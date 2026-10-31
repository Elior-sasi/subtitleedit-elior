using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    public void LocalMfaRoundTripsLiteralRequestAndRejectsUnsupportedInputs()
    {
        var cues = CloudMfaImport.CuesFromPrepared("<i>Hello,\nworld!</i>\n\n-Next line.");
        using var json = JsonDocument.Parse(LocalMfa.Request(cues));
        Assert.Equal(cues.Select(c => c.Text), json.RootElement.GetProperty("cues").EnumerateArray().Select(c => c.GetProperty("text").GetString()));
        Assert.Throws<InvalidDataException>(() => LocalMfa.Request(CloudMfaImport.CuesFromPrepared("שלום")));
        Assert.Throws<InvalidDataException>(() => LocalMfa.Request(CloudMfaImport.CuesFromPrepared(new string('a', 1001))));
        Assert.Throws<InvalidDataException>(() => LocalMfa.PythonCommand("python.exe", "models", "--check"));
    }

    [Fact]
    public void LocalMfaRejectsWrongIdentityPartialAndStaleResultsAndShowsReviewFlags()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var cues = CloudMfaImport.CuesFromPrepared("<i>Hello,\nworld!</i>\n\n-Next line.");
        try
        {
            string Local(string state, string status = "aligned") => MfaResult(state, status, new[] { "crosses_long_pause" }).Replace(MfaJob, "local000000000000000000000000000");
            File.WriteAllText(file, Local("needs_review"));
            var result = LocalMfa.ReadResult(file, cues, new string('a', 64), new string('c', 64));
            Assert.Equal(cues.Select(c => c.Text), result.Cues.Select(c => c.Text));
            Assert.Contains("הפסקה ארוכה", result.ReviewText);
            Assert.Throws<CloudMfaException>(() => LocalMfa.ReadResult(file, cues, new string('b', 64), new string('c', 64)));
            File.WriteAllText(file, Local("partial", "partial"));
            Assert.Throws<CloudMfaException>(() => LocalMfa.ReadResult(file, cues, new string('a', 64), new string('c', 64)));
            File.WriteAllText(file, MfaResult("complete"));
            Assert.Throws<InvalidDataException>(() => LocalMfa.ReadResult(file, cues, new string('a', 64), new string('c', 64)));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void LocalMfaDiscoveryPreservesConfiguredPathAndChecksBothModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "mfa-discovery-" + Guid.NewGuid());
        var detected = Path.Combine(root, "miniforge3", "envs", "subtitleedit-mfa", "python.exe");
        var configured = Path.Combine(root, "chosen", "python.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(detected)!); File.WriteAllText(detected, "fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(configured)!); File.WriteAllText(configured, "fixture");
            Assert.Equal(configured, LocalMfa.FindPython(configured, root));
            Assert.Equal(detected, LocalMfa.FindPython("missing", root));
            File.Delete(detected); Assert.Empty(LocalMfa.FindPython("", root));
            Assert.False(LocalMfa.ModelsReady(root));
            foreach (var part in new[] { "acoustic/english_us_arpa.zip", "dictionary/english_us_arpa.dict" })
            {
                var path = Path.Combine(root, "pretrained_models", part); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
                if (part.StartsWith("acoustic")) Assert.False(LocalMfa.ModelsReady(root));
            }
            Assert.True(LocalMfa.ModelsReady(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void LocalMfaProgressShowsOnlyMeasuredPercentAndResetsBetweenStages()
    {
        var display = new LocalMfa.ProgressDisplay();
        var window = new Window { Content = display };
        try
        {
            window.Show(); display.Begin("מכין שמע");
            Assert.True(display.Bar.IsVisible); Assert.True(display.Bar.IsIndeterminate);
            Assert.Contains("00:00", display.Detail.Text);
            display.Apply("mfa_progress:42"); Assert.False(display.Bar.IsIndeterminate); Assert.Equal(42, display.Bar.Value);
            Assert.Contains("42%", display.Detail.Text);
            display.Apply("mfa_progress:101"); Assert.Equal(42, display.Bar.Value);
            display.Apply("raw private path and transcript"); Assert.DoesNotContain("private", display.Stage.Text);
            display.Apply("segmenting"); Assert.True(display.Bar.IsIndeterminate); Assert.Equal(0, display.Bar.Value);
            Assert.DoesNotContain("42%", display.Detail.Text);
            display.Finish("בוטל"); Assert.False(display.Bar.IsVisible); Assert.False(display.Bar.IsIndeterminate);
            display.Begin("בודק תוצאה"); display.Finish("הושלם", true); Assert.Equal(100, display.Bar.Value);
            Assert.False(display.Bar.IsIndeterminate);
        }
        finally { display.Stop(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task RealLocalMfaDialogCancelKeepsDocumentAndSettingsUntouched()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        var media = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        File.WriteAllBytes(media, new byte[] { 1, 2, 3 });
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            vm.Subtitles.Clear(); var original = new SubtitleLineViewModel { Text = "Current document", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) }; vm.Subtitles.Add(original);
            var before = JsonSerializer.Serialize(Se.Settings);
            var task = (Task)typeof(MainViewModel).GetMethod("OpenLocalMfaAlignment", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object[] { "Hello world!", media })!;
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.Where(w => w.Title == "תסריט באנגלית לכתוביות באנגלית — MFA מקומי"));
            var advanced = Assert.Single(dialog.GetVisualDescendants().OfType<Expander>().Where(e => e.Name == "LocalMfaAdvanced"));
            Assert.False(advanced.IsExpanded);
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "תזמן את התסריט");
            Assert.False(Assert.Single(dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "בדקתי — ייבא למסמך חדש")).IsEnabled);
            advanced.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), t => t.Text != null && t.Text.Contains("montreal-forced-aligner=3.4.3"));
            dialog.Close(); await task;
            Assert.Same(original, Assert.Single(vm.Subtitles)); Assert.Equal("Current document", original.Text);
            Assert.Equal(TimeSpan.FromSeconds(1), original.StartTime); Assert.Equal(TimeSpan.FromSeconds(3), original.EndTime);
            Assert.Equal(before, JsonSerializer.Serialize(Se.Settings));
        }
        finally { window.Close(); File.Delete(media); }
    }
}

