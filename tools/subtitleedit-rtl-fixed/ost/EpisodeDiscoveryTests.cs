using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    private static string DiscoverySrt(string text) => "1\r\n00:00:01,000 --> 00:00:03,000\r\n" + text + "\r\n";
    [Fact]
    public void DiscoveryEpisodeIdentityAcceptsExplicitSeriesAndRejectsRangesSpecialsAndBareNumbers()
    {
        Assert.Equal(new EpisodeIdentity("show name", 2, 7), EpisodeContextDiscovery.Identify("[Group] Show.Name.S02E07.he.srt"));
        Assert.Equal(new EpisodeIdentity("show name", 2, 7), EpisodeContextDiscovery.Identify("Show Name - 2x07.en.SRT"));
        Assert.Equal(new EpisodeIdentity("סדרה", 1, 3), EpisodeContextDiscovery.Identify("סדרה.S01E03.srt"));
        Assert.NotEqual(EpisodeContextDiscovery.Identify("Show (2020).S01E03.srt")!.Series, EpisodeContextDiscovery.Identify("Show (2023).S01E03.srt")!.Series);
        foreach (var name in new[] { "Show.07.srt", "S01E07.srt", "Show.S01E01E02.srt", "Show.S01E01-E02.srt", "Show.S01E01-02.srt", "Show.S01E01.S02E02.srt", "Show.S00E01.srt", "Show.S01E00.srt" }) Assert.Null(EpisodeContextDiscovery.Identify(name));
    }
    [Fact]
    public void DiscoveryFindsOnlyPreviousSameSeriesEpisodesAndMarksVersionsWithoutReadingOrEditingText()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "Show.S02E03.srt", "Show.S02E03.en.srt", "Show.S02E04.srt", "Show.S03E01.srt", "Other.S01E01.srt", "Show.S02E01E02.srt", "Show.S02E02.he.srt", "Show.S02E02.en.srt" }) File.WriteAllText(Path.Combine(root, name), DiscoverySrt(name));
            var season = Path.Combine(root, "Season 1"); Directory.CreateDirectory(season); File.WriteAllText(Path.Combine(season, "Show.1x10.srt"), DiscoverySrt("older season"));
            var originals = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var result = EpisodeContextDiscovery.Scan(root, Path.Combine(root, "Show.S02E03.srt"));
            Assert.Equal(3, result.Candidates.Count); Assert.Equal(2, result.Candidates.Count(c => c.MultipleVersions));
            Assert.Equal(new[] { (2, 2), (2, 2), (1, 10) }, result.Candidates.Select(c => (c.Season, c.Episode)));
            foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
            var imported = EpisodeContextDiscovery.ImportApproved(new[] { result.Candidates[0] }); Assert.Single(imported); Assert.Contains(result.Candidates[0].Name, imported[0].Name); Assert.True(imported[0].IncludeInAi);
            Assert.Throws<InvalidDataException>(() => EpisodeContextDiscovery.ImportApproved(Array.Empty<EpisodeCandidate>()));
            Assert.Throws<OperationCanceledException>(() => EpisodeContextDiscovery.Scan(root, "Show.S02E03.srt", new CancellationToken(true)));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void DiscoveryRechecksChangedFilesAndRejectsInvalidSrtBeforeAnyContextCanBeMerged()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Show.S01E01.srt"); File.WriteAllText(path, DiscoverySrt("first"));
            var candidate = Assert.Single(EpisodeContextDiscovery.Scan(root, Path.Combine(root, "Show.S01E02.srt")).Candidates);
            File.WriteAllText(path, DiscoverySrt("changed and longer"));
            Assert.Throws<InvalidDataException>(() => EpisodeContextDiscovery.ReadVerified(candidate));
            File.WriteAllText(path, "not a subtitle"); candidate = Assert.Single(EpisodeContextDiscovery.Scan(root, "Show.S01E02.srt").Candidates);
            Assert.Throws<InvalidDataException>(() => EpisodeContextDiscovery.ImportApproved(new[] { candidate }));
            Assert.Throws<OperationCanceledException>(() => EpisodeContextDiscovery.ReadVerified(candidate, new CancellationToken(true)));
            Assert.Throws<InvalidDataException>(() => EpisodeContextDiscovery.Scan(root, "unnamed.srt"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void DiscoveryFolderPreferencesAreLocalPerProjectAndScanBoundsFailWithoutPartialImport()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var other = Path.Combine(root, "other"); Directory.CreateDirectory(other); var preferences = Path.Combine(root, "folders.json");
            var settings = Path.Combine(root, "Settings.json"); File.WriteAllText(settings, "untouched settings"); var before = File.ReadAllBytes(settings);
            EpisodeContextDiscovery.SaveFolder(preferences, "one", root); EpisodeContextDiscovery.SaveFolder(preferences, "two", other);
            Assert.Equal(root, EpisodeContextDiscovery.LoadFolder(preferences, "one")); Assert.Equal(other, EpisodeContextDiscovery.LoadFolder(preferences, "two")); Assert.Equal("", EpisodeContextDiscovery.LoadFolder(preferences, "unknown")); Assert.Equal(before, File.ReadAllBytes(settings));
            for (var i = 0; i < EpisodeContextDiscovery.MaxDirectories + 1; i++) Directory.CreateDirectory(Path.Combine(root, "d" + i));
            Assert.Throws<InvalidDataException>(() => EpisodeContextDiscovery.Scan(root, "Show.S02E01.srt"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task WaitForDiscovery(Window dialog, string expected)
    {
        for (var i = 0; i < 500; i++)
        {
            await Task.Delay(10); Dispatcher.UIThread.RunJobs();
            if (dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "EpisodeDiscoveryStatus" && (t.Text ?? "").Contains(expected))) return;
        }
        Assert.Fail("Discovery did not complete: " + string.Join(" | ", dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name is "EpisodeDiscoveryStatus" or "CustomActionError").Select(t => t.Text)));
    }
    [AvaloniaFact]
    public async Task RememberedEpisodeFolderScansUncheckedAndNeedsApprovalThenSaveWhileCancelPreservesEverything()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var preferences = Path.Combine(Se.DataFolder, "CustomProjects", "episode-discovery-folders.json"); var oldPreferences = File.Exists(preferences) ? File.ReadAllBytes(preferences) : null;
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            var current = Path.Combine(root, "Show.S01E03.srt"); var prior = Path.Combine(root, "Show.S01E02.he.srt");
            File.WriteAllText(current, DiscoverySrt("current original")); File.WriteAllText(prior, DiscoverySrt("prior approved candidate")); var sourceBytes = File.ReadAllBytes(prior);
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
            FeedbackSet(vm, "_subtitleFileName", current); FeedbackSet(vm, "_customDocument", current); FeedbackSet(vm, "_customGeneration", Field<int>(vm, "_ostDocumentGeneration"));
            var profile = new ProjectProfile { Name = "Show" }; Field<List<ProjectProfile>>(vm, "_customProfiles").Add(profile); Field<ProjectWork>(vm, "_customWork").ProfileId = profile.Id;
            var row = new SubtitleLineViewModel { Number = 1, Text = "current original", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) }; vm.Subtitles.Clear(); vm.Subtitles.Add(row); var hash = vm.GetFastHash();
            EpisodeContextDiscovery.SaveFolder(preferences, profile.Id, root);
            var task = (Task)typeof(MainViewModel).GetMethod("OpenEpisodeContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null)!; await FeedbackDrain();
            var dialog = Assert.Single(owner.OwnedWindows, w => w.Name == "PreviousEpisodesDialog"); await WaitForDiscovery(dialog, "נמצאו 1");
            var check = Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>(), c => c.Name == "EpisodeDiscoveryCandidate"); Assert.False(check.IsChecked);
            Assert.Empty(profile.PreviousEpisodes); Assert.Empty(profile.Memory); Assert.Equal("[]", ProjectEpisodeContext.PromptContext(profile, "prior"));
            check.IsChecked = true;
            var import = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "צרף לטיוטת הרקע רק את המועמדים שסימנתי"); import.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await WaitForDiscovery(dialog, "לטיוטה בלבד");
            Assert.Empty(profile.PreviousEpisodes); Assert.Empty(profile.Terms); Assert.Empty(profile.Memory); Assert.Equal(hash, vm.GetFastHash());
            dialog.Close(); await task;
            Assert.Empty(profile.PreviousEpisodes); Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(sourceBytes, File.ReadAllBytes(prior)); Assert.Equal(DiscoverySrt("current original"), File.ReadAllText(current));
        }
        finally
        {
            FeedbackSet((MainViewModel)main.DataContext!, "_subtitleFileName", ""); FeedbackSet((MainViewModel)main.DataContext!, "_customDocument", ""); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close();
            if (oldPreferences == null) { if (File.Exists(preferences)) File.Delete(preferences); } else File.WriteAllBytes(preferences, oldPreferences);
            Directory.Delete(root, true);
        }
    }
}
