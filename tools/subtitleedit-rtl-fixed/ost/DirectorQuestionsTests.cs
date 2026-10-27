using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    [Fact]
    public void DirectorStatusIsSeparateFromQcAndSurvivesSidecarAndReopensAfterTextChanges()
    {
        var id = Guid.NewGuid(); var note = new WorkNote { Id = id, Text = "שלום!", Start = 1000, End = 2000, NeedsReview = true, Reviewed = false };
        DirectorQuestions.Update(note, "למי הוא פונה?", "לנטלי", true);
        Assert.True(note.NeedsReview); Assert.False(note.Reviewed);
        var work = new ProjectWork { Notes = new() { note } };
        var restored = System.Text.Json.JsonSerializer.Deserialize<ProjectWork>(System.Text.Json.JsonSerializer.Serialize(work))!;
        var lines = new[] { new DirectorLine(id, 4, "שלום!", 1000, 2000), new DirectorLine(Guid.NewGuid(), 5, "רגילה", 2000, 3000) };
        Assert.Empty(DirectorQuestions.Rows(restored, lines, true, 0));
        Assert.Equal("טופל", Assert.Single(DirectorQuestions.Rows(restored, lines, false, 1000)).Status);
        Assert.Contains("00:00:02,000", DirectorQuestions.Rows(restored, lines, false, 1000)[0].Time);
        Assert.Contains("-00:00:01,000", DirectorQuestions.Rows(restored, lines, false, -2000)[0].Time);
        DirectorQuestions.RefreshNote(restored.Notes[0], "שלום לכם!", 1000, 2000);
        Assert.False(restored.Notes[0].DirectorResolved); Assert.Equal("לנטלי", restored.Notes[0].DirectorAnswer);
        Assert.Single(DirectorQuestions.Rows(restored, lines.Select(l => l with { Text = "שלום לכם!" }), true, 0));
        Assert.Throws<InvalidDataException>(() => DirectorQuestions.Update(note, " ", "", false));
        var legacy = System.Text.Json.JsonSerializer.Deserialize<WorkNote>("{\"Note\":\"לבדוק עם הבמאי\",\"NeedsReview\":true}")!;
        Assert.False(legacy.DirectorQuestion); Assert.Equal("לבדוק עם הבמאי", legacy.Note);
    }
    [Fact]
    public void DirectorExportsAreRealOfficeTablesPreserveHebrewAndNeverExecuteCellFormulas()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var rows = new[] { new DirectorQuestionRow(Guid.NewGuid(), 125, "00:08:32,000 – 00:08:35,000", "<i>אני חוזר הביתה.</i>", "האם הכוונה לבית שלו?\nאו לבית המשפחה?", "ממתין לתשובה", ""), new DirectorQuestionRow(Guid.NewGuid(), 130, "00:09:00,000 – 00:09:02,000", "=1+1", "בדיקת שם: Natalie & Sidney", "טופל", "שם הדמות: נטלי") };
            var doc = Path.Combine(root, "questions.docx"); var sheet = Path.Combine(root, "questions.xlsx");
            DirectorQuestions.ExportWord(doc, "שאלות לסרט — כותרת שנבחרה", rows); DirectorQuestions.ExportExcel(sheet, "שאלות לסרט — כותרת שנבחרה", rows);
            using (var zip = ZipFile.OpenRead(doc))
            {
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                using var stream = zip.GetEntry("word/document.xml")!.Open(); var xml = XDocument.Load(stream);
                Assert.Equal(3, xml.Descendants(w + "tr").Count()); Assert.Equal(6, xml.Descendants(w + "tr").First().Elements(w + "tc").Count());
                Assert.Contains("שאלות לסרט", xml.ToString()); Assert.Contains("האם הכוונה לבית שלו?", xml.ToString()); Assert.Contains("נטלי", xml.ToString()); Assert.Single(xml.Descendants(w + "tblHeader")); Assert.Single(xml.Descendants(w + "bidiVisual"));
                Assert.Equal("אני חוזר הביתה.", string.Concat(xml.Descendants(w + "tr").Skip(1).First().Elements(w + "tc").ElementAt(2).Descendants(w + "t").Select(t => t.Value)));
                Assert.Equal("start", xml.Descendants(w + "jc").First().Attribute(w + "val")!.Value);
                Assert.Equal("landscape", xml.Descendants(w + "pgSz").Single().Attribute(w + "orient")!.Value);
            }
            using (var zip = ZipFile.OpenRead(sheet))
            {
                XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var xml = XDocument.Load(stream);
                Assert.Empty(xml.Descendants(s + "f")); Assert.Contains("=1+1", xml.ToString()); Assert.Equal("1", xml.Descendants(s + "sheetView").Single().Attribute("rightToLeft")!.Value);
                using var table = zip.GetEntry("xl/tables/table1.xml")!.Open(); var t = XDocument.Load(table);
                Assert.Equal("A2:F4", t.Root!.Attribute("ref")!.Value); Assert.Equal(6, t.Descendants(s + "tableColumn").Count());
            }
            // Used by CI's Office-reader/render smoke check; only synthetic test data.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                var fixture = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!, "director-export-smoke"); Directory.CreateDirectory(fixture);
                File.Copy(doc, Path.Combine(fixture, "questions.docx"), true); File.Copy(sheet, Path.Combine(fixture, "questions.xlsx"), true);
            }
            var original = File.ReadAllBytes(sheet); Assert.Throws<InvalidDataException>(() => DirectorQuestions.ExportExcel(sheet, "title", new[] { rows[0] with { Question = new string('א', 32768) } })); Assert.Equal(original, File.ReadAllBytes(sheet));
            Assert.Empty(Directory.GetFiles(root, ".director-*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public void DirectorMetadataAndTableScanNeverChangeSubtitleHashAndRejectOtherDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var document = Path.Combine(root, "movie.srt"); File.WriteAllText(document, "original bytes");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainViewModel).GetField("_subtitleFileName", flags)!.SetValue(vm, document);
            var generation = (int)typeof(MainViewModel).GetField("_ostDocumentGeneration", flags)!.GetValue(vm)!;
            vm.Subtitles.Clear(); var row = new SubtitleLineViewModel { Number = 1, Text = "טקסט מקורי!", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) }; vm.Subtitles.Add(row);
            vm.CurrentDirectorQuestions(true); var hash = vm.GetFastHash();
            vm.SaveDirectorQuestion(row.Id, "למי הוא פונה?", "", false, document, generation);
            Assert.Single(vm.CurrentDirectorQuestions(true)); Assert.Equal(hash, vm.GetFastHash()); Assert.Equal("original bytes", File.ReadAllText(document)); Assert.Contains("ממתין לתשובת הבמאי", row.CustomWorkDescription);
            vm.SaveDirectorQuestion(row.Id, "למי הוא פונה?", "לנטלי", true, document, generation);
            Assert.Empty(vm.CurrentDirectorQuestions(true)); Assert.Equal(hash, vm.GetFastHash()); Assert.False(CustomProject.Load<ProjectWork>(Path.ChangeExtension(document, ".sework.json")).Notes.Single().Reviewed);
            Assert.Throws<InvalidOperationException>(() => vm.SaveDirectorQuestion(row.Id, "?", "", false, "other.srt", generation));
            row.Text = "נוסח חדש"; vm.MonitorCustomProject(); Assert.Single(vm.CurrentDirectorQuestions(true)); Assert.Equal("לנטלי", vm.CurrentDirectorQuestions(true)[0].Answer);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }
}

