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
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Features.Translate;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    [AvaloniaFact]
    public async Task NativeGeminiRequestIncludesOnlyThisProjectsGuideAndRetainsNativeOutputFormat()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<AutoTranslateViewModel>();
        var nativePrompt = Configuration.Settings.Tools.GeminiPrompt;
        try
        {
            Configuration.Settings.Tools.GeminiPrompt = "Translate from {0} to {1}. Return numbered lines.";
            var profile = new ProjectProfile { Name = "בדיקה", Guide = "GUIDE_CANONICAL", Prompt = "CLIENT_INSTRUCTIONS" };
            profile.Terms.Add(new ProjectTerm { Preferred = "שם מאושר", Source = "Approved" });
            profile.PreviousEpisodes.Add(new EpisodeContextDocument { Name = "old.srt", Text = "Hello Naruto returns", CueCount = 1, IncludeInAi = true });
            profile.PreviousEpisodes.Add(new EpisodeContextDocument { Name = "excluded.srt", Text = "DO_NOT_SEND", CueCount = 1, IncludeInAi = false });
            vm.ConfigureCustomGuide(profile);
            var gemini = Assert.Single(vm.AutoTranslators.OfType<GeminiTranslate>());
            Assert.True(vm.CustomGuideEnabled); Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out var error)); Assert.Empty(error);
            // Mutating the live project cannot change the request's immutable snapshot.
            profile.Guide = "LATER_GUIDE";
            var method = typeof(GeminiTranslate).GetMethod("MakeContent", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var content = (System.Net.Http.HttpContent)method.Invoke(gemini, new object[] { "1: Hello Naruto", "en", "he" })!;
            using var json = JsonDocument.Parse(await content.ReadAsStringAsync());
            var text = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
            Assert.Contains("GUIDE_CANONICAL", text); Assert.Contains("CLIENT_INSTRUCTIONS", text); Assert.Contains("שם מאושר", System.Text.RegularExpressions.Regex.Unescape(text));
            Assert.Contains("Return numbered lines.", text); Assert.Contains("1: Hello Naruto", text); Assert.Contains("old.srt", text); Assert.DoesNotContain("DO_NOT_SEND", text); Assert.DoesNotContain("LATER_GUIDE", text);
            Assert.Equal("Translate from {0} to {1}. Return numbered lines.", Configuration.Settings.Tools.GeminiPrompt);
            vm.ConfigureCustomGuide(new ProjectProfile { Guide = "OTHER_PROJECT" }); Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out _));
            Assert.DoesNotContain("GUIDE_CANONICAL", gemini.CustomGuidePrompt!("task", "text"));
        }
        finally { Configuration.Settings.Tools.GeminiPrompt = nativePrompt; }
    }

    [AvaloniaFact]
    public void NativeGuideNeverSilentlyUsesUnsupportedEngineAndCanBeExplicitlyDisabled()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<AutoTranslateViewModel>();
        var gemini = Assert.Single(vm.AutoTranslators.OfType<GeminiTranslate>());
        vm.ConfigureCustomGuide(new ProjectProfile { Name = "לקוח", Guide = "Guide" });
        Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out _)); Assert.NotNull(gemini.CustomGuidePrompt);
        Assert.False(vm.TryConfigureCustomGuideEngine(vm.AutoTranslators.First(t => t is not GeminiTranslate), out var error)); Assert.Contains("רק במנוע Google Gemini", error); Assert.Null(gemini.CustomGuidePrompt);
        vm.CustomGuideEnabled = false; Assert.Contains("המדריך כבוי", vm.CustomGuideStatus);
        Assert.True(vm.TryConfigureCustomGuideEngine(gemini, out _)); Assert.Null(gemini.CustomGuidePrompt);
        vm.ConfigureCustomGuide(null); Assert.False(vm.CustomGuideAvailable); Assert.Contains("ללא מדריך", vm.CustomGuideStatus);
    }

    [AvaloniaFact]
    public async Task WholeFileAndSelectedNativeTranslationDialogsShowTheActiveGuideWithoutSendingOrEditing()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt");
            FeedbackSet(vm, "_subtitleFileName", path); FeedbackSet(vm, "_customDocument", path); FeedbackSet(vm, "_customGeneration", Field<int>(vm, "_ostDocumentGeneration"));
            var profile = new ProjectProfile { Name = "מדריך תרגום ניסיוני", Guide = "Use approved names" };
            Field<System.Collections.Generic.List<ProjectProfile>>(vm, "_customProfiles").Add(profile); Field<ProjectWork>(vm, "_customWork").ProfileId = profile.Id;
            var row = new SubtitleLineViewModel { Number = 1, Text = "Hello there", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) };
            vm.Subtitles.Clear(); vm.Subtitles.Add(row); vm.SubtitleGrid.SelectedItem = row; await FeedbackDrain();
            var hash = vm.GetFastHash(); var before = JsonSerializer.Serialize(profile);
            foreach (var selected in new[] { false, true })
            {
                var task = selected ? vm.AutoTranslateSelectedLinesCommand.ExecuteAsync(null) : vm.ShowAutoTranslateCommand.ExecuteAsync(null);
                await FeedbackDrain();
                var dialog = Assert.Single(owner.OwnedWindows.OfType<AutoTranslateWindow>());
                var translateVm = (AutoTranslateViewModel)dialog.DataContext!;
                Assert.True(translateVm.CustomGuideAvailable); Assert.True(translateVm.CustomGuideEnabled);
                var check = Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>(), c => c.Name == "CustomNativeGuideEnabled"); Assert.True(check.IsChecked);
                var status = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "CustomNativeGuideStatus"); Assert.Contains(profile.Name, status.Text);
                Assert.False(translateVm.OkPressed); dialog.Close(); await task;
                Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(before, JsonSerializer.Serialize(profile));
            }
            var session = vm.CaptureNativeGuideSession(); Assert.True(vm.IsNativeGuideSessionCurrent(session));
            profile.Guide += " changed"; Assert.False(vm.IsNativeGuideSessionCurrent(session)); profile.Guide = session.Profile!.Guide;
            row.Text += " changed"; Assert.False(vm.IsNativeGuideSessionCurrent(session));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".sework.json")));
        }
        finally { FeedbackSet((MainViewModel)main.DataContext!, "_subtitleFileName", ""); FeedbackSet((MainViewModel)main.DataContext!, "_customDocument", ""); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }
}
