using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Features.Video.SpeechToText;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private async Task OpenScriptTranscriptReview(string initialScript = "", string initialMedia = "")
    {
        if (Window == null) return;
        var document = _subtitleFileName; var generation = _ostDocumentGeneration; var hash = GetFastHash();
        List<ScriptTranscriptReview.Cue>? approved = null;
        var media = string.IsNullOrEmpty(initialMedia) ? _videoFileName ?? "" : initialMedia;
        await ProjectDialog("תסריט שונה מהדיבור — השוואה ואישור", (panel, dialog) =>
        {
            dialog.Name = "ScriptReviewDialog";
            var actions = PinProjectActions(dialog);
            var preparation = new StackPanel { Spacing = 8 }; var review = new StackPanel { Spacing = 8 }; var final = new StackPanel { Spacing = 8 };
            var stages = new TabControl { Name = "ScriptReviewStages", SelectedIndex = 0, ItemsSource = new[] {
                new TabItem { Header = Label("1. קבצים"), Content = preparation },
                new TabItem { Header = Label("2. בדיקת שורות"), Content = review },
                new TabItem { Header = Label("3. סיכום וייבוא"), Content = final } } };
            panel.Children.Add(Label("טען תסריט ותמלול באותה שפה, בדוק כל שורה ואז אשר ייבוא למסמך חדש.")); panel.Children.Add(stages);
            var closed = false; var busy = false; CancellationTokenSource? running = null;
            dialog.Closed += (_, _) => { closed = true; running?.Cancel(); };
            var transcript = new List<ScriptTranscriptReview.Cue>();
            string[] script = Array.Empty<string>();
            ScriptTranscriptReview.Suggestion?[] proposals = Array.Empty<ScriptTranscriptReview.Suggestion?>();
            var decisions = new Dictionary<int, ScriptTranscriptReview.Decision>();
            var help = new StackPanel { Spacing = 8 };
            var helpSection = DialogDetails(preparation, "איך מכינים ומה חשוב לבדוק?", help); helpSection.Name = "ScriptReviewHelp";
            help.Children.Add(Label("שמע ותסריט באותה שפה, עם הבדלי ניסוח. טען תמלול SRT מתוזמן או הפק תמלול מקומי. ההצעות מבוססות על דמיון מילולי בלבד; שגיאות תמלול ומשפטים חוזרים מחייבים בדיקה. אין שינוי במסמך הפתוח לפני ייבוא מפורש למסמך חדש."));
            help.Children.Add(Label("הפרד קטעי תסריט בשורה ריקה כדי לשמור קטע מרובה שורות. ללא שורות ריקות, כל שורה היא קטע נפרד. אין חלוקה מחדש או שינוי בתגי עיצוב; בדוק אורך ושבירת שורות לאחר הייבוא."));
            var editor = Field(initialScript, true); editor.Name = "ScriptReviewText"; editor.MinHeight = 160; preparation.Children.Add(Label("התסריט לעבודה — קובץ המקור נשמר ללא שינוי")); preparation.Children.Add(editor);
            var mediaLabel = Label("שמע / וידאו: " + (File.Exists(media) ? media : "טרם נבחר"));
            var transcriptLabel = Label("טרם נטען תמלול מתוזמן");
            var status = Label("");
            var rows = new ListBox { Height = 160, FontSize = 16, Name = "ScriptReviewRows" };
            var original = Field("", true); original.IsReadOnly = true;
            var spoken = Field("", true); spoken.IsReadOnly = true;
            var differences = Label("");
            var first = new NumericUpDown { Minimum = 1, Maximum = 1, Value = 1, FontSize = 16 };
            var last = new NumericUpDown { Minimum = 1, Maximum = 1, Value = 1, FontSize = 16 };
            var choice = new ComboBox { ItemsSource = new[] { "שמור את נוסח התסריט", "השתמש בטקסט התמלול" }, SelectedIndex = 0, FontSize = 16 };
            var omissions = new CheckBox { Content = Label("בדקתי את הסיכום: שורות שדילגתי עליהן ודיבור שלא שויך לא ייכללו בייבוא"), IsChecked = false };
            var summary = Field("", true); summary.IsReadOnly = true;
            var changing = false;
            Button? compare = null, approveRow = null, skipRow = null, import = null, cancelButton = null;
            var progress = new ProgressBar { IsIndeterminate = true, Height = 8, IsVisible = false, Name = "ScriptReviewProgress" }; panel.Children.Insert(1, progress);
            void UpdateActions()
            {
                var stage = stages.SelectedIndex; progress.IsVisible = busy;
                if (compare != null) { compare.IsVisible = stage == 0; compare.IsEnabled = !busy; }
                foreach (var button in new[] { approveRow, skipRow }) if (button != null) { button.IsVisible = stage == 1; button.IsEnabled = !busy && rows.SelectedIndex >= 0 && rows.SelectedIndex < script.Length; }
                if (import != null) { import.IsVisible = stage == 2; import.IsEnabled = !busy && script.Length > 0 && decisions.Count == script.Length && omissions.IsChecked == true; }
                if (cancelButton != null) { cancelButton.IsVisible = busy; cancelButton.IsEnabled = busy; }
            }
            stages.SelectionChanged += (_, _) => UpdateActions(); omissions.IsCheckedChanged += (_, _) => UpdateActions();
            void Invalidate()
            {
                script = Array.Empty<string>(); proposals = Array.Empty<ScriptTranscriptReview.Suggestion?>(); decisions.Clear();
                rows.ItemsSource = null; original.Text = spoken.Text = summary.Text = ""; differences.Text = ""; omissions.IsChecked = false; UpdateActions();
            }
            void UpdateSummary()
            {
                var used = new HashSet<int>();
                foreach (var d in decisions.Values.Where(d => !d.Skip)) for (var n = d.First; n <= d.Last; n++) used.Add(n);
                summary.Text = $"אושרו: {decisions.Values.Count(d => !d.Skip)} | דילוג מפורש: {decisions.Values.Count(d => d.Skip)} | טרם הוחלט: {script.Length - decisions.Count}\nדיבור שלא שויך (ייתכן שגיאת תמלול או דיבור שחסר בתסריט):\n" +
                    string.Join("\n", transcript.Select((c, i) => (c, i)).Where(x => !used.Contains(x.i)).Select(x => $"{x.i + 1}: {x.c.Text}"));
                omissions.IsChecked = false; UpdateActions();
            }
            void DisplayRange()
            {
                var a = (int)(first.Value ?? 1) - 1; var b = (int)(last.Value ?? 1) - 1;
                if (a < 0 || b < a || b >= transcript.Count || b - a >= 12) { spoken.Text = "בחר טווח של עד 12 שורות תמלול."; return; }
                spoken.Text = string.Join("\n", transcript.Skip(a).Take(b - a + 1).Select((c, n) => $"[{a + n + 1}] {TimeSpan.FromMilliseconds(c.Start)} → {TimeSpan.FromMilliseconds(c.End)}\n{c.Text}"));
                var row = rows.SelectedIndex;
                if (row >= 0 && row < script.Length)
                {
                    try
                    {
                        var diff = ScriptTranscriptReview.Diff(script[row], string.Join("\n", transcript.Skip(a).Take(b - a + 1).Select(c => c.Text)));
                        differences.Text = "מילים בתסריט שלא הותאמו: " + string.Join(" ", diff.ScriptOnly) + "\nמילים בתמלול שלא הותאמו: " + string.Join(" ", diff.TranscriptOnly) + "\nסימני פיסוק, ניקוד ותגי עיצוב אינם חלק מההשוואה. הטקסטים המלאים מוצגים לעיל.";
                    }
                    catch (InvalidDataException e) { differences.Text = e.Message; }
                }
            }
            string RowCaption(int i) => $"{i + 1}. {(decisions.TryGetValue(i, out var d) ? d.Skip ? "דילוג" : "אושר" : "טרם אושר")} | {(proposals[i] is { } p ? p.Ambiguous ? "התאמה לא חד־משמעית" : "הצעה לבדיקה" : "ללא הצעה")} | {script[i].Replace('\n', ' ')}";
            void RefreshRows(int selected)
            {
                changing = true; rows.ItemsSource = Enumerable.Range(0, script.Length).Select(RowCaption).ToArray(); rows.SelectedIndex = selected; changing = false;
            }
            void SelectRow()
            {
                var i = rows.SelectedIndex; if (changing || i < 0 || i >= script.Length) return;
                changing = true; original.Text = script[i];
                var p = proposals[i];
                var d = decisions.GetValueOrDefault(i);
                first.Value = (d is { Skip: false } ? d.First : p?.First ?? 0) + 1;
                last.Value = (d is { Skip: false } ? d.Last : p?.Last ?? 0) + 1;
                choice.SelectedIndex = d?.Choice == ScriptTranscriptReview.TextChoice.Transcript ? 1 : 0;
                status.Text = p == null ? "לא נמצאה הצעה. בחר טווח ידנית לפי התמלול והשמע או דלג במפורש." : $"דמיון מילולי: {p.Similarity:P0} — אינו מדד לדיוק תזמון. " + (p.Ambiguous ? "קיימת עמימות; בדוק את המיקום ידנית. " : "") + (p.SearchLimited ? "החיפוש הוגבל עקב חזרות רבות. " : "") + "הזמנים מועתקים מגבולות התמלול, ללא תזמון מילים חדשות.";
                changing = false; DisplayRange();
            }
            void ChangedChoice()
            {
                if (changing) return;
                var i = rows.SelectedIndex;
                if (i >= 0) { decisions.Remove(i); RefreshRows(i); UpdateSummary(); }
                DisplayRange();
            }
            rows.SelectionChanged += (_, _) => { SelectRow(); UpdateActions(); };
            first.ValueChanged += (_, _) => ChangedChoice(); last.ValueChanged += (_, _) => ChangedChoice(); choice.SelectionChanged += (_, _) => ChangedChoice();
            editor.TextChanged += (_, _) => { if (!busy) Invalidate(); };
            ActionButton(preparation, "טען תסריט Word / TXT / SRT / MD…", async () =>
            {
                if (busy) return;
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("תסריט") { Patterns = new[] { "*.docx", "*.txt", "*.srt", "*.md" } } } });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                var text = await Task.Run(() => ScriptImport.Read(path)); if (!closed) editor.Text = text;
            });
            preparation.Children.Add(mediaLabel);
            ActionButton(preparation, "בחר שמע או וידאו…", async () =>
            {
                if (busy) return;
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("שמע או וידאו") { Patterns = new[] { "*.wav", "*.mp3", "*.m4a", "*.flac", "*.mkv", "*.mp4", "*.mov", "*.webm" } } } });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null || closed) return;
                media = path; transcript.Clear(); transcriptLabel.Text = "המדיה השתנתה. טען או הפק שוב תמלול המתאים לה."; mediaLabel.Text = "שמע / וידאו: " + media; Invalidate();
            });
            help.Children.Add(Label("תמלול אוטומטי עלול לטעות. בדוק שבחרת את אותו הסרט, ערוץ השמע והשפה. אין פיצוי אוטומטי על היסט וידאו או התאמה לגרסה אחרת."));
            help.Children.Add(Label("בתמלול המקומי בחר עברית ומודל Whisper רב־לשוני, ללא הסיומת .en. הורדת מנוע ומודל נדרשת רק אם אינם מותקנים. אפשר גם לטעון SRT מכל כלי תמלול אחר."));
            preparation.Children.Add(transcriptLabel);
            void SetTranscript(List<ScriptTranscriptReview.Cue> cues, string description)
            {
                ScriptTranscriptReview.Validate(cues); transcript = cues; first.Maximum = last.Maximum = cues.Count;
                Invalidate(); transcriptLabel.Text = description + $" — {cues.Count} שורות";
            }
            ActionButton(preparation, "טען תמלול מתוזמן SRT שכבר יש לי…", async () =>
            {
                if (busy) return;
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("תמלול מתוזמן") { Patterns = new[] { "*.srt" } } } });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("בחר SRT עד 20 MB.");
                var text = await File.ReadAllTextAsync(path); if (closed) return;
                SetTranscript(ScriptTranscriptReview.ReadSrt(text), Path.GetFileName(path));
            });
            ActionButton(preparation, "הפק תמלול עברי מקומי מהשמע…", async () =>
            {
                if (busy) return;
                if (!File.Exists(media)) throw new InvalidDataException("בחר שמע או וידאו קיים.");
                if (!await RequireFfmpegOk()) return;
                var vm = await _windowService.ShowDialogAsync<SpeechToTextWindow, SpeechToTextViewModel>(dialog, v => v.InitializeCustomScriptReview(media));
                if (closed || !vm.OkPressed) return;
                if (vm.DoTranslateToEnglish) throw new InvalidDataException("התוצאה תורגמה לאנגלית. הפק תמלול באותה שפה, ללא תרגום.");
                SetTranscript(vm.TranscribedSubtitle.Paragraphs.Select(p => new ScriptTranscriptReview.Cue(p.StartTime.TotalMilliseconds, p.EndTime.TotalMilliseconds, p.Text)).ToList(), "תמלול מקומי — בדוק מול השמע");
            });
            compare = ActionButton(actions, "השווה והצג הצעות — ללא אישור אוטומטי", async () =>
            {
                if (busy) return;
                Invalidate(); script = ScriptTranscriptReview.PrepareRows(editor.Text ?? "");
                busy = true; editor.IsEnabled = false; UpdateActions();
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2)); running = cancel;
                try
                {
                    proposals = await Task.Run(() => ScriptTranscriptReview.Compare(script, transcript, cancel.Token), cancel.Token);
                    if (closed) return;
                    RefreshRows(0); SelectRow(); UpdateSummary(); stages.SelectedIndex = 1;
                }
                finally { busy = false; editor.IsEnabled = true; running = null; UpdateActions(); }
            });
            compare.Name = "ScriptReviewCompare";
            cancelButton = ActionButton(actions, "בטל השוואה", () => { running?.Cancel(); return Task.CompletedTask; });
            review.Children.Add(rows); review.Children.Add(status);
            review.Children.Add(Label("התסריט המקורי לכתובית הנבחרת")); review.Children.Add(original);
            review.Children.Add(Label("שורת התמלול הראשונה בטווח")); review.Children.Add(first);
            review.Children.Add(Label("שורת התמלול האחרונה בטווח — ניתן לתקן ידנית")); review.Children.Add(last);
            review.Children.Add(spoken); review.Children.Add(differences); review.Children.Add(choice);
            approveRow = ActionButton(actions, "אשר את הטווח והנוסח לשורה הנבחרת", () =>
            {
                if (busy || rows.SelectedIndex < 0 || rows.SelectedIndex >= script.Length) return Task.CompletedTask;
                var i = rows.SelectedIndex; var d = new ScriptTranscriptReview.Decision(i, (int)(first.Value ?? 1) - 1, (int)(last.Value ?? 1) - 1, choice.SelectedIndex == 1 ? ScriptTranscriptReview.TextChoice.Transcript : ScriptTranscriptReview.TextChoice.Script);
                // Reuse the final validator for single-row range checks before recording approval.
                _ = ScriptTranscriptReview.Build(new[] { script[i] }, transcript, new[] { d with { Row = 0 } });
                decisions[i] = d; RefreshRows(i); UpdateSummary(); return Task.CompletedTask;
            });
            approveRow.Name = "ScriptReviewApproveRow";
            skipRow = ActionButton(actions, "דלג במפורש על שורת התסריט הנבחרת", () =>
            {
                if (busy || rows.SelectedIndex < 0 || rows.SelectedIndex >= script.Length) return Task.CompletedTask;
                var i = rows.SelectedIndex; decisions[i] = new ScriptTranscriptReview.Decision(i, -1, -1, ScriptTranscriptReview.TextChoice.Script, true);
                RefreshRows(i); UpdateSummary(); return Task.CompletedTask;
            });
            skipRow.Name = "ScriptReviewSkipRow";
            ActionButton(review, "עבור לסיכום ולייבוא…", () => { stages.SelectedIndex = 2; return Task.CompletedTask; });
            final.Children.Add(Label("בדוק את ההחלטות והדיבור שלא שויך. הייבוא זמין רק אחרי החלטה לכל שורה ואישור הסיכום."));
            final.Children.Add(summary); final.Children.Add(omissions);
            final.Children.Add(Label("שמור תסריט משתמש בזמני הקטע שבתמלול, גם כשהניסוח שונה. אין כאן יישור מדויק לכל מילה. בייבוא ייכללו רק הבחירות שאישרת; דיבור שלא שויך לא מתווסף מעצמו."));
            import = ActionButton(actions, "בדקתי מול השמע — ייבא בחירות למסמך חדש", () =>
            {
                if (busy) return Task.CompletedTask;
                if (omissions.IsChecked != true) throw new InvalidDataException("בדוק את סיכום השורות ודיבור שלא שויך ואשר אותו.");
                approved = ScriptTranscriptReview.Build(script, transcript, decisions.Values.ToList()); dialog.Close(); return Task.CompletedTask;
            });
            import.Name = "ScriptReviewImport"; UpdateActions();
        });
        if (approved == null || Window == null) return;
        if (document != _subtitleFileName || generation != _ostDocumentGeneration || hash != GetFastHash()) { ShowStatus("המסמך השתנה. התוצאה לא יובאה; פתח שוב את ההשוואה.", 8000); return; }
        if (!await HasChangesContinue()) return;
        if (hash != GetFastHash() || generation != _ostDocumentGeneration) { ShowStatus("המסמך השתנה בזמן האישור. התוצאה לא יובאה.", 8000); return; }
        _subtitleFileName = string.Empty; ResetSubtitle();
        foreach (var cue in approved) Subtitles.Add(new SubtitleLineViewModel { Text = cue.Text, StartTime = TimeSpan.FromMilliseconds(cue.Start), EndTime = TimeSpan.FromMilliseconds(cue.End) });
        Renumber(); _updateAudioVisualizer = true;
        if (File.Exists(media)) await AutoOpenVideoFile(media);
        ShowStatus("הבחירות יובאו למסמך חדש. בדוק את גבולות התמלול מול השמע ושמור בשם חדש.", 9000);
    }
}

