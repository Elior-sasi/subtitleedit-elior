using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private Task OpenVideoEditionSync()
    {
        if (AreTimeCodesLocked) { ShowStatus("בטל נעילת תזמונים לפני התאמה לגרסת וידאו אחרת.", 6000); return Task.CompletedTask; }
        return ProjectDialog("התאם כתוביות לגרסת וידאו אחרת", (panel, dialog) =>
        {
            var oldFile = _videoFileName ?? ""; var newFile = ""; var busy = false;
            CancellationTokenSource? running = null;
            dialog.Closed += (_, _) => running?.Cancel();
            panel.Children.Add(Label("בחר שני קבצים של אותו סרט עם שמע באותה שפה. ההתאמה משווה מקטעי שמע מקומית, ללא העלאה לענן. מיקס שונה, מוזיקה חוזרת, שינוי מהירות או דיבוב אחר עלולים למנוע התאמה. זו הצעה לבדיקה, לא זיהוי ודאי."));
            panel.Children.Add(Label("הכתוביות הפתוחות צריכות להיות מתוזמנות לגרסה הישנה. המקור נשאר פתוח; אחרי אישור ושמירה בשם חדש, פתח את הווידאו החדש ובדוק את התוצאה."));
            var oldLabel = Label("הישן: " + oldFile); var newLabel = Label("החדש: טרם נבחר"); panel.Children.Add(oldLabel); panel.Children.Add(newLabel);
            var oldTrack = new NumericUpDown { Minimum = 1, Maximum = 32, Value = 1, FontSize = 16 };
            var newTrack = new NumericUpDown { Minimum = 1, Maximum = 32, Value = 1, FontSize = 16 };
            panel.Children.Add(Label("מספר ערוץ השמע בישן (1 = הראשון)")); panel.Children.Add(oldTrack);
            panel.Children.Add(Label("מספר ערוץ השמע בחדש (1 = הראשון)")); panel.Children.Add(newTrack);
            var results = new StackPanel { Spacing = 8 }; var status = Label("");
            var checkedRows = new List<(CheckBox Check, VideoEditionSync.Proposal Proposal)>();
            var document = ""; var generation = -1; var hash = 0; double offset = 0;
            void Clear() { results.Children.Clear(); checkedRows.Clear(); }
            oldTrack.ValueChanged += (_, _) => { if (!busy) Clear(); };
            newTrack.ValueChanged += (_, _) => { if (!busy) Clear(); };
            ActionButton(panel, "בחר וידאו ישן…", async () => { if (busy) return; var picked = await _fileHelper.PickOpenVideoFile(dialog, "בחר וידאו ישן"); if (!string.IsNullOrEmpty(picked)) { oldFile = picked; oldLabel.Text = "הישן: " + picked; Clear(); } });
            ActionButton(panel, "בחר וידאו חדש…", async () => { if (busy) return; var picked = await _fileHelper.PickOpenVideoFile(dialog, "בחר וידאו חדש"); if (!string.IsNullOrEmpty(picked)) { newFile = picked; newLabel.Text = "החדש: " + picked; Clear(); } });
            ActionButton(panel, "נתח והצג הצעות — ללא שינוי כתוביות", async () =>
            {
                if (busy) return;
                if (!File.Exists(oldFile) || !File.Exists(newFile) || string.Equals(Path.GetFullPath(oldFile), Path.GetFullPath(newFile), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("בחר שני קובצי וידאו שונים.");
                if (!await RequireFfmpegOk()) return;
                busy = true; Clear(); oldTrack.IsEnabled = newTrack.IsEnabled = false;
                document = _subtitleFileName ?? ""; generation = _ostDocumentGeneration; hash = GetFastHash(); offset = Se.Settings.General.CurrentVideoOffsetInMs;
                var cues = EditionCues(); var firstTrack = (int)(oldTrack.Value ?? 1) - 1; var secondTrack = (int)(newTrack.Value ?? 1) - 1;
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30)); running = cancel;
                try
                {
                    var ffmpeg = FfmpegHelper.GetFfmpegLocation();
                    status.Text = "קורא את השמע בגרסה הישנה…";
                    var source = await VideoEditionSync.ReadAudio(ffmpeg, oldFile, firstTrack, cancel.Token);
                    status.Text = "קורא את השמע בגרסה החדשה…";
                    var target = await VideoEditionSync.ReadAudio(ffmpeg, newFile, secondTrack, cancel.Token);
                    var progress = new Progress<int>(v => { if (busy && !cancel.IsCancellationRequested) status.Text = $"משווה מקטעי שמע: {v}%"; });
                    var anchors = await Task.Run(() => VideoEditionSync.Match(source, target, cancel.Token, progress), cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    if (generation != _ostDocumentGeneration || document != (_subtitleFileName ?? "") || hash != GetFastHash()) throw new InvalidOperationException("המסמך השתנה במהלך הניתוח. נתח שוב.");
                    var segments = VideoEditionSync.Segments(anchors); var proposals = VideoEditionSync.Propose(cues, segments, offset);
                    status.Text = $"נמצאו {segments.Count} מקטעי התאמה ו־{proposals.Count} הצעות לשינוי תזמון. {cues.Length - proposals.Count} כתוביות ללא הצעה: לא נדרשת הזזה, אין התאמה או דרושה בדיקה ידנית. דבר לא הוחל.";
                    foreach (var proposal in proposals)
                    {
                        var row = Subtitles.First(s => s.Id == proposal.Before.Id);
                        var check = new CheckBox { FontSize = 16, IsChecked = false, Content = Label($"כתובית {row.Number}: {proposal.Before.Text}\n{proposal.Before.Start:c} – {proposal.Before.End:c}\n← {proposal.Start:c} – {proposal.End:c}") };
                        AutomationProperties.SetName(check, $"אשר שינוי תזמון לכתובית {row.Number}");
                        results.Children.Add(check); checkedRows.Add((check, proposal));
                    }
                }
                catch (OperationCanceledException) { status.Text = "הניתוח בוטל. הכתוביות לא השתנו."; }
                finally { busy = false; oldTrack.IsEnabled = newTrack.IsEnabled = true; running = null; }
            });
            ActionButton(panel, "בטל ניתוח", () => { running?.Cancel(); return Task.CompletedTask; });
            panel.Children.Add(status);
            ActionButton(panel, "סמן את כל ההצעות לבדיקה ולהחלה", () => { if (!busy) foreach (var item in checkedRows) item.Check.IsChecked = true; return Task.CompletedTask; });
            ActionButton(panel, "בטל את כל הסימונים", () => { foreach (var item in checkedRows) item.Check.IsChecked = false; return Task.CompletedTask; });
            panel.Children.Add(results);
            ActionButton(panel, "החל רק שינויים שסימנתי", () =>
            {
                if (busy) return Task.CompletedTask;
                var selected = checkedRows.Where(x => x.Check.IsChecked == true).Select(x => x.Proposal).ToArray();
                if (selected.Length == 0) { status.Text = "סמן תחילה הצעה שברצונך להחיל."; return Task.CompletedTask; }
                ApplyVideoEdition(selected, document, generation, hash, offset); dialog.Close(); return Task.CompletedTask;
            });
        });
    }

    private VideoEditionSync.Cue[] EditionCues() => Subtitles.Where(s => !s.IsReferenceOnly && !s.IsDefault && !s.IsComment)
        .Select(s => new VideoEditionSync.Cue(s.Id, s.Text, s.StartTime, s.EndTime)).ToArray();

    internal void ApplyVideoEdition(IReadOnlyList<VideoEditionSync.Proposal> proposals, string document, int generation, int hash, double offset)
    {
        if (AreTimeCodesLocked || generation != _ostDocumentGeneration || document != (_subtitleFileName ?? "") || hash != GetFastHash() || offset != Se.Settings.General.CurrentVideoOffsetInMs)
            throw new InvalidOperationException("התזמונים נעולים או שהמסמך השתנה. נתח שוב לפני החלה.");
        var changes = proposals.ToDictionary(p => p.Before.Id);
        var cues = EditionCues();
        foreach (var p in proposals)
        {
            if (!cues.Contains(p.Before) || p.Start < TimeSpan.Zero || p.End <= p.Start || p.End - p.Start != p.Before.End - p.Before.Start)
                throw new InvalidOperationException("ההצעה אינה מתאימה עוד לכתובית המקורית.");
        }
        // Partial selection must not introduce new overlaps at a segment boundary.
        for (var i = 1; i < cues.Length; i++)
        {
            var a = cues[i - 1]; var b = cues[i];
            var end = changes.TryGetValue(a.Id, out var ap) ? ap.End : a.End;
            var start = changes.TryGetValue(b.Id, out var bp) ? bp.Start : b.Start;
            if (a.End <= b.Start && end > start) throw new InvalidOperationException("הבחירה יוצרת חפיפה חדשה בין כתוביות. בדוק את גבול המקטע ושנה את הבחירה.");
        }
        if (proposals.Count == 0) return;
        var before = MakeUndoRedoObject("לפני התאמת גרסת וידאו");
        if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != before.Hash) _undoRedoManager.Do(before);
        var change = BeginToolApply("התאם כתוביות לגרסת וידאו אחרת");
        EndToolApply(change, () => { foreach (var row in Subtitles) if (changes.TryGetValue(row.Id, out var p)) { row.StartTime = p.Start; row.EndTime = p.End; } });
        RefreshOstMarkers(); MonitorCustomProject();
    }
}

