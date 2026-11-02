using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    [Fact]
    public void NiqqudRemovalKeepsMarkupAttributesCantillationPunctuationDirectionsAndOtherLanguages()
    {
        var tags = "<font title=\"שָם > 'x'\">";
        var before = tags + "\u200fשָׁלוֹם־לְךָ!\u00a0\n{\\fnשָם}בַּיִת\u0591׀׃ café عَرَبِيّ</font>";
        var after = HebrewNiqqud.Remove(before);
        Assert.Equal(tags + "\u200fשלום־לך!\u00a0\n{\\fnשָם}בית\u0591׀׃ café عَرَبِيّ</font>", after);
        Assert.Equal(before.Length - after.Length, HebrewNiqqud.Count(before)); Assert.Equal(after, HebrewNiqqud.Remove(after));
        Assert.Equal("", HebrewNiqqud.Remove("")); Assert.Equal("אבג", HebrewNiqqud.Remove("אָבּגׇ"));
    }
    [Fact]
    public void GuideDraftSeparatesUnverifiedKnowledgeFromOfficialApprovalAndUsesOnlyExplicitSourceText()
    {
        var prompt = GuideDraft.Prompt("Avatar", "2026, סרט", "Original = מקור");
        Assert.Contains("No web search", prompt); Assert.Contains("unverified", prompt); Assert.Contains("ambiguous", prompt);
        Assert.Contains("URLs alone have NOT been fetched", prompt); Assert.DoesNotContain("Renovation Resort", prompt);
        Assert.Contains("אינה מדריך רשמי", GuideDraft.Template("Avatar", "2026"));
        Assert.Throws<InvalidDataException>(() => GuideDraft.Template("", ""));
        Assert.Throws<InvalidDataException>(() => GuideDraft.Prompt("x", "", new string('x', 60001)));
        Assert.ThrowsAny<Exception>(() => GuideDraft.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => GuideDraft.Parse("{\"guide\":\"\"}"));
        Assert.Throws<InvalidDataException>(() => GuideDraft.Parse(JsonSerializer.Serialize(new { guide = new string('x', 55001) })));
        Assert.StartsWith(GuideDraft.Notice, GuideDraft.Parse("{\"guide\":\"טיוטה\"}"));
    }
    [AvaloniaFact]
    public async Task NiqqudActualDialogPreviewCancelAndExplicitApplyHaveNativeUndoAndStaleGuards()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
            var row = new SubtitleLineViewModel { Number = 1, Text = "<i>שָׁלוֹם</i>", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) };
            var reference = new SubtitleLineViewModel { Number = 2, Text = "שָם", IsReferenceOnly = true };
            vm.Subtitles.Clear(); vm.Subtitles.Add(row); vm.Subtitles.Add(reference); var hash = vm.GetFastHash();
            var pending = (Task)typeof(MainViewModel).GetMethod("OpenNiqqudRemoval", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!; await FeedbackDrain();
            var dialog = Assert.Single(owner.OwnedWindows, w => w.Name == "NiqqudRemovalDialog");
            Assert.Equal(hash, vm.GetFastHash());
            Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>()).IsChecked = true; await FeedbackDrain();
            Assert.Contains("שלום", Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), b => b.Name == "NiqqudRemovalPreview").Text);
            Assert.Equal(hash, vm.GetFastHash()); dialog.Close(); await pending; Assert.Equal(hash, vm.GetFastHash());
            var generation = Field<int>(vm, "_ostDocumentGeneration");
            var originals = new Dictionary<Guid, string> { [row.Id] = row.Text };
            Assert.Throws<InvalidOperationException>(() => vm.ApplyNiqqudRemoval(originals, "wrong", generation, hash));
            Assert.Throws<InvalidOperationException>(() => vm.ApplyNiqqudRemoval(new Dictionary<Guid, string> { [reference.Id] = reference.Text }, "", generation, hash));
            vm.ApplyNiqqudRemoval(originals, "", generation, hash); Assert.Equal("<i>שלום</i>", row.Text); Assert.Equal("שָם", reference.Text);
            Assert.Equal(TimeSpan.FromSeconds(1), row.StartTime); Assert.Equal(TimeSpan.FromSeconds(3), row.EndTime);
            vm.UndoCommand.Execute(null); Assert.Equal("<i>שָׁלוֹם</i>", vm.Subtitles.First(r => r.Id == row.Id).Text);
        }
        finally { foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }
    [AvaloniaFact]
    public async Task GuideDraftActualLocalTemplateIsEditableAndCancelDoesNotChangeProjectOrDocument()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm); vm.StopBackgroundWork();
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt"); FeedbackSet(vm, "_subtitleFileName", path); FeedbackSet(vm, "_customDocument", path); FeedbackSet(vm, "_customGeneration", Field<int>(vm, "_ostDocumentGeneration"));
            var profile = new ProjectProfile { Name = "Avatar", Guide = "המדריך הקיים" }; Field<List<ProjectProfile>>(vm, "_customProfiles").Add(profile); Field<ProjectWork>(vm, "_customWork").ProfileId = profile.Id;
            var original = JsonSerializer.Serialize(profile); var hash = vm.GetFastHash();
            var pending = (Task)typeof(MainViewModel).GetMethod("OpenGuideDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!; await FeedbackDrain();
            var dialog = Assert.Single(owner.OwnedWindows, w => w.Name == "GuideDraftDialog");
            Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => (b.Content as string)?.StartsWith("חפש ברשת וצור מדריך עם מקורות") == true);
            var copy = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "העתק את המחקר שבדקתי לטיוטת המדריך");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FeedbackDrain();
            Assert.Contains("בצע חיפוש", Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "CustomActionError").Text);
            Assert.Equal(original, JsonSerializer.Serialize(profile));
            var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "צור שלד מקומי ללא AI"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await FeedbackDrain();
            var preview = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>(), b => b.Name == "GuideDraftPreview"); Assert.Contains("Avatar", preview.Text); Assert.False(preview.IsReadOnly);
            preview.Text += "\nהנחיה שלי";
            Assert.Equal(original, JsonSerializer.Serialize(profile)); Assert.Equal(hash, vm.GetFastHash()); dialog.Close(); await pending;
            Assert.Equal(original, JsonSerializer.Serialize(profile)); Assert.False(File.Exists(Path.ChangeExtension(path, ".sework.json")));
        }
        finally { FeedbackSet((MainViewModel)main.DataContext!, "_subtitleFileName", ""); FeedbackSet((MainViewModel)main.DataContext!, "_customDocument", ""); foreach (var w in owner.OwnedWindows.ToArray()) w.Close(); owner.Close(); }
    }
}
