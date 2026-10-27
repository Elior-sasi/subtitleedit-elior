using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Features.Video.SpeechToText;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private CcTiming.Cue[] CcTimingCues() => Subtitles.Where(s => !s.IsReferenceOnly && !s.IsDefault && !s.IsComment)
        .Select(s => new CcTiming.Cue(s.Id, s.Text, s.StartTime, s.EndTime)).ToArray();

    private Task OpenCcTiming()
    {
        if (AreTimeCodesLocked) { ShowStatus("בטל נעילת תזמונים תחילה."); return Task.CompletedTask; }
        var document = _subtitleFileName ?? ""; var generation = _ostDocumentGeneration; var hash = GetFastHash();
        var media = _videoFileName ?? ""; var offset = Se.Settings.General.CurrentVideoOffsetInMs;
        var source = CcTimingCues();
        if (source.Length == 0) { ShowStatus("פתח כתוביות לפני תיקון תזמוני CC."); return Task.CompletedTask; }
        return ProjectDialog("תיקון תזמוני CC לפי הדיבור — ללא שינוי טקסט", (panel, dialog) =>
        {
            bool busy = false, closed = false; CancellationTokenSource? running = null;
            dialog.Closed += (_, _) => { closed = true; running?.Cancel(); };
            var transcript = new List<ScriptTranscriptReview.Cue>();
            var checks = new List<(CheckBox Check, CcTiming.Proposal Proposal)>();
            var results = new StackPanel { Spacing = 8 }; var status = Label("טרם נטען תמלול.");
            var window = new NumericUpDown { Minimum = 1, Maximum = 120, Value = 30, FontSize = 16 };
            var changeEnd = new CheckBox { Content = Label("תקן גם זמן סיום — ברירת המחדל מתקנת התחלה בלבד"), IsChecked = false };
            var sameLanguage = new CheckBox { Content = Label("אישרתי: התמלול והכתוביות באותה שפה והתמלול מתאים בדיוק לקובץ המדיה הנוכחי"), IsChecked = false };
            var mediaInfo = File.Exists(media) ? new FileInfo(media) : null;
            var mediaLength = mediaInfo?.Length; var mediaWrite = mediaInfo?.LastWriteTimeUtc;
            bool Fresh() => document == (_subtitleFileName ?? "") && generation == _ostDocumentGeneration && hash == GetFastHash()
                && media == (_videoFileName ?? "") && offset == Se.Settings.General.CurrentVideoOffsetInMs
                && (mediaInfo == null || (File.Exists(media) && new FileInfo(media).Length == mediaLength && new FileInfo(media).LastWriteTimeUtc == mediaWrite));
            void Clear() { checks.Clear(); results.Children.Clear(); }
            void SetTranscript(List<ScriptTranscriptReview.Cue> cues)
            {
                ScriptTranscriptReview.Validate(cues); transcript = cues; Clear(); sameLanguage.IsChecked = false;
                status.Text = $"נטענו {cues.Count} שורות תמלול. ודא שפה, מדיה וגבולות מול השמע.";
            }
            panel.Children.Add(Label("לתיקון הקדמה או איחור המשתנים בין כתוביות באותו פרק. התמלול חייב להיות באותה שפה, לא תרגום. התאמה מילולית מלאה בלבד, בין כתובית שלמה ל־1–4 שורות תמלול שלמות; אין כאן יישור מילים מדויק. משפטים קצרים, חזרות ותיאורי צלילים עשויים להישאר ללא הצעה."));
            panel.Children.Add(Label("הזמנים מגיעים מגבולות התמלול ועלולים להיות שגויים או מוקדמים בעצמם. בדוק מול השמע לפני סימון. אין שינוי טקסט, חלוקה, תגי עיצוב, מזהים או הערות. אין אישור אוטומטי."));
            panel.Children.Add(Label("וידאו נוכחי: " + media));
            ActionButton(panel, "טען תמלול מתוזמן SRT מאותו פרק…", async () =>
            {
                if (busy) return;
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("תמלול SRT") { Patterns = new[] { "*.srt" } } } });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null || closed) return;
                if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("בחר SRT עד 20 MB.");
                var text = await File.ReadAllTextAsync(path); if (!closed) SetTranscript(ScriptTranscriptReview.ReadSrt(text));
            });
            ActionButton(panel, "הפק תמלול מקומי — בחר את שפת הדיבור…", async () =>
            {
                if (busy) return;
                if (!Fresh() || !File.Exists(media)) throw new InvalidOperationException("טען את הווידאו המתאים ופתח שוב את הכלי.");
                if (!await RequireFfmpegOk()) return;
                busy = true;
                try
                {
                    var vm = await _windowService.ShowDialogAsync<SpeechToTextWindow, SpeechToTextViewModel>(dialog, v => v.InitializeCustomScriptReview(media));
                    if (closed || !vm.OkPressed) return;
                    if (!Fresh() || vm.DoTranslateToEnglish) throw new InvalidOperationException("המסמך או המדיה השתנו, או שנבחר תרגום. הפק תמלול באותה שפה.");
                    SetTranscript(vm.TranscribedSubtitle.Paragraphs.Select(p => new ScriptTranscriptReview.Cue(p.StartTime.TotalMilliseconds, p.EndTime.TotalMilliseconds, p.Text)).ToList());
                }
                finally { busy = false; }
            });
            panel.Children.Add(Label("בתמלול המקומי ברירת המחדל היא עברית. שנה לשפת הדיבור ובחר מודל מתאים; הורדות מנוע ומודל מתבצעות במחשב שלך. ההעדפות הקיימות אינן מוחלפות."));
            panel.Children.Add(sameLanguage); panel.Children.Add(Label("מרחק חיפוש מרבי מזמן ההתחלה הקיים, בשניות")); panel.Children.Add(window); panel.Children.Add(changeEnd);
            window.ValueChanged += (_, _) => Clear(); changeEnd.IsCheckedChanged += (_, _) => Clear(); sameLanguage.IsCheckedChanged += (_, _) => Clear();
            ActionButton(panel, "נתח והצג הצעות לתזמון בלבד", async () =>
            {
                if (busy) return;
                Clear();
                if (!Fresh() || sameLanguage.IsChecked != true) throw new InvalidOperationException("אשר שפה ומדיה, ופתח מחדש אם המסמך השתנה.");
                var search = (double)(window.Value ?? 30) * 1000; var ends = changeEnd.IsChecked == true;
                busy = true; window.IsEnabled = changeEnd.IsEnabled = sameLanguage.IsEnabled = false;
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2)); running = cancel;
                try
                {
                    var proposed = await Task.Run(() => CcTiming.Propose(source, transcript, search, offset, ends, cancel.Token), cancel.Token);
                    if (closed || !Fresh()) return;
                    foreach (var p in proposed)
                    {
                        var check = new CheckBox { IsChecked = false, Content = Label($"{p.Before.Text}\n{p.Before.Start:c} – {p.Before.End:c}\nהצעה: {p.Start:c} – {p.End:c} | שינוי התחלה: {(p.Start - p.Before.Start).TotalSeconds:+0.000;-0.000;0} שניות") };
                        AutomationProperties.SetName(check, "אשר תזמון לאחר בדיקה: " + p.Before.Text);
                        results.Children.Add(check); checks.Add((check, p));
                    }
                    status.Text = $"{proposed.Count} הצעות מתוך {source.Length} כתוביות. יתר הכתוביות נשארות ללא שינוי. כל הצעה דורשת בדיקה וסימון.";
                }
                catch (OperationCanceledException) { status.Text = "הניתוח בוטל; דבר לא השתנה."; }
                finally { busy = false; running = null; window.IsEnabled = changeEnd.IsEnabled = sameLanguage.IsEnabled = true; }
            });
            ActionButton(panel, "בטל ניתוח", () => { running?.Cancel(); return Task.CompletedTask; });
            panel.Children.Add(status); panel.Children.Add(results);
            ActionButton(panel, "החל רק הצעות שבדקתי וסימנתי", () =>
            {
                if (busy) return Task.CompletedTask;
                if (!Fresh() || sameLanguage.IsChecked != true) throw new InvalidOperationException("המסמך או המדיה השתנו. פתח מחדש.");
                var selected = checks.Where(x => x.Check.IsChecked == true).Select(x => x.Proposal).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException("סמן הצעות לאחר בדיקה מול השמע.");
                ApplyCcTiming(selected, document, generation, hash, media, offset); dialog.Close(); return Task.CompletedTask;
            });
        });
    }

    internal void ApplyCcTiming(IReadOnlyList<CcTiming.Proposal> proposals, string document, int generation, int hash, string media, double offset)
    {
        if (AreTimeCodesLocked || document != (_subtitleFileName ?? "") || generation != _ostDocumentGeneration || hash != GetFastHash() || media != (_videoFileName ?? "") || offset != Se.Settings.General.CurrentVideoOffsetInMs)
            throw new InvalidOperationException("התזמונים נעולים או שהמסמך והמדיה השתנו. נתח מחדש.");
        CcTiming.ValidateApply(CcTimingCues(), proposals);
        if (proposals.Count == 0) return;
        var changes = proposals.ToDictionary(p => p.Before.Id);
        var before = MakeUndoRedoObject("לפני תיקון תזמוני CC");
        if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != before.Hash) _undoRedoManager.Do(before);
        var change = BeginToolApply("תיקון תזמוני CC לפי הדיבור");
        EndToolApply(change, () => { foreach (var row in Subtitles) if (changes.TryGetValue(row.Id, out var p)) { row.StartTime = p.Start; row.EndTime = p.End; } });
        RefreshOstMarkers(); MonitorCustomProject();
    }
}

