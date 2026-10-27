using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Files.ImportPlainText;
using Nikse.SubtitleEdit.Features.Files.ImportPlainText.ForcedAlignerSetup;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private async Task OpenScriptToSubtitles()
    {
        if (Window == null) return;
        string? prepared = null; string media = _videoFileName ?? ""; var cloud = false;
        var document = _subtitleFileName; var generation = _ostDocumentGeneration; var hash = GetFastHash();
        await ProjectDialog("תסריט לכתוביות — הכנה ותזמון מקומי", (panel, dialog) =>
        {
            var closed = false; dialog.Closed += (_, _) => closed = true;
            panel.Children.Add(Label("1. טען תסריט. 2. בחר שמע או וידאו באותה שפה. 3. בדוק את הטקסט והחלוקה. 4. הפעל תזמון בחלון הבא, בדוק את הזמנים ואשר ייבוא למסמך חדש."));
            panel.Children.Add(Label("נדרש טקסט קרוב לנאמר. כותרות, הוראות בימוי ושמות דוברים אינם מזוהים בוודאות: הסר אותם כאן לפי הצורך. אין זיהוי קולות אוטומטי, ואין תרגום."));
            var editor = Field("", true); editor.FlowDirection = FlowDirection.LeftToRight; editor.MinHeight = 180;
            var name = Label("לא נטען תסריט"); panel.Children.Add(name);
            ActionButton(panel, "טען תסריט Word / TXT / SRT / MD…", async () =>
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "בחר תסריט", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("תסריט") { Patterns = new[] { "*.docx", "*.txt", "*.srt", "*.md" } } } });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
                var text = await Task.Run(() => ScriptImport.Read(path)); if (closed) return;
                editor.Text = text; name.Text = Path.GetFileName(path);
            });
            panel.Children.Add(Label("הטקסט לעבודה — ניתן לערוך, בלי לשנות את קובץ המקור")); panel.Children.Add(editor);
            ActionButton(panel, "התסריט שונה מהדיבור? פתח השוואה לתמלול…", async () => { var text = editor.Text ?? ""; dialog.Close(); await OpenScriptTranscriptReview(text, media); });
            var remove = new CheckBox { Content = Label("הסר בתצוגה המקדימה שמות בתבנית Name: בתחילת שורה"), FontSize = 16, IsChecked = false };
            panel.Children.Add(remove);
            var max = new NumericUpDown { Minimum = 20, Maximum = 100, Value = 42, FontSize = 16 };
            panel.Children.Add(Label("תווים בשורה — עד שתי שורות בכתובית. מילה ארוכה מהמגבלה תישאר שלמה לבדיקה.")); panel.Children.Add(max);
            var preview = Field("", true); preview.IsReadOnly = true; preview.FlowDirection = FlowDirection.LeftToRight;
            var hebrewWarning = Label(""); hebrewWarning.IsVisible = false;
            void Refresh()
            {
                preview.Text = ScriptImport.Split(ScriptImport.Prepare(editor.Text ?? "", remove.IsChecked == true), (int)(max.Value ?? 42));
                // Visible warning, not a block: no bundled local aligner and no cloud MFA model supports Hebrew yet.
                hebrewWarning.IsVisible = ImportPlainTextViewModel.LooksHebrew(preview.Text ?? "");
                hebrewWarning.Text = hebrewWarning.IsVisible ? "⚠ התסריט בעברית. למנועי היישור הזמינים כאן (CrispASR מקומי ו־MFA בענן) אין כרגע מודל לעברית, ולכן הזמנים עלולים להיות שגויים. לתסריט עברי מול דיבור עברי מומלץ \"תסריט שונה מהדיבור - השוואה\"." : "";
            }
            ActionButton(panel, "הצג חלוקה לפני תזמון", () => { Refresh(); return Task.CompletedTask; }); panel.Children.Add(preview);
            Avalonia.Automation.AutomationProperties.SetName(hebrewWarning, "אזהרה: אין מודל יישור לעברית"); panel.Children.Add(hebrewWarning);
            var mediaName = Label("שמע / וידאו: " + (File.Exists(media) ? media : "טרם נבחר")); panel.Children.Add(mediaName);
            ActionButton(panel, "בחר שמע או וידאו…", async () =>
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "בחר קובץ שמע או וידאו", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("שמע או וידאו") { Patterns = new[] { "*.wav", "*.mp3", "*.m4a", "*.flac", "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm" } } } });
                var picked = files.FirstOrDefault()?.TryGetLocalPath(); if (picked != null) { media = picked; mediaName.Text = "שמע / וידאו: " + media; }
            });
            panel.Children.Add(Label("המנוע: CrispASR מקומי, עם מודל תזמון לשפה שבחרת. התקנה ראשונה מורידה רכיבים; התסריט והשמע אינם מועלים לענן. Nyra אינו כלול; MFA זמין רק בכפתור היישור בענן שלמטה."));
            ActionButton(panel, "התקן / בחר מנוע ומודל תזמון…", async () => { await _windowService.ShowDialogAsync<ForcedAlignerSetupWindow, ForcedAlignerSetupViewModel>(dialog, vm => { vm.CustomScriptSetup = true; vm.Initialize(); }); });
            ActionButton(panel, "המשך לתצוגת התזמון — ללא שינוי המסמך כעת", async () =>
            {
                if (!File.Exists(media)) throw new InvalidDataException("בחר קובץ שמע או וידאו קיים.");
                Refresh();
                if (!await RequireFfmpegOk()) return;
                prepared = preview.Text; dialog.Close();
            });
            panel.Children.Add(Label("אפשרות נוספת: יישור בענן עם Montreal Forced Aligner, בשירות שהקמת. בחלון הבא תראה מה יישלח ולאן, ושום דבר לא יישלח לפני אישורך."));
            ActionButton(panel, "יישור בענן עם MFA… - ללא שינוי המסמך כעת", async () =>
            {
                if (!File.Exists(media)) throw new InvalidDataException("בחר קובץ שמע או וידאו קיים.");
                Refresh();
                if (!await RequireFfmpegOk()) return;
                prepared = preview.Text; cloud = true; dialog.Close();
            });
        });
        if (prepared == null || Window == null) return;
        if (generation != _ostDocumentGeneration || document != _subtitleFileName || hash != GetFastHash()) { ShowStatus("המסמך השתנה. פתח שוב את כלי התסריט.", 7000); return; }
        if (cloud) { await OpenCloudMfaAlignment(prepared, media); return; }
        var result = await ShowDialogAsync<ImportPlainTextWindow, ImportPlainTextViewModel>(vm => vm.InitializeCustomScript(prepared, media));
        if (!result.OkPressed || !result.CustomScriptCanImport || result.Subtitles.Count == 0) return;
        if (generation != _ostDocumentGeneration || document != _subtitleFileName || hash != GetFastHash()) { ShowStatus("המסמך השתנה. התוצאה לא יובאה.", 7000); return; }
        if (!await HasChangesContinue()) return;
        _subtitleFileName = string.Empty;
        ResetSubtitle();
        foreach (var item in result.Subtitles) Subtitles.Add(item);
        Renumber(); _updateAudioVisualizer = true;
        await AutoOpenVideoFile(media);
        ShowStatus("התזמון יובא למסמך חדש. בדוק מול השמע ושמור בשם חדש.", 9000);
    }
}

