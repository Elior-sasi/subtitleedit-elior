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
}
