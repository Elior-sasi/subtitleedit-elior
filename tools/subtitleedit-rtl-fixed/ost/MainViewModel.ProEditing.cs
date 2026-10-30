using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Data;
using Nikse.SubtitleEdit.Controls;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Core.Forms;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private SubtitleLineViewModel? _stampEndAdvanceTarget;
    private double _stampEndAdvanceSeconds;

    private void AdvanceAfterStampEnd(SubtitleLineViewModel stamped, double seconds)
    {
        var next = GetNextWorkingRow(Subtitles.IndexOf(stamped));
        if (next == null) return;

        // Select now, before another command or a player tick can observe the old row.
        SubtitleGrid.SelectedItems?.Clear();
        SubtitleGrid.SelectedItem = next;
        TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        SubtitleGridSelectionChanged();
        _stampEndAdvanceTarget = next;
        _stampEndAdvanceSeconds = seconds;
        _pausedSelectLastSeconds = seconds;
        SelectAndScrollToRow(next);
    }

    private bool HoldStampEndAdvanceSelection(double seconds)
    {
        if (_stampEndAdvanceTarget == null) return false;
        // End points are inclusive in playhead-follow. At the stamped end it would
        // immediately reselect the row we just finished. A seek or playback movement
        // releases this hold; it never changes the user's follow-selection settings.
        if (ReferenceEquals(SelectedSubtitle, _stampEndAdvanceTarget) &&
            Math.Abs(seconds - _stampEndAdvanceSeconds) <= 0.001)
            return true;

        _stampEndAdvanceTarget = null;
        return false;
    }

    private string? _customPlayerSettingsBaseline;
    internal static bool CustomCanKeepPlayer(string? before, string after)
    {
        if (string.IsNullOrEmpty(before)) return false;
        try
        {
            var a = System.Text.Json.Nodes.JsonNode.Parse(before);
            var b = System.Text.Json.Nodes.JsonNode.Parse(after);
            if (a is null || b is null) return false;
            foreach (var key in new[] { "Appearance", "Video", "Language" })
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(a[key], b[key])) return false;
            foreach (var key in new[] { "LayoutNumber", "UseFrameMode", "UseFrameNumbersPersisted", "CurrentFrameRate" })
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(a["General"]?[key], b["General"]?[key])) return false;
            return true;
        }
        catch { return false; }
    }

    private Dictionary<Guid, (string Text, TimeSpan Start, TimeSpan End)>? _proReviewBaseline;
    private int _proReviewGeneration = -1;

    internal Control MakeProEditingToolbar()
    {
        var flyout = new MenuFlyout();
        void Item(string text, string name, Action action)
        {
            var item = new MenuItem { Name = name, Header = MenuCaption(text), FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetName(item, text);
            item.Click += (_, _) => { try { action(); } catch (Exception e) { ShowStatus(e.Message, 8000); } };
            flyout.Items.Add(item);
        }
        void AsyncItem(string text, string name, Func<Task> action)
        {
            var item = new MenuItem { Name = name, Header = MenuCaption(text), FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetName(item, text);
            item.Click += async (_, _) => { await Task.Yield(); try { await action(); } catch (Exception e) { ShowStatus(e.Message, 8000); } };
            flyout.Items.Add(item);
        }
        Item("כניסה: פריים מוקדם יותר", "ProStartFrameBack", () => ExecuteProFrame("start", -1));
        Item("כניסה: פריים מאוחר יותר", "ProStartFrameForward", () => ExecuteProFrame("start", 1));
        Item("יציאה: פריים מוקדם יותר", "ProEndFrameBack", () => ExecuteProFrame("end", -1));
        Item("יציאה: פריים מאוחר יותר", "ProEndFrameForward", () => ExecuteProFrame("end", 1));
        Item("כל הכתוביות שנבחרו: פריים אחורה", "ProCueFrameBack", () => ExecuteProFrame("cue", -1));
        Item("כל הכתוביות שנבחרו: פריים קדימה", "ProCueFrameForward", () => ExecuteProFrame("cue", 1));
        flyout.Items.Add(new Separator());
        // Keep the editor caret: menu focus does not move it. The native split manager
        // owns markup, Undo and reference handling; no guessed word timestamps here.
        Item("פצל בסמן הטקסט ובמיקום הנוכחי בווידאו", "ProSplitAtCaretAndVideo", () =>
        {
            if (AreTimeCodesLocked || string.IsNullOrEmpty(_videoFileName)) return;
            if (!PrepareProSingleWorkingRow()) return;
            var change = BeginProToolApply("פיצול בסמן ובמיקום הווידאו");
            EndToolApply(change, SplitAtVideoPositionAndTextBoxCursorPosition);
        });
        AsyncItem("אפשרויות פיצול טקסט…", "ProAssistedSplit", () => ExecuteProAssistedEdit(false));
        AsyncItem("אפשרויות העברת מילים לשורה שכנה…", "ProAssistedMove", () => ExecuteProAssistedEdit(true));
        AsyncItem("הגדרת קיצורי מקשים…", "ProFrameShortcuts", CommandShowSettingsShortcuts);
        var info = new MenuItem { Header = MenuCaption(""), IsEnabled = false, Name = "ProFrameRateInfo" };
        flyout.Items.Add(info);
        flyout.Opening += (_, _) =>
        {
            var fps = Se.Settings.General.CurrentFrameRate;
            info.Header = MenuCaption(fps >= 10 ? $"תזמון לפי {fps:0.###} FPS; זמני הכתוביות נשמרים במילישניות" : "קצב פריימים לא זמין: פעולות התזמון משתמשות בברירת המחדל של התוכנה (25 FPS)");
        };
        var button = new Button { Name = "ProFrameToolbarButton", Content = "פריים", FontSize = 16, MinHeight = 36, Flyout = flyout, Focusable = false };
        AutomationProperties.SetName(button, "שינוי תזמוני כתוביות לפי פריים");
        return button;
    }

    internal MenuItem MakeProEditingMenu()
    {
        var button = (Button)MakeProEditingToolbar();
        var flyout = (MenuFlyout)button.Flyout!;
        var items = flyout.Items.ToArray();
        flyout.Items.Clear();
        var menu = new MenuItem { Name = "ProFrameMainMenu", Header = "פריים", FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(menu, "שינוי תזמוני כתוביות לפי פריים");
        foreach (var item in items) menu.Items.Add(item);
        menu.SubmenuOpened += (_, _) =>
        {
            var info = menu.Items.OfType<MenuItem>().First(i => i.Name == "ProFrameRateInfo");
            var fps = Se.Settings.General.CurrentFrameRate;
            info.Header = MenuCaption($"תזמון לפי {(fps >= 10 ? fps : 25):0.###} FPS; זמני הכתוביות נשמרים במילישניות");
        };
        return menu;
    }

    internal Control MakeVideoFrameControls()
    {
        // Video navigation only: VideoOneFrameBack/Forward move the player, never subtitle times.
        var panel = new StackPanel { Name = "CustomVideoFrameControls", Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight, Spacing = 4, Margin = new Avalonia.Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var forward in new[] { false, true })
        {
            var title = forward ? "וידאו: פריים אחד קדימה" : "וידאו: פריים אחד אחורה";
            var button = new NonSpaceButton
            {
                Name = forward ? "VideoFrameForward" : "VideoFrameBack",
                Content = new Optris.Icons.Avalonia.Icon { Value = forward ? "fa-solid fa-forward-step" : "fa-solid fa-backward-step", Width = 18, Height = 18, FlowDirection = FlowDirection.LeftToRight },
                Padding = new Avalonia.Thickness(6, 3), MinWidth = 28, MinHeight = 28, FlowDirection = FlowDirection.LeftToRight,
                Command = forward ? VideoOneFrameForwardCommand : VideoOneFrameBackCommand,
            };
            AutomationProperties.SetName(button, title);
            button.Bind(Button.IsEnabledProperty, new Binding(nameof(IsVideoLoaded)) { Source = this });
            ToolTip.SetTip(button, new TextBlock { Text = title, FontSize = 16, FlowDirection = FlowDirection.RightToLeft });
            button.Click += (_, _) => button.SetValue(ToolTip.IsOpenProperty, false);
            button.PointerExited += (_, _) => button.SetValue(ToolTip.IsOpenProperty, false);
            panel.Children.Add(button);
        }
        return panel;
    }

    internal void ExecuteProFrame(string target, int direction)
    {
        if (direction != -1 && direction != 1) throw new ArgumentOutOfRangeException(nameof(direction));
        if (target == "video")
        {
            if (string.IsNullOrEmpty(_videoFileName)) { ShowStatus("פתח וידאו לפני מעבר בין פריימים.", 5000); return; }
            // Native decoding/frame stepping; never touches the subtitle selection or times.
            if (direction < 0) VideoOneFrameBack(); else VideoOneFrameForward();
            return;
        }
        if (target != "start" && target != "end" && target != "cue") throw new ArgumentException("Unknown frame target", nameof(target));
        if (AreTimeCodesLocked) { ShowStatus("התזמונים נעולים.", 5000); return; }
        TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        if (SubtitleGridSelectedCount == 0) return;
        var selection = SubtitleGridSelectedItems.Cast<SubtitleLineViewModel>().ToList();
        if (selection.Any(r => r.IsReferenceOnly || r.IsDefault || r.IsComment)) { ShowStatus("בחר כתוביות עבודה בלבד.", 5000); return; }
        if (target != "cue")
        {
            if (selection.Count != 1) { ShowStatus("לשינוי כניסה או יציאה בחר כתובית אחת.", 5000); return; }
            SelectedSubtitle = selection[0]; SelectedSubtitleIndex = Subtitles.IndexOf(selection[0]);
        }
        var change = BeginProToolApply("הזזה בפריים: " + (target == "start" ? "כניסה" : target == "end" ? "יציאה" : "הכתוביות שנבחרו"));
        EndToolApply(change, () =>
        {
            switch (target)
            {
                case "start": if (direction < 0) MoveStartOneFrameBack(); else MoveStartOneFrameForward(); break;
                case "end": if (direction < 0) MoveEndOneFrameBack(); else MoveEndOneFrameForward(); break;
                case "cue": if (direction < 0) MoveSelectedLinesOneFrameBack(); else MoveSelectedLinesOneFrameForward(); break;
            }
        });
        RefreshOstMarkers();
    }

    private ToolApply BeginProToolApply(string name)
    {
        // A first edit can run before the polling change detector has recorded the
        // loaded/current rows. Preserve that state explicitly, as in edition sync.
        var before = MakeUndoRedoObject("לפני " + name);
        if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != before.Hash) _undoRedoManager.Do(before);
        return BeginToolApply(name);
    }

    private bool PrepareProSingleWorkingRow()
    {
        TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        var rows = SubtitleGridSelectedItems.Cast<SubtitleLineViewModel>().ToList();
        if (rows.Count != 1 || rows[0].IsReferenceOnly || rows[0].IsDefault || rows[0].IsComment)
        { ShowStatus("בחר כתובית עבודה אחת.", 5000); return false; }
        SelectedSubtitle = rows[0]; SelectedSubtitleIndex = Subtitles.IndexOf(rows[0]); return true;
    }

    internal async Task ExecuteProAssistedEdit(bool move)
    {
        if (AreTimeCodesLocked) { ShowStatus("התזמונים נעולים. פיצול או העברת מילים עשויים לשנות תזמונים.", 6000); return; }
        if (!PrepareProSingleWorkingRow()) return;
        var change = BeginProToolApply(move ? "העברת מילים לשורה שכנה" : "פיצול טקסט באישור");
        if (move) await AssistedMove(); else await AssistedSplit();
        EndToolApply(change, () => { });
    }

    internal void SetProReviewBaseline()
    {
        _proReviewBaseline = ProWorkingRows().ToDictionary(r => r.Id, r => (r.Text ?? "", r.StartTime, r.EndTime));
        _proReviewGeneration = _ostDocumentGeneration;
        ShowStatus("נקודת בדיקה נקבעה לזיכרון ההפעלה הנוכחית. שינויים מכאן יופיעו בסקירה הממוקדת.", 7000);
    }

    private List<SubtitleLineViewModel> ProWorkingRows() => Subtitles.Where(r => !r.IsReferenceOnly && !r.IsDefault && !r.IsComment).ToList();

    internal List<SubtitleLineViewModel> GetProReviewRows(string mode)
    {
        var rows = ProWorkingRows();
        if (mode == "qc") { ScanCustomQc(); var ids = CustomQcIssues.Select(i => i.RowId).ToHashSet(); return rows.Where(r => ids.Contains(r.Id)).ToList(); }
        if (mode == "marked") { MonitorCustomProject(); return rows.Where(r => r.CustomWorkMarker.Contains('◇') || r.CustomWorkMarker.Contains('?')).ToList(); }
        if (mode == "changed")
        {
            if (_proReviewBaseline == null || _proReviewGeneration != _ostDocumentGeneration) throw new InvalidOperationException("קבע תחילה נקודת בדיקה במסמך הנוכחי. אין היסטוריית שינויים שנאספה לפני כן.");
            return rows.Where(r => !_proReviewBaseline.TryGetValue(r.Id, out var old) || old != (r.Text ?? "", r.StartTime, r.EndTime)).ToList();
        }
        if (mode != "selected") throw new ArgumentException("Unknown review mode", nameof(mode));
        TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        var selected = SubtitleGridSelectedItems.Cast<SubtitleLineViewModel>().Select(r => r.Id).ToHashSet();
        return rows.Where(r => selected.Contains(r.Id)).ToList();
    }

    private Task OpenProFocusedReview() => ProjectDialog("סקירה ממוקדת — ניגון בלי הרווחים בין הכתוביות", (panel, dialog) =>
    {
        var modes = new[] { "selected", "qc", "marked", "changed" };
        var choice = new ComboBox { ItemsSource = new[] { "השורות שנבחרו", "שורות עם אזהרות בדיקה", "שורות שסומנו לבדיקה או לשאלת במאי", "שורות ששונו מאז נקודת הבדיקה" }, SelectedIndex = 0, FontSize = 16 };
        panel.Children.Add(Label("הסקירה משתמשת בזמנים הקיימים ואינה משנה טקסט או תזמון. רצף ניגון מדלג על רווחים; אפשר גם לבדוק שורה אחת בכל פעם."));
        panel.Children.Add(choice);
        var status = Label(""); var results = new StackPanel { Spacing = 6 }; panel.Children.Add(status);
        List<SubtitleLineViewModel> queue = new(); var generation = -1; var hash = 0; var media = "";
        bool Fresh() => generation == _ostDocumentGeneration && hash == GetFastHash() && media == (_videoFileName ?? "");
        void Load()
        {
            StopReviewLinePlayback(); queue = GetProReviewRows(modes[Math.Max(0, choice.SelectedIndex)]);
            if (queue.Count > 5000) throw new InvalidOperationException("בחר עד 5,000 כתוביות לסקירה אחת.");
            generation = _ostDocumentGeneration; hash = GetFastHash(); media = _videoFileName ?? "";
            results.Children.Clear(); status.Text = $"{queue.Count} כתוביות בסקירה. הרשימה אינה מסמנת אותן כנבדקו.";
            foreach (var row in queue.Take(500))
                ActionButton(results, $"{row.Number}: {row.Text}", () => { if (!Fresh()) throw new InvalidOperationException("המסמך או המדיה השתנו. טען את הרשימה מחדש."); var player = MakeLinePlayer(); if (player == null) throw new InvalidOperationException("פתח מדיה לפני ניגון."); SelectAndScrollToRow(row); player(row); return Task.CompletedTask; });
            if (queue.Count > 500) results.Children.Add(Label("מוצגים 500 הכפתורים הראשונים; ניגון רצף כולל את כל הרשימה."));
        }
        ActionButton(panel, "טען / רענן רשימת סקירה", () => { Load(); return Task.CompletedTask; });
        choice.SelectionChanged += (_, _) => { StopReviewLinePlayback(); queue.Clear(); results.Children.Clear(); status.Text = "טען רשימה לפי הסינון החדש."; };
        ActionButton(panel, "נגן את כל הרשימה ברצף", () =>
        {
            if (!Fresh()) throw new InvalidOperationException("טען רשימה עדכנית לפני ניגון.");
            var vp = GetVideoPlayerControl();
            if (queue.Count == 0 || vp == null || string.IsNullOrEmpty(_videoFileName)) throw new InvalidOperationException("נדרשות מדיה ורשימת כתוביות לסקירה.");
            var ordered = queue.OrderBy(r => r.StartTime).ToList();
            vp.VideoPlayer.Pause(); SelectAndScrollToRow(ordered[0]);
            SeekVideoPlayer(vp, ordered[0].StartTime.TotalSeconds); PinPlayheadTo(ordered[0].StartTime.TotalSeconds);
            _playSelectionItem = new PlaySelectionItem(ordered, ordered[0].EndTime, false); PlayVideo(vp);
            return Task.CompletedTask;
        });
        ActionButton(panel, "עצור סקירה", () => { StopReviewLinePlayback(); return Task.CompletedTask; });
        panel.Children.Add(results);
        var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        guard.Tick += (_, _) => { if (queue.Count > 0 && !Fresh()) { StopReviewLinePlayback(); queue.Clear(); results.Children.Clear(); status.Text = "המסמך או המדיה השתנו. הסקירה נעצרה; טען רשימה חדשה."; } };
        dialog.Opened += (_, _) => guard.Start();
        dialog.Closed += (_, _) => { guard.Stop(); StopReviewLinePlayback(); };
    });

    private Task OpenProShotPreview()
    {
        if (AreTimeCodesLocked || AudioVisualizer == null || AudioVisualizer.ShotChanges.Count == 0 || string.IsNullOrEmpty(_videoFileName))
        { ShowStatus("נדרשים וידאו, רשימת חילופי שוטים ותזמונים לא נעולים. טען או צור חילופי שוטים בכלי הווידאו.", 8000); return Task.CompletedTask; }
        TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        var selected = SubtitleGridSelectedItems.Cast<SubtitleLineViewModel>().Where(r => !r.IsReferenceOnly && !r.IsComment && !r.IsDefault).ToList();
        if (selected.Count == 0 || selected.Count > 5000) { ShowStatus("בחר בין כתובית אחת ל־5,000 כתוביות.", 6000); return Task.CompletedTask; }
        var generation = _ostDocumentGeneration; var hash = GetFastHash(); var media = _videoFileName;
        var shots = AudioVisualizer.ShotChanges.ToList(); var offset = Se.Settings.General.CurrentVideoOffsetInMs;
        var proposals = selected.Select(row =>
        {
            var snap = ShotChangesHelper.GetSnappedToNearestMs(shots, row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds,
                TimeCodesBeautifierUtils.GetInCuesGapMs(), TimeCodesBeautifierUtils.GetOutCuesGapMs(),
                Se.Settings.Waveform.SnapToShotChangeStartMaxSeconds * 1000, Se.Settings.Waveform.SnapToShotChangeEndMaxSeconds * 1000,
                Se.Settings.Waveform.SnapToShotChangeSameShotEndMaxSeconds * 1000);
            return snap == null ? null : new ProTimingProposal(row.Id, row.Text ?? "", row.StartTime, row.EndTime, TimeSpan.FromMilliseconds(snap.Value.StartMs), TimeSpan.FromMilliseconds(snap.Value.EndMs));
        }).Where(p => p != null && (p.Start != p.BeforeStart || p.End != p.BeforeEnd)).Cast<ProTimingProposal>().ToList();
        return ProjectDialog("הצמדה לחילופי שוטים — אישור הצעות", (panel, dialog) =>
        {
            panel.Children.Add(Label($"{proposals.Count} הצעות לפי מרחקי ההצמדה הפעילים של התוכנה. אין זיהוי דיבור כאן; השוטים עצמם אינם מבטיחים גבול דיבור. כל הצעה דורשת סימון מפורש."));
            var checks = proposals.Select(p => (Proposal: p, Check: new CheckBox { IsChecked = false, Content = Label($"{p.Text}\n{p.BeforeStart:c} — {p.BeforeEnd:c}\n← {p.Start:c} — {p.End:c}"), FontSize = 16 })).ToList();
            foreach (var c in checks) { AutomationProperties.SetName(c.Check, "אישור הצעת תזמון: " + c.Proposal.Text); panel.Children.Add(c.Check); }
            ActionButton(panel, "החל רק את ההצעות שסימנתי", () =>
            {
                if (media != _videoFileName || offset != Se.Settings.General.CurrentVideoOffsetInMs || AudioVisualizer == null || !shots.SequenceEqual(AudioVisualizer.ShotChanges)) throw new InvalidOperationException("המדיה, ההיסט או חילופי השוטים השתנו. פתח תצוגה חדשה.");
                ApplyProTiming(checks.Where(c => c.Check.IsChecked == true).Select(c => c.Proposal).ToArray(), generation, hash);
                dialog.Close(); return Task.CompletedTask;
            });
        });
    }

    internal void ApplyProTiming(IReadOnlyList<ProTimingProposal> proposals, int generation, int hash)
    {
        if (AreTimeCodesLocked || generation != _ostDocumentGeneration || hash != GetFastHash()) throw new InvalidOperationException("התזמונים נעולים או שהמסמך השתנה. צור הצעות חדשות.");
        var rows = ProWorkingRows(); var byId = rows.ToDictionary(r => r.Id);
        var changes = proposals.ToDictionary(p => p.Id);
        if (rows.Count > 20000 || proposals.Count > 5000) throw new InvalidOperationException("בחר מסמך קטן יותר לבדיקת הצעות.");
        foreach (var p in proposals)
        {
            if (!byId.TryGetValue(p.Id, out var row) || row.Text != p.Text || row.StartTime != p.BeforeStart || row.EndTime != p.BeforeEnd || p.Start < TimeSpan.Zero || p.End <= p.Start)
                throw new InvalidOperationException("הצעה אינה מתאימה עוד לשורת המקור או מכילה זמנים לא תקינים.");
            // Include non-adjacent rows: existing nested ASS events must not conceal a
            // newly introduced overlap. At most 5,000 x 20,000 bounded comparisons.
            foreach (var other in rows)
            {
                if (other.Id == p.Id) continue;
                var start = changes.TryGetValue(other.Id, out var c) ? c.Start : other.StartTime;
                var end = c?.End ?? other.EndTime;
                if (p.Start < end && start < p.End && !(p.BeforeStart < other.EndTime && other.StartTime < p.BeforeEnd))
                    throw new InvalidOperationException("הבחירה יוצרת חפיפה חדשה. שנה את בחירת ההצעות.");
            }
        }
        if (proposals.Count == 0) return;
        var change = BeginProToolApply("הצמדה מאושרת לחילופי שוטים");
        EndToolApply(change, () => { foreach (var p in proposals) byId[p.Id].SetTimes(p.Start, p.End); });
        RefreshOstMarkers(); MonitorCustomProject();
    }

    private Task OpenProWidthReview() => ProjectDialog("בדיקת רוחב לפי גופן ואזור בטוח", (panel, dialog) =>
    {
        panel.Children.Add(Label($"מדידה בגופן {Se.Settings.General.ColorTextTooWideFontName}, גודל {Se.Settings.General.ColorTextTooWideFontSize}. בחר גופן וגודל בהגדרות רוחב הטקסט של התוכנה. זו מדידת טקסט משוערת, לא בדיקת תמונה מרונדרת או מיקום ASS."));
        var screen = new NumericUpDown { Minimum = 320, Maximum = 16384, Value = 1920, FontSize = 16 };
        var percent = new NumericUpDown { Minimum = 10, Maximum = 100, Value = 80, FontSize = 16 };
        panel.Children.Add(Label("רוחב תמונת המסירה בפיקסלים")); panel.Children.Add(screen);
        panel.Children.Add(Label("אחוז הרוחב המותר לטקסט")); panel.Children.Add(percent);
        var result = new StackPanel { Spacing = 6 };
        ActionButton(panel, "בדוק בלי לשנות טקסט או הגדרות", () =>
        {
            result.Children.Clear(); var limit = (double)(screen.Value ?? 1920) * (double)(percent.Value ?? 80) / 100;
            var rows = ProWorkingRows(); if (rows.Count > 20000) throw new InvalidOperationException("בחר מסמך עד 20,000 כתוביות.");
            var issues = rows.Select(r => (Row: r, Width: r.MeasureProPixelWidth())).Where(x => x.Width > limit).ToList();
            result.Children.Add(Label($"מגבלת רוחב: {limit:0} פיקסלים. {issues.Count} כתוביות חורגות; מוצגות עד 500. הטקסט לא השתנה."));
            foreach (var issue in issues.Take(500)) ActionButton(result, $"{issue.Row.Number}: {issue.Width} פיקסלים — {issue.Row.Text}", () => { if (!Subtitles.Contains(issue.Row)) throw new InvalidOperationException("השורה כבר אינה במסמך."); SelectAndScrollToRow(issue.Row); dialog.Close(); return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        panel.Children.Add(result);
    });
}

internal sealed record ProTimingProposal(Guid Id, string Text, TimeSpan BeforeStart, TimeSpan BeforeEnd, TimeSpan Start, TimeSpan End);

