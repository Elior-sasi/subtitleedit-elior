using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
    private static QcRow LocalNameRow(string text, int number = 1) => new(Guid.NewGuid(), number, text, TimeSpan.FromSeconds(number), TimeSpan.FromSeconds(number + 1), 10);
    [Fact]
    public void LocalNameDifferenceFindsMissingLetterConjunctionAndTranspositionWithoutClaimingIdentity()
    {
        var rows = new[] { LocalNameRow("נרוטו"), LocalNameRow("ונרוטו", 2), LocalNameRow("נארטוו", 3) };
        var result = LocalNameSuggestions.Scan(rows, new[] { new QcTerm("נארוטו", Array.Empty<string>()) }, "");
        Assert.Equal(3, result.Hits.Count); Assert.All(result.Hits, hit =>
        { Assert.Contains("כתיב שונה", hit.Message); Assert.Contains("אין קביעה שזה אותו אדם", hit.Message); Assert.Contains("נארוטו", hit.Similar); Assert.DoesNotContain("✓", hit.Message); });
        Assert.Equal("נרוטו", rows[0].Text);
    }
    [Fact]
    public void LocalNameDifferenceKeepsAllAmbiguousMatchesAndNeverFlagsKnownNamesVariantsOrCommonWords()
    {
        var terms = new[] { new QcTerm("מיקאל", Array.Empty<string>()), new QcTerm("מיהאל", Array.Empty<string>()), new QcTerm("נארוטו", new[] { "נרוטו" }), new QcTerm("משה", Array.Empty<string>()) };
        var result = LocalNameSuggestions.Scan(new[] { LocalNameRow("מיכאל"), LocalNameRow("מיקאל ומיהאל נארוטו ונארוטו נרוטו אנחנו רוצה משה") }, terms, "");
        var hit = Assert.Single(result.Hits); Assert.Equal(2, hit.Similar.Length); Assert.Contains("מיקאל", hit.Similar); Assert.Contains("מיהאל", hit.Similar);
        Assert.False(LocalNameSuggestions.OneEdit("נארוטו", "נטטוו"));
        var shortNames = LocalNameSuggestions.Scan(new[] { LocalNameRow("משא") }, new[] { new QcTerm("משה", Array.Empty<string>()) }, "");
        Assert.Empty(shortNames.Hits); Assert.Contains(shortNames.KnownNames!, n => n.Text == "משה");
    }
    [Fact]
    public void LocalGuideNamesNeedExplicitFieldsAndNeverBecomeApprovedGlossary()
    {
        var guide = "Naruto = נארוטו\nשם: סאקורה\nלא לרכך את הדיאלוג.";
        var terms = new List<QcTerm>(); var before = JsonSerializer.Serialize(terms);
        var result = LocalNameSuggestions.Scan(new[] { LocalNameRow("נרוטו סקורה") }, terms, guide);
        Assert.Equal(2, result.Anchors); Assert.Equal(2, result.Hits.Count); Assert.All(result.Hits, h => Assert.True(h.HasGuideAnchors));
        Assert.Equal(before, JsonSerializer.Serialize(terms));
        Assert.Empty(LocalNameSuggestions.Anchors(terms, "סאקורה היא דמות חשובה. שמור את השם נארוטו."));
    }
    [Fact]
    public void LocalNameDifferenceIgnoresAttributesOverridesAndNiqqudAndKeepsPhraseBoundaries()
    {
        var terms = new[] { new QcTerm("נארוטו", Array.Empty<string>()), new QcTerm("הארי פוטר", Array.Empty<string>()) };
        Assert.Empty(LocalNameSuggestions.Scan(new[] { LocalNameRow("<i title='נרוטו'>נָארוטו</i> {\\pos(1,2)}נארוטו\nהארי, פטר") }, terms, "").Hits);
        var hit = Assert.Single(LocalNameSuggestions.Scan(new[] { LocalNameRow("<i>הארי\nפטר</i>") }, terms, "").Hits);
        Assert.Contains("הארי פוטר", hit.Similar);
        Assert.Equal("ג'ון", LocalNameSuggestions.Normalize("ג׳ון"));
    }
    [Fact]
    public void LocalNameDifferenceIsBoundedCancellableAndReportsLimitedResultsExplicitly()
    {
        Assert.Throws<OperationCanceledException>(() => LocalNameSuggestions.Scan(Array.Empty<QcRow>(), Array.Empty<QcTerm>(), "", new CancellationToken(true)));
        Assert.Throws<InvalidDataException>(() => LocalNameSuggestions.Scan(new[] { LocalNameRow(new string('א', 1_000_001)) }, Array.Empty<QcTerm>(), ""));
        Assert.Throws<InvalidDataException>(() => LocalNameSuggestions.Anchors(Array.Empty<QcTerm>(), new string('א', 200_001)));
        var result = LocalNameSuggestions.Scan(Enumerable.Range(1, 501).Select(i => LocalNameRow("נרוטו", i)).ToArray(), new[] { new QcTerm("נארוטו", Array.Empty<string>()) }, "");
        Assert.True(result.Limited); Assert.Equal(500, result.Hits.Count);
    }
    [AvaloniaFact]
    public async Task ActualLocalNameDialogScansOfflineAndMarkersAreDisplayOnlyAndInvalidateOnEdits()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt"); FeedbackSet(vm, "_subtitleFileName", path); FeedbackSet(vm, "_customDocument", path); FeedbackSet(vm, "_customGeneration", Field<int>(vm, "_ostDocumentGeneration"));
            var profile = new ProjectProfile { Name = "בדיקה", Guide = "Naruto = נארוטו" }; Field<List<ProjectProfile>>(vm, "_customProfiles").Add(profile); Field<ProjectWork>(vm, "_customWork").ProfileId = profile.Id;
            var row = new SubtitleLineViewModel { Number = 1, Text = "נרוטו", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) };
            var reference = new SubtitleLineViewModel { Number = 2, Text = "נרוטו", IsReferenceOnly = true };
            vm.Subtitles.Clear(); vm.Subtitles.Add(row); vm.Subtitles.Add(reference); var hash = vm.GetFastHash(); var original = JsonSerializer.Serialize(profile);
            var toolbar = Assert.Single(main.GetVisualDescendants().OfType<Button>(), b => b.Name == "CustomQcToolbarButton");
            toolbar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FeedbackDrain();
            var menu = Field<MenuFlyout>(vm, "_customQcMenu");
            var entry = Assert.Single(menu.Items.OfType<MenuItem>(), item => (item.Header as TextBlock)?.Text == "כתיב שונה בשמות — בדיקה מקומית ללא AI…");
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Hide(); await FeedbackDrain();
            var dialog = Assert.Single(owner.OwnedWindows, w => w.Name == "LocalNameSuggestionsDialog");
            var scan = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "בדוק את כל השמות והצג כתיב שונה"); scan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 500 && vm.CurrentLocalNameSuggestions().Count == 0; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Single(vm.CurrentLocalNameSuggestions()); Assert.StartsWith("? כתיב שונה", row.CustomNameSummary); Assert.Contains("נרוטו", row.AccessibleErrorText); Assert.Empty(reference.CustomNameSummary);
            Assert.Equal(hash, vm.GetFastHash()); Assert.Equal(original, JsonSerializer.Serialize(profile)); Assert.False(File.Exists(Path.ChangeExtension(path, ".sework.json")));
            dialog.Close(); await FeedbackDrain();
            row.Text = "נארוטו"; vm.ScanCustomQc(); Assert.Empty(vm.CurrentLocalNameSuggestions()); Assert.DoesNotContain("כתיב שונה", row.CustomNameSummary);
            row.Text = "נרוטו"; vm.SetLocalNameSuggestions(LocalNameSuggestions.Scan(new[] { LocalNameRow(row.Text) with { Id = row.Id } }, Array.Empty<QcTerm>(), profile.Guide));
            profile.Guide = "שם: סאקורה"; vm.ScanCustomQc(); Assert.Empty(vm.CurrentLocalNameSuggestions());
        }
        finally { FeedbackSet((MainViewModel)main.DataContext!, "_subtitleFileName", ""); FeedbackSet((MainViewModel)main.DataContext!, "_customDocument", ""); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }
}
