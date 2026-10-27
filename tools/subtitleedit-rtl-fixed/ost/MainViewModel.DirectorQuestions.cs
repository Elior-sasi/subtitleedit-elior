using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;
public partial class MainViewModel
{
    internal List<DirectorQuestionRow> CurrentDirectorQuestions(bool openOnly)
    {
        MonitorCustomProject();
        return DirectorQuestions.Rows(_customWork, Subtitles.Where(r => !r.IsReferenceOnly && !r.IsDefault && !r.IsComment)
            .Select(r => new DirectorLine(r.Id, r.Number, r.Text, r.StartTime.TotalMilliseconds, r.EndTime.TotalMilliseconds)), openOnly, Se.Settings.General.CurrentVideoOffsetInMs);
    }
    internal void SaveDirectorQuestion(Guid id, string question, string answer, bool resolved, string document, int generation)
    {
        if (document != _subtitleFileName || generation != _ostDocumentGeneration) throw new InvalidOperationException("הקובץ הפעיל השתנה. פתח את טבלת השאלות מחדש.");
        var row = Subtitles.FirstOrDefault(r => r.Id == id && !r.IsReferenceOnly && !r.IsDefault && !r.IsComment) ?? throw new InvalidOperationException("הכתובית אינה קיימת עוד.");
        var note = CustomProject.FindNote(_customWork, row.Id, row.Text, row.StartTime.TotalMilliseconds, row.EndTime.TotalMilliseconds) ?? new WorkNote { Id = row.Id, Text = row.Text, Start = row.StartTime.TotalMilliseconds, End = row.EndTime.TotalMilliseconds };
        DirectorQuestions.Update(note, question, answer, resolved);
        if (!_customWork.Notes.Contains(note)) _customWork.Notes.Add(note);
        _customNoteCache[row.Id] = note; SaveCustomWork(); MonitorCustomProject();
    }
    private Task OpenDirectorQuestions()
    {
        if (!EnsureCustomProject()) return Task.CompletedTask;
        var document = _subtitleFileName!; var generation = _ostDocumentGeneration;
        return ProjectDialog("טבלת שאלות לבמאי", (panel, dialog) =>
        {
            dialog.Width = 1250;
            panel.Children.Add(Label("שאלות לכתוביות בקובץ הפתוח. כדי להוסיף שאלה, בחר כתובית ולחץ בתפריט פרויקט על ׳שאלה לבמאי׳. שאלות והערות אינן נכתבות לקובץ הכתוביות."));
            var title = Field(Path.GetFileNameWithoutExtension(document)); panel.Children.Add(Label("כותרת הטבלה ב־Word וב־Excel")); panel.Children.Add(title);
            var open = new CheckBox { Content = Label("הצג וייצא רק שאלות פתוחות"), IsChecked = true }; panel.Children.Add(open);
            var grid = new DataGrid { Name = "DirectorQuestionsGrid", AutoGenerateColumns = false, IsReadOnly = true, FontSize = 16, Height = 300, SelectionMode = DataGridSelectionMode.Single, CanUserReorderColumns = false };
            foreach (var (header, property, width) in new[] { ("כתובית", "Number", 80), ("זמן", "Time", 240), ("הכתובית", "Text", 280), ("השאלה", "Question", 320), ("מצב", "Status", 150), ("תשובה", "Answer", 280) })
                grid.Columns.Add(new DataGridTemplateColumn { Header = Label(header), Width = new DataGridLength(width), CellTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock { [!TextBlock.TextProperty] = new Binding(property) { Source = item }, TextWrapping = TextWrapping.Wrap, FlowDirection = property is "Number" or "Time" ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, FontSize = 16, Margin = new Thickness(6, 4) }) });
            panel.Children.Add(grid); var summary = Label(""); panel.Children.Add(summary);
            var question = Field("", true); var answer = Field("", true); var resolved = new CheckBox { Content = Label("השאלה טופלה (אינו אישור לכל הכתובית)") };
            panel.Children.Add(Label("עריכת השאלה הנבחרת")); panel.Children.Add(question); panel.Children.Add(Label("תשובת הבמאי")); panel.Children.Add(answer); panel.Children.Add(resolved);
            void LoadSelected()
            {
                var selected = grid.SelectedItem as DirectorQuestionRow;
                question.Text = selected?.Question ?? ""; answer.Text = selected?.Answer ?? ""; resolved.IsChecked = selected?.Status == "טופל";
                question.IsEnabled = answer.IsEnabled = resolved.IsEnabled = selected != null;
            }
            List<DirectorQuestionRow> Refresh()
            {
                if (document != _subtitleFileName || generation != _ostDocumentGeneration) throw new InvalidOperationException("המסמך השתנה. פתח את הטבלה מחדש.");
                var id = (grid.SelectedItem as DirectorQuestionRow)?.Id;
                var rows = CurrentDirectorQuestions(open.IsChecked == true); grid.ItemsSource = rows;
                grid.SelectedItem = rows.FirstOrDefault(r => r.Id == id) ?? rows.FirstOrDefault();
                summary.Text = rows.Count + " שאלות מוצגות. הייצוא כולל את השאלות המוצגות בלבד; לחץ ׳שמור שאלה ותשובה׳ כדי לכלול שינויים שהקלדת.";
                LoadSelected(); return rows;
            }
            grid.SelectionChanged += (_, _) => LoadSelected(); open.IsCheckedChanged += (_, _) => Refresh();
            ActionButton(panel, "שמור שאלה ותשובה", () => { if (grid.SelectedItem is not DirectorQuestionRow row) return Task.CompletedTask; SaveDirectorQuestion(row.Id, question.Text ?? "", answer.Text ?? "", resolved.IsChecked == true, document, generation); Refresh(); return Task.CompletedTask; });
            ActionButton(panel, "קפוץ לכתובית המקורית", () => { if (grid.SelectedItem is DirectorQuestionRow row) { var actual = Subtitles.FirstOrDefault(s => s.Id == row.Id); if (actual != null) { dialog.Close(); SelectAndScrollToRowCentered(Subtitles.IndexOf(actual)); } } return Task.CompletedTask; });
            async Task Export(bool word)
            {
                if (grid.SelectedItem is DirectorQuestionRow selected && (question.Text != selected.Question || answer.Text != selected.Answer || (resolved.IsChecked == true) != (selected.Status == "טופל")))
                    throw new InvalidOperationException("יש שינויים שלא נשמרו. לחץ תחילה על ׳שמור שאלה ותשובה׳ ואז ייצא.");
                // Snapshot only persisted notes; exporting cannot rewrite subtitles or user settings.
                if (document != _subtitleFileName || generation != _ostDocumentGeneration) throw new InvalidOperationException("המסמך השתנה. פתח את הטבלה מחדש.");
                var rows = CurrentDirectorQuestions(open.IsChecked == true);
                if (rows.Count == 0) throw new InvalidOperationException("אין שאלות בסינון הנבחר. הוסף שאלה או בטל את סינון השאלות הפתוחות.");
                var heading = title.Text ?? "";
                var extension = word ? ".docx" : ".xlsx";
                var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = word ? "ייצוא טבלת שאלות ל־Word" : "ייצוא טבלת שאלות ל־Excel", SuggestedFileName = Path.GetFileNameWithoutExtension(document) + "-Director" + extension, DefaultExtension = extension, ShowOverwritePrompt = true, FileTypeChoices = new[] { new FilePickerFileType(word ? "Word" : "Excel") { Patterns = new[] { "*" + extension } } } });
                if (file == null) return;
                var path = file.TryGetLocalPath() ?? throw new IOException("בחר תיקייה מקומית לייצוא.");
                if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("בחר סיומת " + extension + " לקובץ הייצוא.");
                await Task.Run(() => { if (word) DirectorQuestions.ExportWord(path, heading, rows); else DirectorQuestions.ExportExcel(path, heading, rows); });
                summary.Text = "הטבלה נשמרה: " + path;
            }
            ActionButton(panel, "ייצא את השאלות המוצגות ל־Word…", () => Export(true));
            ActionButton(panel, "ייצא את השאלות המוצגות ל־Excel…", () => Export(false));
            Refresh();
        });
    }
}

