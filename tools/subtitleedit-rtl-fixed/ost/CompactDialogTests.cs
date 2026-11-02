using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    private static void CheckCompactFooter(Window dialog, string name)
    {
        dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var actions = Assert.Single(dialog.GetVisualDescendants().OfType<StackPanel>(), p => p.Name == "CustomDialogActions");
        var body = Assert.Single(dialog.GetVisualDescendants().OfType<ScrollViewer>(), p => p.Name == "CustomDialogBody");
        Assert.DoesNotContain(body.GetVisualDescendants(), x => ReferenceEquals(x, actions));
        Assert.True(actions.Bounds.Height > 30); Assert.True(body.Bounds.Height > 40);
        Assert.True(body.Bounds.Bottom <= actions.Bounds.Top + 1);
        var dock = Assert.IsType<DockPanel>(dialog.Content); Assert.True(actions.Bounds.Bottom <= dock.Bounds.Height + 1);
        foreach (var button in actions.Children.OfType<Button>().Where(b => b.IsVisible))
        { Assert.True(button.Bounds.Height >= 25); Assert.True(button.Bounds.Bottom <= actions.Bounds.Height + 1); Assert.True(button.Bounds.Right <= actions.Bounds.Width + 1); }
        Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "CustomDialogClose");
        var before = actions.Bounds; body.Offset = new Vector(0, 10000); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Assert.Equal(before, actions.Bounds);
        body.Offset = default; dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } workspace)
        {
            var dir = Path.Combine(workspace, "dialog-layout-smoke"); Directory.CreateDirectory(dir);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
            using var file = File.Create(Path.Combine(dir, name + ".png")); frame!.Save(file, PngBitmapEncoderOptions.Default);
        }
    }
    [AvaloniaFact]
    public async Task FourActualDialogsKeepPrimaryActionsOutsideScrollingAndCancelWithoutChangingData()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt"); FeedbackSet(vm, "_subtitleFileName", path); FeedbackSet(vm, "_customDocument", path); FeedbackSet(vm, "_customGeneration", Field<int>(vm, "_ostDocumentGeneration"));
            var profile = new ProjectProfile { Name = "סדרת בדיקה", Guide = "Naruto = נארוטו\n" + string.Join("\n", Enumerable.Repeat("הנחיית בדיקה", 40)) };
            Field<List<ProjectProfile>>(vm, "_customProfiles").Add(profile); Field<ProjectWork>(vm, "_customWork").ProfileId = profile.Id;
            vm.Subtitles.Clear(); vm.Subtitles.Add(new SubtitleLineViewModel { Number = 1, Text = "נרוטו שָׁלוֹם", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) });
            var hash = vm.GetFastHash(); var original = JsonSerializer.Serialize(profile);
            foreach (var method in new[] { "OpenGuideDraft", "OpenProjectProfile", "OpenLocalNameSuggestions", "OpenNiqqudRemoval" })
            {
                var task = (Task)typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!; await FeedbackDrain();
                var dialog = Assert.Single(owner.OwnedWindows, w => w.Name is "GuideDraftDialog" or "ProjectProfileDialog" or "LocalNameSuggestionsDialog" or "NiqqudRemovalDialog");
                foreach (var (width, height) in new[] { (780, 680), (620, 480) })
                { dialog.Width = width; dialog.Height = height; await FeedbackDrain(); CheckCompactFooter(dialog, method + "-" + width); }
                if (method == "OpenGuideDraft")
                {
                    var advanced = Assert.Single(dialog.GetVisualDescendants().OfType<Expander>(), e => e.Name == "GuideDraftAdvanced"); Assert.False(advanced.IsExpanded);
                    Assert.False(Assert.Single(dialog.GetVisualDescendants().OfType<StackPanel>(), p => p.Name == "GuideDraftEditor").IsVisible);
                    advanced.IsExpanded = true; await FeedbackDrain();
                    Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "צור שלד מקומי ללא AI").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FeedbackDrain();
                    Assert.False(advanced.IsExpanded); Assert.True(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "GuideDraftSave").IsEnabled);
                    CheckCompactFooter(dialog, "GuideDraftEditor-620");
                    advanced.IsExpanded = true; await FeedbackDrain();
                    Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), b => b.Name == "GuideDraftEdition").Text = "גרסה שונה"; await FeedbackDrain();
                    Assert.False(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "GuideDraftSave").IsEnabled);
                }
                if (method == "OpenNiqqudRemoval")
                {
                    var remove = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "NiqqudRemovalApply");
                    var all = Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>()); all.IsChecked = true; await FeedbackDrain(); Assert.True(remove.IsEnabled);
                    CheckCompactFooter(dialog, "NiqqudPreview-620");
                }
                Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(original, JsonSerializer.Serialize(profile));
                Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "CustomDialogClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await task;
                Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(original, JsonSerializer.Serialize(profile));
            }
            Assert.False(File.Exists(Path.ChangeExtension(path, ".sework.json")));
        }
        finally { FeedbackSet((MainViewModel)main.DataContext!, "_subtitleFileName", ""); FeedbackSet((MainViewModel)main.DataContext!, "_customDocument", ""); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }
    [AvaloniaFact]
    public async Task ScriptAndEpisodeDialogsKeepActionsVisibleAcrossStagesAndCancelWithoutEdits()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt"); FeedbackSet(vm, "_subtitleFileName", path); FeedbackSet(vm, "_customDocument", path); FeedbackSet(vm, "_customGeneration", Field<int>(vm, "_ostDocumentGeneration"));
            var profile = new ProjectProfile { Name = "בדיקת חלונות", PreviousEpisodes = new() { new() { Name = "Show.S01E01.he.srt", Text = "חומר רקע לבדיקה", CueCount = 1, IncludeInAi = true } } };
            Field<List<ProjectProfile>>(vm, "_customProfiles").Add(profile); Field<ProjectWork>(vm, "_customWork").ProfileId = profile.Id;
            vm.Subtitles.Clear(); vm.Subtitles.Add(new SubtitleLineViewModel { Number = 1, Text = "הטקסט המקורי", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) });
            var hash = vm.GetFastHash(); var original = JsonSerializer.Serialize(profile);
            foreach (var method in new[] { "OpenScriptToSubtitles", "OpenScriptTranscriptReview", "OpenEpisodeContext" })
            {
                var args = method == "OpenScriptTranscriptReview" ? new object[] { "תסריט לבדיקה בלבד", "" } : null;
                var task = (Task)typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!; await FeedbackDrain();
                var dialog = Assert.Single(owner.OwnedWindows, w => w.Name is "ScriptImportDialog" or "ScriptReviewDialog" or "PreviousEpisodesDialog");
                foreach (var (width, height) in new[] { (780, 680), (620, 480) })
                { dialog.Width = width; dialog.Height = height; await FeedbackDrain(); CheckCompactFooter(dialog, method + "-" + width); }
                if (method == "OpenScriptToSubtitles")
                {
                    var options = Assert.Single(dialog.GetVisualDescendants().OfType<Expander>(), e => e.Name == "ScriptImportOptions"); Assert.False(options.IsExpanded);
                    var routes = Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "ScriptTimingRoute"); Assert.Equal(3, routes.Items.Count); Assert.Equal(0, routes.SelectedIndex);
                    for (var i = 0; i < 3; i++) { routes.SelectedIndex = i; await FeedbackDrain(); CheckCompactFooter(dialog, "ScriptRoute-" + i); }
                    options.IsExpanded = true; await FeedbackDrain();
                    Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "התקן / בחר מנוע ומודל תזמון…");
                    options.IsExpanded = false;
                    var next = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "ScriptImportContinue"); next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FeedbackDrain();
                    Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "CustomActionError" && t.Text!.Contains("בחר קובץ שמע"));
                    Assert.False(task.IsCompleted); // Missing media cannot enter any engine or replace the document.
                }
                if (method == "OpenScriptTranscriptReview")
                {
                    var stages = Assert.Single(dialog.GetVisualDescendants().OfType<TabControl>(), t => t.Name == "ScriptReviewStages"); Assert.Equal(3, stages.Items.Count);
                    for (var i = 0; i < 3; i++) { stages.SelectedIndex = i; await FeedbackDrain(); CheckCompactFooter(dialog, "ScriptReviewStage-" + i); }
                    var import = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "ScriptReviewImport"); Assert.False(import.IsEnabled);
                    Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>()).IsChecked = true; await FeedbackDrain(); Assert.False(import.IsEnabled);
                    stages.SelectedIndex = 1; await FeedbackDrain(); Assert.False(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "ScriptReviewApproveRow").IsEnabled);
                    stages.SelectedIndex = 0; await FeedbackDrain();
                    Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "ScriptReviewText").Text = "תסריט ערוך זמני";
                }
                if (method == "OpenEpisodeContext")
                {
                    var discovery = Assert.Single(dialog.GetVisualDescendants().OfType<Expander>(), e => e.Name == "EpisodeDiscoverySection"); Assert.False(discovery.IsExpanded);
                    Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>()).IsChecked = false; await FeedbackDrain();
                    Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("0 מסומנים להקשר") == true);
                    discovery.IsExpanded = true; await FeedbackDrain(); CheckCompactFooter(dialog, "PreviousEpisodesDiscovery-620");
                    Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "צרף לטיוטת הרקע רק את המועמדים שסימנתי");
                }
                Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(original, JsonSerializer.Serialize(profile));
                Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "CustomDialogClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await task;
                Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(original, JsonSerializer.Serialize(profile));
            }
            Assert.False(File.Exists(Path.ChangeExtension(path, ".sework.json")));
        }
        finally { FeedbackSet((MainViewModel)main.DataContext!, "_subtitleFileName", ""); FeedbackSet((MainViewModel)main.DataContext!, "_customDocument", ""); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }
}
