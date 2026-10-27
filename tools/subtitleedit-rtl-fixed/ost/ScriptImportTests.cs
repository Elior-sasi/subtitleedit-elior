using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Files.ImportPlainText;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Features.Video.SpeechToText;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    [Fact]
    public void ScriptWordReadsTablesAndBreaksWithoutChangingSource()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Natalie: Hello!</w:t><w:br/><w:t>Keep THIS.</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Sidney: שלום.</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>");
            var before = File.ReadAllBytes(file);
            Assert.Equal("Natalie: Hello!\nKeep THIS.\nSidney: שלום.", ScriptImport.Read(file));
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void ScriptSrtIgnoresOldTimesAndPrefixCleanupRequiresOptIn()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt");
        try
        {
            File.WriteAllText(file, "1\n00:10:00,000 --> 00:10:02,000\nNatalie: Hello!\n\n2\n00:11:00,000 --> 00:11:02,000\n[LAUGHING]\nדובר 2: שלום.\n");
            var text = ScriptImport.Read(file);
            Assert.DoesNotContain("00:10", text); Assert.Contains("[LAUGHING]", text);
            Assert.Equal(text, ScriptImport.Prepare(text, false));
            var clean = ScriptImport.Prepare(text, true);
            Assert.DoesNotContain("Natalie:", clean); Assert.Contains("Hello!", clean); Assert.Contains("[LAUGHING]", clean); Assert.Contains("שלום.", clean);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void ScriptCueSplitPreservesWordsCasePunctuationAndLongTokens()
    {
        var longWord = new string('X', 65);
        var text = "This is a sentence, KEEP its punctuation! " + longWord + " Do not cut words.\n-First speaker.\n-Second speaker.";
        var split = ScriptImport.Split(text, 42);
        string Words(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Words(text), Words(split)); Assert.Contains(longWord, split);
        Assert.All(split.Split("\n\n"), cue => Assert.InRange(cue.Split('\n').Length, 1, 2));
        Assert.All(split.Split('\n').Where(l => l.Length > 42), line => Assert.Equal(longWord, line));
        Assert.Throws<InvalidDataException>(() => ScriptImport.Split(text, 0));
    }
    [AvaloniaFact]
    public async Task ScriptImportNeedsCompletedAlignmentAndRejectsStaleText()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<ImportPlainTextViewModel>();
        try
        {
            vm.InitializeCustomScript("A complete script.\n\nAnother line.", "sample.wav");
            for (var i = 0; i < 80 && vm.Subtitles.Count == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(2, vm.Subtitles.Count); Assert.False(vm.CustomScriptCanImport);
            vm.Subtitles[0].StartTime = TimeSpan.Zero; vm.Subtitles[0].EndTime = TimeSpan.FromSeconds(1);
            vm.Subtitles[1].StartTime = TimeSpan.FromSeconds(2); vm.Subtitles[1].EndTime = TimeSpan.FromSeconds(3);
            vm.CustomScriptRecordAlignment(); Assert.True(vm.CustomScriptCanImport);
            vm.Subtitles[0].Text += " changed"; Assert.False(vm.CustomScriptCanImport);
            vm.CustomScriptRecordAlignment(); Assert.True(vm.CustomScriptCanImport);
            vm.PlainTextChanged(); Assert.False(vm.CustomScriptCanImport);
        }
        finally { vm.OnClosingCleanup(); }
    }
    [Fact]
    public void HebrewScriptWarnsForEveryAlignerWithoutHebrewModel()
    {
        foreach (var option in Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines.ForcedAlignerOption.All())
            Assert.True(ImportPlainTextViewModel.NeedsHebrewAlignerWarning("<i>שלום לכולם</i>, מה שלומכם?", option.Choice));
        Assert.False(ImportPlainTextViewModel.NeedsHebrewAlignerWarning("Hello everyone, how are you?", "wav2vec2-aligner-en"));
        Assert.False(ImportPlainTextViewModel.LooksHebrew("OK שׁ"));
    }
    [Fact]
    public void ScriptEngineRejectsUnexpectedDownloadBeforeExecution()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not the official release archive"));
        stream.Position = 4;
        Assert.Throws<InvalidDataException>(() => DownloadSpeechToTextEngineViewModel.VerifyCustomScriptEngine(stream));
        Assert.Equal(4, stream.Position);
    }
}

