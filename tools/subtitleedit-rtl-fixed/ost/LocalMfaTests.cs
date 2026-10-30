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
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), t => t.Text != null && t.Text.Contains("montreal-forced-aligner=3.4.3"));
            dialog.Close(); await task;
            Assert.Same(original, Assert.Single(vm.Subtitles)); Assert.Equal("Current document", original.Text);
            Assert.Equal(TimeSpan.FromSeconds(1), original.StartTime); Assert.Equal(TimeSpan.FromSeconds(3), original.EndTime);
            Assert.Equal(before, JsonSerializer.Serialize(Se.Settings));
        }
        finally { window.Close(); File.Delete(media); }
    }
}
