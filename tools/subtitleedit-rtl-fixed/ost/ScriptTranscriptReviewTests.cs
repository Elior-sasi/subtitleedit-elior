using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
using Nikse.SubtitleEdit.Features.Video.SpeechToText;
using Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    private static ScriptTranscriptReview.Cue Speech(double start, string text) => new(start, start + 1800, text);

    [Fact]
    public void HebrewScriptOmissionsAreProposalsOnlyAndKeepExactOriginalOnApproval()
    {
        var script = new[] { "אני לא יודע מה אתה רוצה ממני." };
        var speech = new[] { Speech(1200, "לא יודע מה אתה רוצה.") };
        var match = Assert.Single(ScriptTranscriptReview.Compare(script, speech)); Assert.NotNull(match); Assert.Equal(0, match.First);
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, Array.Empty<ScriptTranscriptReview.Decision>()));
        var diff = ScriptTranscriptReview.Diff(script[0], speech[0].Text); Assert.Equal(new[] { "אני", "ממני" }, diff.ScriptOnly); Assert.Empty(diff.TranscriptOnly);
        var decision = new ScriptTranscriptReview.Decision(0, 0, 0, ScriptTranscriptReview.TextChoice.Script);
        var kept = Assert.Single(ScriptTranscriptReview.Build(script, speech, new[] { decision }));
        Assert.Equal(script[0], kept.Text); Assert.Equal(1200, kept.Start); Assert.Equal(3000, kept.End);
        Assert.Equal(speech[0].Text, Assert.Single(ScriptTranscriptReview.Build(script, speech, new[] { decision with { Choice = ScriptTranscriptReview.TextChoice.Transcript } })).Text);
        Assert.Equal("אני לא יודע מה אתה רוצה ממני.", script[0]); Assert.Equal("לא יודע מה אתה רוצה.", speech[0].Text);
    }

    [Fact]
    public void RepeatedHebrewAndMissingSceneNeverGetAutomaticAcceptance()
    {
        var speech = new[] { Speech(0, "שלום לך"), Speech(4000, "איך עבר היום שלך"), Speech(8000, "שלום לך") };
        var proposals = ScriptTranscriptReview.Compare(new[] { "שלום לך", "המכונית הירוקה נעלמה ברחוב" }, speech);
        Assert.True(proposals[0]!.Ambiguous); Assert.Null(proposals[1]);
        Assert.Null(Assert.Single(ScriptTranscriptReview.Compare(new[] { "כן" }, new[] { Speech(0, "כן בהחלט") })));
    }

    [Fact]
    public void HebrewSubstitutionsAndExtraSpeechStayVisible()
    {
        var diff = ScriptTranscriptReview.Diff("אנחנו הולכים הביתה עכשיו", "אנחנו נוסעים הביתה עכשיו באמת");
        Assert.Equal(new[] { "הולכים" }, diff.ScriptOnly); Assert.Equal(new[] { "נוסעים", "באמת" }, diff.TranscriptOnly);
        var proposals = ScriptTranscriptReview.Compare(new[] { "אנחנו הולכים הביתה עכשיו" }, new[] { Speech(0, "אנחנו נוסעים הביתה עכשיו באמת"), Speech(5000, "דיבור נוסף שלא קיים בתסריט") });
        Assert.Equal(0, proposals[0]!.Last);
    }

    [Fact]
    public void ExactScriptMarkupDirectionMarksAndPunctuationSurviveComparison()
    {
        var script = "<font face=\"A B\">שָׁלוֹם\u200f, עולם!</font>\n{\\i1}מה קורה?";
        var rows = ScriptTranscriptReview.PrepareRows(script + "\n\nקטע אחר"); Assert.Equal(script, rows[0]);
        var speech = new[] { Speech(0, "שלום עולם מה קורה") };
        Assert.Empty(ScriptTranscriptReview.Diff(script, speech[0].Text).ScriptOnly);
        var output = ScriptTranscriptReview.Build(new[] { script }, speech, new[] { new ScriptTranscriptReview.Decision(0, 0, 0, ScriptTranscriptReview.TextChoice.Script) });
        Assert.Equal(script, output[0].Text);
    }

    [Fact]
    public void MultiCueProposalUsesOnlyRealTranscriptBoundariesAndRejectsLongSilence()
    {
        var script = new[] { "אנחנו צריכים לחזור הביתה עכשיו" };
        var speech = new[] { Speech(1000, "אנחנו צריכים"), Speech(3000, "לחזור הביתה עכשיו") };
        var match = ScriptTranscriptReview.Compare(script, speech)[0]!; Assert.Equal(0, match.First); Assert.Equal(1, match.Last); Assert.Equal(1, match.Similarity);
        var d = new ScriptTranscriptReview.Decision(0, 0, 1, ScriptTranscriptReview.TextChoice.Script);
        var output = Assert.Single(ScriptTranscriptReview.Build(script, speech, new[] { d })); Assert.Equal(1000, output.Start); Assert.Equal(4800, output.End);
        speech[1] = Speech(10000, "לחזור הביתה עכשיו");
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, new[] { d }));
    }

    [Fact]
    public void ReviewRejectsDuplicateMappingsOverlapReverseOrderAndUndecidedRows()
    {
        var script = new[] { "אחד", "שתיים" }; var speech = new[] { Speech(0, "אחד"), Speech(4000, "שתיים") };
        var a = new ScriptTranscriptReview.Decision(0, 0, 0, ScriptTranscriptReview.TextChoice.Script);
        var b = new ScriptTranscriptReview.Decision(1, 1, 1, ScriptTranscriptReview.TextChoice.Script);
        Assert.Equal(2, ScriptTranscriptReview.Build(script, speech, new[] { a, b }).Count);
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, new[] { a }));
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, new[] { a, b with { First = 0, Last = 0 } }));
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, new[] { a with { First = 1, Last = 1 }, b with { First = 0, Last = 0 } }));
        speech[1] = Speech(1000, "שתיים"); Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, new[] { a, b }));
    }

    [Fact]
    public void DeletedScriptSceneRequiresExplicitSkipAndNeverInventsTimes()
    {
        var script = new[] { "שלום לך", "סצנה שנחתכה" }; var speech = new[] { Speech(1000, "שלום לך") };
        var decisions = new[] { new ScriptTranscriptReview.Decision(0, 0, 0, ScriptTranscriptReview.TextChoice.Script), new ScriptTranscriptReview.Decision(1, -1, -1, ScriptTranscriptReview.TextChoice.Script, true) };
        Assert.Single(ScriptTranscriptReview.Build(script, speech, decisions));
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Build(script, speech, decisions.Select(d => d with { Skip = true }).ToArray()));
    }

    [Theory]
    [InlineData("1\n00:60:00,000 --> 00:60:02,000\nשלום")]
    [InlineData("1\n00:00:02,000 --> 00:00:01,000\nשלום")]
    [InlineData("1\n00:00:00,000 --> 00:00:02,000\n")]
    public void InvalidTranscriptCannotSilentlyDropRows(string text) => Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.ReadSrt(text));

    [Fact]
    public void SrtPreservesLongHoursAndLiteralTextWithoutFormulaOrMarkupExecution()
    {
        var cues = ScriptTranscriptReview.ReadSrt("\uFEFF1\r\n125:01:02,003 --> 125:01:04,005\r\n<font face=\"A B\">שלום!</font>\r\n=1+1");
        Assert.Equal(450062003, cues[0].Start); Assert.Equal("<font face=\"A B\">שלום!</font>\n=1+1", cues[0].Text);
    }

    [Fact]
    public void ComparisonIsBoundedCancellableAndCannotNormalizeSourceInPlace()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ScriptTranscriptReview.Compare(new[] { "שלום" }, new[] { Speech(0, "שלום") }, cancel.Token));
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.PrepareRows(new string('א', ScriptImport.MaxText + 1)));
        Assert.Throws<InvalidDataException>(() => ScriptTranscriptReview.Validate(new[] { new ScriptTranscriptReview.Cue(double.NaN, 10, "שלום") }));
    }

    [AvaloniaFact]
    public void ScriptReviewTranscriptionIsLocalHebrewAndDoesNotSaveOverExistingPreferences()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var vm = Locator.Services.GetRequiredService<SpeechToTextViewModel>();
        var before = JsonSerializer.Serialize(Se.Settings.Tools.AudioToText);
        vm.InitializeCustomScriptReview("example.wav");
        Assert.IsType<WhisperCppEngine>(Assert.Single(vm.Engines)); Assert.IsType<WhisperCppEngine>(vm.SelectedEngine);
        Assert.Equal("he", vm.SelectedLanguage?.Code); Assert.False(vm.DoTranslateToEnglish); Assert.False(vm.IsBatchMode);
        typeof(SpeechToTextViewModel).GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        Assert.Equal(before, JsonSerializer.Serialize(Se.Settings.Tools.AudioToText));
    }

    [AvaloniaFact]
    public async Task RealScriptReviewDialogCanOpenAndCancelWithoutChangingCurrentDocument()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            vm.Subtitles.Clear(); var original = new SubtitleLineViewModel { Text = "המסמך המקורי", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) }; vm.Subtitles.Add(original);
            var method = typeof(MainViewModel).GetMethod("OpenScriptTranscriptReview", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(vm, new object[] { "אני לא יודע מה אתה רוצה ממני.", "" })!;
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.Where(w => w.Title == "תסריט שונה מהדיבור — השוואה ואישור"));
            Assert.Contains("תסריט שונה מהדיבור", dialog.Title);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "אני לא יודע מה אתה רוצה ממני.");
            Assert.Same(original, Assert.Single(vm.Subtitles)); Assert.Equal("המסמך המקורי", original.Text);
            dialog.Close(); await task;
            Assert.Same(original, Assert.Single(vm.Subtitles)); Assert.Equal(TimeSpan.FromSeconds(1), original.StartTime); Assert.Equal(TimeSpan.FromSeconds(3), original.EndTime);
        }
        finally { window.Close(); }
    }
}

