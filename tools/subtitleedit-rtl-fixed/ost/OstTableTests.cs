using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic.Config;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Nikse.SubtitleEdit.Features.Tools.OstTable;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    [Theory]
    [InlineData("רציתי לדבר איתך, אבל כבר הלכת הביתה.", 42, 25, "רציתי לדבר איתך,\nאבל כבר הלכת הביתה.")]
    [InlineData("זה כל מה שאני רוצה לומר לך", 42, 30, "זה כל מה שאני רוצה לומר לך")]
    [InlineData("הטקסטהארוךהזה ממשיךבמיליםארוכות נוספותלבדיקה", 42, 25, null)]
    public void HebrewBreakUsesMeaningAndConfiguredLengthRatherThanWordCount(string input, int limit, int merge, string? expected)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, limit, merge, "he");
            Assert.Equal(input, actual.Replace(Environment.NewLine, " "));
            if (expected != null) Assert.Equal(expected.Replace("\n", Environment.NewLine), actual);
            else Assert.Contains(Environment.NewLine, actual);
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Theory]
    [InlineData("נפגשנו אתמול עם החברים ושוחחנו על כל התוכניות שלנו", "עם החברים")]
    [InlineData("הוא שלח את המכתב החשוב והמשיך בדרכו הביתה", "את המכתב")]
    [InlineData("מצאתי את הספר של אמא וחזרתי מיד הביתה", "של אמא")]
    [InlineData("הוא הניח את המכתב על השולחן וחזר מיד לעבודה", "על השולחן")]
    [InlineData("אנחנו מחפשים בית ספר מתאים לילדים בשנה הבאה", "בית ספר")]
    [InlineData("היום הגיע ראש הממשלה לביקור ארוך בעיר שלנו", "ראש הממשלה")]
    [InlineData("אנחנו נוסעים אל תל אביב לפגישה חשובה מחר בבוקר", "תל אביב")]
    [InlineData("כבר ניסיתי שלוש פעמים ועדיין לא הצלחתי לעשות זאת", "שלוש פעמים")]
    [InlineData("שילמתי עשרים שקלים וקיבלתי את הספר החדש מיד", "עשרים שקלים")]
    [InlineData("הוא הביט אל הבית הגדול ונזכר בכל מה שקרה אתמול", "הבית הגדול")]
    [InlineData("סיפרתי להם על ההחלטה החשובה שהתקבלה אתמול בערב", "ההחלטה החשובה")]
    [InlineData("כששאלת אותי לא ידעתי מה בדיוק קרה שם אתמול", "לא ידעתי")]
    [InlineData("בבקשה ממך אל תלך לפני שנסיים לדבר על הכל", "אל תלך")]
    [InlineData("אמרתי שאני יכול לעזור אבל אני חייב להגיע הביתה", "יכול לעזור")]
    [InlineData("הוא אמר שהוא רוצה ללכת אבל כולם ביקשו שיישאר", "רוצה ללכת")]
    [InlineData("אתה בוודאי צריך לדעת איך כל הדבר הזה עובד", "צריך לדעת")]
    public void HebrewBreakKeepsTightMeaningUnits(string input, string unit)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 32, 25, "he");
            Assert.Contains(Environment.NewLine, actual);
            Assert.Contains(unit, actual);
            Assert.Equal(input, actual.Replace(Environment.NewLine, " "));
            Assert.All(actual.Split(Environment.NewLine), line => Assert.True(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1));
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Theory]
    [InlineData("נוכל להתמודד עם כל מה שיבוא מולנו!")]
    [InlineData("נוכל להתמודד עם כל\nמה שיבוא מולנו!")]
    public void HebrewBreakKeepsKolMaClauseTogether(string input)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            Assert.Equal("נוכל להתמודד" + Environment.NewLine + "עם כל מה שיבוא מולנו!", Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 42, 25, "he"));
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Fact]
    public void CustomQcFindsReadingTimingPunctuationTermsAndHiddenCharactersWithoutRewriting()
    {
        var text = "חלופה [טקסט ארוך מאוד מאוד\u200F!!";
        var row = new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcRow(Guid.NewGuid(), 1, text, TimeSpan.Zero, TimeSpan.FromMilliseconds(500), 40);
        var limits = new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcLimits(12, 2, 20, 1000, 7000, 80);
        Assert.True(Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.TryParseTerms("שם תקני=חלופה", out var terms, out _));
        var issues = Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.Scan(new[] { row }, limits, terms);
        foreach (var kind in new[] { "reading", "duration", "length", "punctuation", "term", "invisible" }) Assert.Contains(issues, i => i.Kind == kind);
        Assert.Equal(text, row.Text);
        var acronym = row with { Text = "צה\"ל אמר: \"שלום\".", Cps = 10, End = TimeSpan.FromSeconds(2) };
        Assert.DoesNotContain(Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.Scan(new[] { acronym }, limits with { LineLength = 60 }, terms), i => i.Kind == "punctuation");
    }

    [Theory]
    [InlineData("נארוטו כאן", "✓")]
    [InlineData("נרוטו כאן", "✕")]
    [InlineData("ונרוטו כאן", "✕")]
    [InlineData("ונארוטו כאן", "✓")]
    [InlineData("נארוטו וגם נרוטו", "✕")]
    [InlineData("<font title='נרוטו'>נארוטו</font>", "✓")]
    [InlineData("נארוטואים", "")]
    [InlineData("שם שלא במילון", "")]
    public void ApprovedNameMarkersAreLiteralAndNeverCertifyUnknownNames(string text, string expected)
    {
        var terms = new[] { new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcTerm("נארוטו", new[] { "נרוטו" }) };
        var result = Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.NameSummary(text, terms);
        if (expected.Length == 0) Assert.Empty(result); else Assert.StartsWith(expected, result);
    }

    [Theory]
    [InlineData("נארוטו מופיע", "נארוטו הוא הגיבור", "✓")]
    [InlineData("נרוטו מופיע", "נארוטו הוא הגיבור", "✕")]
    [InlineData("נארוטו ונרוטו", "נארוטו הוא הגיבור", "✕")]
    [InlineData("נרוטו מופיע", "אין כאן כתיב מוסכם", "?")]
    [InlineData("מישהו אחר", "נארוטו הוא הגיבור", "")]
    [InlineData("<font title='נרוטו'>נארוטו</font>", "נארוטו", "✓")]
    public void AutomaticNameDetectionNeedsGuideEvidenceAndLabelsUncertainty(string text, string guide, string expected)
    {
        var terms = new[] { new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcTerm("נארוטו", new[] { "נרוטו" }) };
        var summary = Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.DetectedNameSummary(text, guide, terms);
        if (expected.Length == 0) Assert.Empty(summary); else Assert.StartsWith(expected, summary);
        if (expected == "✕") Assert.Contains("חשד AI", summary);
    }

    [Fact]
    public void CustomQcDetectsNestedOverlapsAndRespectsExactGapLimit()
    {
        var rows = new[]
        {
            new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcRow(Guid.NewGuid(), 1, "א", TimeSpan.Zero, TimeSpan.FromSeconds(10), 1),
            new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcRow(Guid.NewGuid(), 2, "ב", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1),
            new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcRow(Guid.NewGuid(), 3, "ג", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), 1),
            new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcRow(Guid.NewGuid(), 4, "ד", TimeSpan.FromMilliseconds(10080), TimeSpan.FromSeconds(12), 1),
        };
        var issues = Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.Scan(rows, new(43, 2, 20, 0, 0, 80), Array.Empty<Nikse.SubtitleEdit.Features.Main.MainHelpers.QcTerm>());
        Assert.Contains(issues, i => i.Number == 3 && i.Kind == "overlap" && i.Message.Contains("1"));
        Assert.DoesNotContain(issues, i => i.Number == 4 && i.Kind == "gap");
    }

    [Fact]
    public void QcGlossaryIsLiteralBoundedAndPersistedByClientProfile()
    {
        Assert.False(Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.TryParseTerms("שם בלי מפריד", out _, out var error));
        Assert.NotEmpty(error);
        Assert.True(Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.TryParseTerms("שם=אן|a+b", out var terms, out _));
        var row = new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcRow(Guid.NewGuid(), 1, "כאן aab", TimeSpan.Zero, TimeSpan.FromSeconds(2), 3);
        var limits = new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcLimits(43, 2, 20, 0, 0, 0);
        Assert.DoesNotContain(Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.Scan(new[] { row }, limits, terms), i => i.Kind == "term");
        Assert.Contains(Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomQc.Scan(new[] { row with { Text = "אן a+b" } }, limits, terms), i => i.Kind == "term");
        var config = new SeCustomQc(); config.GlossariesByProfile["לקוח א"] = "שם=אן"; config.GlossariesByProfile["לקוח ב"] = "שם אחר=אן";
        var copy = System.Text.Json.JsonSerializer.Deserialize<SeCustomQc>(System.Text.Json.JsonSerializer.Serialize(config))!;
        Assert.Equal(config.GlossariesByProfile, copy.GlossariesByProfile);
    }

    [AvaloniaFact]
    public void OstPreflightFindsEmptyInvalidAndDuplicateRowsWithoutTouchingNotes()
    {
        var view = MakeWindow();
        try
        {
            var rows = Field<ObservableCollection<OstItemVm>>(view, "_ostItems");
            rows.Add(new OstItemVm { TC = "00:00:01,000", Text = "שלום", Notes = "הערה" });
            Assert.Empty(view.ValidateForExport());
            rows.Add(new OstItemVm { TC = "00:00:01,000", Text = "שלום", Notes = "הערה אחרת" });
            rows.Add(new OstItemVm { TC = "bad", Text = "" });
            var issues = view.ValidateForExport();
            Assert.Contains(issues, s => s.Contains("כפילות"));
            Assert.Contains(issues, s => s.Contains("טקסט ריק"));
            Assert.Contains(issues, s => s.Contains("תזמון"));
            Assert.Equal("הערה", rows[0].Notes);
            Assert.Equal(3, rows.Count);
        }
        finally { view.TrySaveAndStop(); }
    }

    [AvaloniaFact]
    public void MainQcToolbarShowsNavigableLiveWarningsAndPreservesOstMembership()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var previousProfile = Se.Settings.General.CurrentProfile;
        var previousQc = Se.Settings.Tools.CustomQc;
        var window = new Window { Width = 1500, Height = 900 };
        MainView.NextHostWindow = window;
        var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            Se.Settings.General.CurrentProfile = "בדיקת לקוח";
            Se.Settings.Tools.CustomQc = new(); Se.Settings.Tools.CustomQc.GlossariesByProfile["בדיקת לקוח"] = "שם=חלופה";
            var row = new SubtitleLineViewModel { Number = 1, Text = "חלופה [שלום\u200F", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3), IsInOst = true };
            var original = row.Text;
            var comment = new SubtitleLineViewModel { Number = 2, Text = "הערה [\u200F!!", IsComment = true };
            vm.Subtitles.Clear(); vm.Subtitles.Add(row); vm.Subtitles.Add(comment);
            vm.SubtitleGrid.SelectedItem = row;
            vm.ShowOstTableCommand.Execute(null);
            var hash = vm.GetFastHash();
            vm.ScanCustomQc();
            Assert.Equal(hash, vm.GetFastHash());
            Assert.Empty(comment.CustomQcSummary);
            Assert.Equal(original, row.Text);
            Assert.Contains("✓", row.NumberDisplay); Assert.Contains("⚠", row.NumberDisplay);
            Assert.Contains("מונח לא אחיד", row.AccessibleErrorText);
            Assert.Contains(vm.CustomQcIssues, i => i.Kind == "term");
            Assert.Contains("שמות ✕", row.NumberDisplay);
            Assert.StartsWith("✕", row.CustomNameSummary);
            Assert.Empty(comment.CustomNameSummary);
            var button = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CustomQcToolbarButton");
            var menu = Field<MenuFlyout>(vm, "_customQcMenu");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);
            Assert.True(menu.Items.Count > 5);
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.Equal(FlowDirection.RightToLeft, Assert.IsType<TextBlock>(item.Header).FlowDirection));
            vm.SelectCustomQcIssue(vm.CustomQcIssues[0]); Assert.Same(row, vm.SubtitleGrid.SelectedItem);
            menu.Hide();
            row.Text = "שלום עולם.";
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.DoesNotContain("⚠", row.NumberDisplay); Assert.Contains("✓", row.NumberDisplay);
            Assert.Empty(vm.CustomQcIssues);
            Assert.Empty(row.CustomNameSummary);
            row.Text = "שם";
            var approvedHash = vm.GetFastHash();
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Contains("שמות ✓", row.NumberDisplay);
            Assert.Contains("לפי המילון", row.AccessibleErrorText);
            Assert.Equal(approvedHash, vm.GetFastHash());
            row.Text = "חלופה";
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Contains("שמות ✕", row.NumberDisplay);
            var project = new Nikse.SubtitleEdit.Features.Main.MainHelpers.ProjectProfile();
            project.Terms.Add(new() { Preferred = "נארוטו", Variants = new[] { "נרוטו" } });
            Field<List<Nikse.SubtitleEdit.Features.Main.MainHelpers.ProjectProfile>>(vm, "_customProfiles").Add(project);
            Field<Nikse.SubtitleEdit.Features.Main.MainHelpers.ProjectWork>(vm, "_customWork").ProfileId = project.Id;
            row.Text = "נארוטו";
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Contains("שמות ✓", row.NumberDisplay);
            // Changing the glossary alone invalidates the live marker.
            project.Terms[0].Preferred = "נרוטו";
            project.Terms[0].Variants = new[] { "נארוטו" };
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Contains("שמות ✕", row.NumberDisplay);
            Field<Nikse.SubtitleEdit.Features.Main.MainHelpers.ProjectWork>(vm, "_customWork").ProfileId = "";
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Empty(row.CustomNameSummary);
            Field<Nikse.SubtitleEdit.Features.Main.MainHelpers.ProjectWork>(vm, "_customWork").ProfileId = project.Id;
            project.Terms.Clear(); project.Guide = "הדמות הראשית נקראת נארוטו.";
            row.Text = "נרוטו";
            var suggestionHash = vm.GetFastHash();
            vm.SetDetectedNameSuggestions(new[] { new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcTerm("נארוטו", new[] { "נרוטו" }) });
            Assert.Contains("שמות ✕", row.NumberDisplay);
            Assert.Contains("חשד AI", row.CustomNameSummary);
            Assert.Equal(suggestionHash, vm.GetFastHash());
            project.Guide = "מדריך אחר";
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Empty(row.CustomNameSummary);
            vm.SetDetectedNameSuggestions(new[] { new Nikse.SubtitleEdit.Features.Main.MainHelpers.QcTerm("נרוטו", Array.Empty<string>()) });
            Assert.Contains("שמות ?", row.NumberDisplay);
            row.Text = "עריכה חדשה";
            Call(vm, "CustomQcTick", new object(), EventArgs.Empty);
            Assert.Empty(row.CustomNameSummary);
            // Even an empty document must have a working menu with settings and status.
            vm.Subtitles.Clear();
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);
            Assert.True(menu.Items.Count > 5);
            menu.Hide();
            var timer = Field<DispatcherTimer>(vm, "_customQcTimer");
            window.Close(); Assert.False(timer.IsEnabled);
        }
        finally { Se.Settings.General.CurrentProfile = previousProfile; Se.Settings.Tools.CustomQc = previousQc; window.Close(); }
    }

    [Theory]
    [InlineData("  שלום ,   עולם !  ", "he", "שלום, עולם!")]
    [InlineData("  Bonjour   !  ", "fr", "Bonjour !")]
    [InlineData("  מחיר  12.50  ש״ח ....  ", "he", "מחיר 12.50 ש״ח....")]
    public void LineBreakSpacingCleansOnlyWhitespace(string input, string language, string expected)
    {
        Assert.Equal(expected, Nikse.SubtitleEdit.Core.Common.SubtitleLineSpacing.Clean(input, language));
    }

    [Fact]
    public void LineBreakSpacingPreservesFormattingAttributesOverridesAndDirectionMarks()
    {
        const string tag = "<font face=\"Open  Sans > Font\">";
        Assert.Equal(tag + "שלום, עולם</font>!", Nikse.SubtitleEdit.Core.Common.SubtitleLineSpacing.Clean("  " + tag + "  שלום  ,  עולם </font> !  ", "he"));
        Assert.Equal("{\\fnOpen  Sans}שלום, עולם!", Nikse.SubtitleEdit.Core.Common.SubtitleLineSpacing.Clean("{\\fnOpen  Sans} שלום  , עולם !", "he"));
        Assert.Equal("\uE000 שלום עולם\u200F\u00A0", Nikse.SubtitleEdit.Core.Common.SubtitleLineSpacing.Clean("\uE000 שלום  עולם\u200F\u00A0", "he"));
    }

    [AvaloniaFact]
    public void ActualBreakAndToggleCommandsCleanSelectedRowsAndPreserveMarkup()
    {
        var services = new ServiceCollection();
        services.AddSubtitleEditServices();
        Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 };
        MainView.NextHostWindow = window;
        var main = new MainView();
        window.Content = main;
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previousLines = general.MaxNumberOfLines;
        var previousLength = general.SubtitleLineMaximumLength;
        var previousMerge = general.MergeLinesShorterThan;
        var previousUiLength = Se.Settings.General.SubtitleLineMaximumLength;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainViewModel)main.DataContext!;
            window.SuppressSaveChangesPromptOnClose(vm);
            general.MaxNumberOfLines = 2;
            general.SubtitleLineMaximumLength = 42;
            general.MergeLinesShorterThan = 25;
            Se.Settings.General.SubtitleLineMaximumLength = 42;
            const string dirty = "  אך  שום דבר לא דמה למה   שהתמודדת איתו לאחרונה .  ";
            const string singleLine = "אך שום דבר לא דמה למה שהתמודדת איתו לאחרונה.";
            var expected = "אך שום דבר לא דמה" + Environment.NewLine + "למה שהתמודדת איתו לאחרונה.";
            var selected = new SubtitleLineViewModel { Number = 1, Text = dirty };
            var other = new SubtitleLineViewModel { Number = 2, Text = "  כתובית  שלא נבחרה !  " };
            var untouched = other.Text;
            vm.Subtitles.Clear(); vm.Subtitles.Add(selected); vm.Subtitles.Add(other);
            vm.SubtitleGrid.SelectedItem = selected;
            vm.AutoBreakCommand.Execute(null);
            Assert.Equal(expected, selected.Text);
            Assert.Equal(untouched, other.Text);
            vm.ToggleBreakCommand.Execute(null);
            Assert.Equal(singleLine, selected.Text);
            selected.Text = dirty;
            vm.ToggleBreakCommand.Execute(null);
            Assert.Equal(expected, selected.Text);
            const string tag = "<font face=\"Open  Sans > Font\">";
            selected.Text = "  " + tag + dirty + "</font>  ";
            vm.AutoBreakCommand.Execute(null);
            Assert.StartsWith(tag, selected.Text);
            Assert.EndsWith("</font>", selected.Text);
            Assert.Contains(Environment.NewLine, selected.Text);
            Assert.Equal(singleLine, selected.Text.Substring(tag.Length).Replace("</font>", string.Empty).Replace(Environment.NewLine, " "));
            Assert.Equal(untouched, other.Text);
        }
        finally
        {
            general.MaxNumberOfLines = previousLines;
            general.SubtitleLineMaximumLength = previousLength;
            general.MergeLinesShorterThan = previousMerge;
            Se.Settings.General.SubtitleLineMaximumLength = previousUiLength;
            window.Close();
        }
    }

    [Fact]
    public void HebrewAiReviewPresetSelectsCuratedLocalModelWithoutCredentials()
    {
        var settings = new Nikse.SubtitleEdit.Logic.Config.SeAiReview();
        Assert.True(Nikse.SubtitleEdit.Features.Tools.AiReview.CustomHebrewAiReview.ApplyDefaults(settings));
        Assert.Equal(Nikse.SubtitleEdit.Logic.Config.SeAiReview.EngineLlamaCpp, settings.Engine);
        var model = Assert.Single(Nikse.SubtitleEdit.UiLogic.LlamaCpp.LlamaCppServerManager.ReviewModels.Where(m => m.FileName == settings.LlamaCppModelFileName));
        Assert.Equal("2.8 GB", model.Size);
        Assert.Contains("{language}", settings.Prompt);
        Assert.Contains("Preserve all formatting tags and existing line breaks exactly", settings.Prompt);
        Assert.Empty(settings.OpenAiCompatibleApiKey);
        Assert.False(Nikse.SubtitleEdit.Features.Tools.AiReview.CustomHebrewAiReview.ApplyDefaults(settings));
    }

    [Fact]
    public void HebrewAiReviewPresetPreservesExistingEngineModelAndPrompt()
    {
        var settings = new Nikse.SubtitleEdit.Logic.Config.SeAiReview
        {
            Engine = Nikse.SubtitleEdit.Logic.Config.SeAiReview.EngineOllama,
            OllamaModel = "my-local-model", Prompt = "my prompt", Context = "names and terms", MaxLinesPerBatch = 7,
        };
        Assert.False(Nikse.SubtitleEdit.Features.Tools.AiReview.CustomHebrewAiReview.ApplyDefaults(settings));
        Assert.Equal("my-local-model", settings.OllamaModel);
        Assert.Equal("my prompt", settings.Prompt);
        Assert.Equal("names and terms", settings.Context);
        Assert.Equal(7, settings.MaxLinesPerBatch);
        settings.Engine = Nikse.SubtitleEdit.Logic.Config.SeAiReview.EngineLlamaCpp;
        settings.LlamaCppModelFileName = "my-model.gguf";
        Assert.False(Nikse.SubtitleEdit.Features.Tools.AiReview.CustomHebrewAiReview.ApplyDefaults(settings));
        Assert.Equal("my-model.gguf", settings.LlamaCppModelFileName);
        settings.LlamaCppModelFileName = string.Empty;
        Assert.True(Nikse.SubtitleEdit.Features.Tools.AiReview.CustomHebrewAiReview.ApplyDefaults(settings));
        Assert.Equal("my prompt", settings.Prompt);
        Assert.Equal("names and terms", settings.Context);
    }

    [Theory]
    [InlineData("אך שום דבר לא דמה למה שהתמודדת איתו לאחרונה.")]
    [InlineData("אך שום דבר לא דמה למה\nשהתמודדת איתו לאחרונה.")]
    public void HebrewBreakKeepsPrefixedRelativeWordWithItsClause(string input)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 42, 25, "he");
            Assert.Equal("אך שום דבר לא דמה" + Environment.NewLine + "למה שהתמודדת איתו לאחרונה.", actual);
            Assert.Equal(input.Replace("\n", " "), actual.Replace(Environment.NewLine, " "));
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Theory]
    [InlineData("איננו יודעים הרבה על האנרגיה המוזרה שתקפה אותנו.")]
    [InlineData("איננו יודעים הרבה על האנרגיה\nהמוזרה שתקפה אותנו.")]
    public void HebrewBreakKeepsDefiniteNounAndAdjectiveTogether(string input)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 42, 25, "he");
            Assert.Equal("איננו יודעים הרבה על" + Environment.NewLine + "האנרגיה המוזרה שתקפה אותנו.", actual);
            Assert.Equal(input.Replace("\n", " "), actual.Replace(Environment.NewLine, " "));
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Fact]
    public void HebrewNounPhraseExceptionPreservesShortCuesAndStrictFunctionWords()
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            Assert.Equal("לא צריך לשבור משפט קצר.", Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine("לא צריך לשבור משפט קצר.", 42, 25, "he"));
            foreach (var input in new[] { "אני רוצה לראות את הסרט החדש שיצא אתמול בקולנוע.", "הסיפור של הגיבור החדש הפתיע את כל האנשים בחדר." })
            {
                var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 42, 25, "he");
                var firstLine = actual.Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0];
                Assert.False(firstLine.EndsWith(" את", StringComparison.Ordinal));
                Assert.False(firstLine.EndsWith(" של", StringComparison.Ordinal));
                Assert.Equal(input, actual.Replace(Environment.NewLine, " "));
            }
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Theory]
    [InlineData("אך אני יודעת שהמקלט שלנו ניצב איתן היום")]
    [InlineData("אך אני יודעת שהמקלט\nשלנו ניצב איתן היום")]
    public void HebrewBreakKeepsPossessivePronounWithItsNoun(string input)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 42, 25, "he");
            Assert.Equal("אך אני יודעת שהמקלט שלנו" + Environment.NewLine + "ניצב איתן היום", actual);
            Assert.Equal(input.Replace("\n", " "), actual.Replace(Environment.NewLine, " "));
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    [Theory]
    [InlineData("אה! אתה יכול ללמד אותי איך להשתמש ברחפן האווירי שלך?")]
    [InlineData("אה! אתה יכול ללמד אותי איך\nלהשתמש ברחפן האווירי שלך?")]
    public void HebrewBreakKeepsHowWithFollowingInfinitive(string input)
    {
        var general = Nikse.SubtitleEdit.Core.Common.Configuration.Settings.General;
        var previous = general.MaxNumberOfLines;
        try
        {
            general.MaxNumberOfLines = 2;
            var actual = Nikse.SubtitleEdit.Core.Common.Utilities.AutoBreakLine(input, 42, 25, "he");
            Assert.Equal("אה! אתה יכול ללמד אותי" + Environment.NewLine + "איך להשתמש ברחפן האווירי שלך?", actual);
            Assert.Equal(input.Replace("\n", " "), actual.Replace(Environment.NewLine, " "));
        }
        finally { general.MaxNumberOfLines = previous; }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static OstTableView MakeWindow() => new(new List<SubEntry>(), "test", true, "");

    [Fact]
    public void JsonRoundTripPreservesBracketsNewlinesAndOrder()
    {
        var state = new ProjectState { ProjectTitle = "בדיקה", Entries = new()
        {
            new() { StartTime = "00:06:16,330", Text = "טקסט ] {עם} \\\n\"מירכאות\"", Notes = "\tהערה" },
            new() { StartTime = "00:05:55,506", Text = "123" }
        }};
        var actual = OstTableView.DeserializeState(OstTableView.SerializeState(state))!;
        Assert.Equal(2, actual.Entries.Count);
        Assert.Equal(state.Entries[0].Text, actual.Entries[0].Text);
        Assert.Equal(state.Entries[0].Notes, actual.Entries[0].Notes);
        Assert.Equal(state.Entries[1].StartTime, actual.Entries[1].StartTime);
        Assert.Null(OstTableView.DeserializeState("{}"));
    }

    [Fact]
    public void NumericSubtitleAndMultilineTextSurviveParsing()
    {
        var rows = OstTableView.ParseSrt("1\r\n00:00:01,000 --> 00:00:02,000\r\n123\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nשלום\r\nעולם\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("123", rows[0].Text);
        Assert.Equal("שלום\nעולם", rows[1].Text);
    }

    [AvaloniaFact]
    public void RebuildingColumnsKeepsRowsAndEditsMarkDirty()
    {
        var window = MakeWindow();
        var rows = Field<ObservableCollection<OstItemVm>>(window, "_ostItems");
        var vm = new OstItemVm { TC = "00:00:01,000", Text = "שורה" };
        rows.Add(vm);
        Call(window, "RebuildOstColumns"); Call(window, "RebuildOstColumns");
        Assert.Single(rows); Assert.Same(vm, rows[0]);
        typeof(OstTableView).GetField("_isDirty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
        vm.Notes = "הערה";
        Assert.True(Field<bool>(window, "_isDirty"));
        Call(window, "ApplyProjectState", new ProjectState { Entries = new() { new() { StartTime = "00:06:00,000" }, new() { StartTime = "00:05:00,000" } } });
        Assert.Equal("00:06:00,000", rows[0].TC);
        window.TrySaveAndStop();
    }

    [AvaloniaFact]
    public void ExportIsValidDocxWithHebrewText()
    {
        var window = MakeWindow();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            window.WriteDocx(path, new() { new() { StartTime = "00:00:01,000", Text = "שלום & <עולם>\n123" } });
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries) { using var stream = entry.Open(); XDocument.Load(stream); }
            using var doc = zip.GetEntry("word/document.xml")!.Open();
            Assert.Contains("שלום & <עולם>", XDocument.Load(doc).Root!.Value);
        }
        finally { window.TrySaveAndStop(); if (File.Exists(path)) File.Delete(path); }
    }
    [AvaloniaFact]
    public void ToolbarAddsAndRemovesRowsWithoutOpeningAnyPanelOrChangingSubtitleText()
    {
        var services = new ServiceCollection();
        services.AddSubtitleEditServices();
        Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900, FlowDirection = FlowDirection.RightToLeft };
        MainView.NextHostWindow = window;
        var main = new MainView();
        window.Content = main;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var vm = (MainViewModel)main.DataContext!;
        window.SuppressSaveChangesPromptOnClose(vm);
        var parent = vm.ContentGrid.Parent;
        var bounds = vm.ContentGrid.Bounds;
        var line = new SubtitleLineViewModel { Number = 12, Text = "טקסט מקורי", StartTime = TimeSpan.FromSeconds(1) };
        vm.Subtitles.Clear(); vm.Subtitles.Add(line);
        vm.SubtitleGrid.SelectedItem = line;
        vm.ShowOstTableCommand.Execute(null);
        Assert.True(line.IsInOst);
        Assert.Contains("✓", line.NumberDisplay);
        Assert.Equal("טקסט מקורי", line.Text);
        var view = Field<OstTableView>(vm, "_ostView");
        Assert.Null(view.Parent);
        Assert.Same(window, view.DialogOwner);
        Assert.DoesNotContain(main.GetVisualDescendants(), v => v is OstTableView);
        Assert.Same(parent, vm.ContentGrid.Parent);
        Assert.Equal(bounds, vm.ContentGrid.Bounds);
        Assert.Equal(FlowDirection.LeftToRight, vm.ContentGrid.FlowDirection);
        var button = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OstTableToolbarButton");
        Assert.Equal("OST −", button.Content);
        ToolTip.SetIsOpen(button, true);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(ToolTip.GetIsOpen(button));
        var rows = Field<ObservableCollection<OstItemVm>>(view, "_ostItems");
        Assert.Single(rows);
        line.Text = "אחרי עריכה";
        line.StartTime = TimeSpan.FromSeconds(4);
        vm.RefreshOstMarkers();
        Assert.Equal("אחרי עריכה", rows[0].Text);
        Assert.EndsWith("04,000", rows[0].TC);
        // The same toolbar command removes the already-included selection.
        vm.ShowOstTableCommand.Execute(null);
        Assert.Equal("OST +", button.Content);
        Assert.Empty(rows);
        Assert.False(line.IsInOst);
        Assert.Equal("12", line.NumberDisplay);
        Assert.Equal("אחרי עריכה", line.Text);
        vm.ShowOstTableCommand.Execute(null);
        var second = new SubtitleLineViewModel { Number = 13, Text = "שורה נוספת", StartTime = TimeSpan.FromSeconds(5) };
        vm.Subtitles.Add(second);
        vm.SubtitleGrid.SelectedItems!.Add(second);
        vm.RefreshOstToolbar();
        Assert.Equal("OST +", button.Content);
        vm.ShowOstTableCommand.Execute(null);
        Assert.Equal(2, rows.Count);
        Assert.True(line.IsInOst); Assert.True(second.IsInOst);
        Assert.Equal("OST −", button.Content);
        vm.ShowOstTableCommand.Execute(null);
        Assert.Empty(rows);
        Assert.False(line.IsInOst); Assert.False(second.IsInOst);
        vm.SubtitleGrid.SelectedItem = line;
        vm.ShowOstTableCommand.Execute(null);
        var renamed = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt");
        var sidecar = Path.ChangeExtension(renamed, ".ostproject");
        try
        {
            typeof(MainViewModel).GetField("_subtitleFileName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, renamed);
            Assert.True((bool)Call(vm, "EnsureOstProject")!);
            Assert.Same(view, Field<OstTableView>(vm, "_ostView"));
            Assert.True(vm.SaveOstBeforeClosing());
            Assert.True(File.Exists(sidecar));
            // A new untitled document must never inherit the previous table.
            vm.NewOstDocument();
            typeof(MainViewModel).GetField("_subtitleFileName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, "");
            vm.Subtitles.Clear();
            var fresh = new SubtitleLineViewModel { Number = 1, Text = "מסמך חדש" };
            vm.Subtitles.Add(fresh);
            Assert.True((bool)Call(vm, "EnsureOstProject")!);
            vm.RefreshOstMarkers();
            Assert.False(fresh.IsInOst);
            Assert.Empty(Field<ObservableCollection<OstItemVm>>(Field<OstTableView>(vm, "_ostView"), "_ostItems"));
        }
        finally
        {
            window.Close();
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    [AvaloniaFact]
    public void AddingMainSelectionUsesFreshTextWithoutDuplicates()
    {
        var view = MakeWindow();
        view.SelectedRowsProvider = () => new() { new() { StartTime = "00:00:01,000", Text = "מהרשימה הראשית" } };
        view.AddMainSelection(); view.AddMainSelection();
        var rows = Field<ObservableCollection<OstItemVm>>(view, "_ostItems");
        Assert.Single(rows);
        Assert.Equal("מהרשימה הראשית", rows[0].Text);
        view.TrySaveAndStop();
    }

    [AvaloniaFact]
    public void LinkedRowTracksTextAndTimeButPreservesNotesAndManualOrder()
    {
        var view = MakeWindow();
        var id = Guid.NewGuid();
        var source = new SubEntry { SourceId = id, StartTime = "00:00:01,000", Text = "לפני" };
        view.SelectedRowsProvider = () => new() { source };
        view.SourceRowsProvider = () => new() { source };
        view.AddMainSelection();
        var rows = Field<ObservableCollection<OstItemVm>>(view, "_ostItems");
        rows[0].Notes = "הערה פרטית";
        source.Text = "אחרי"; source.StartTime = "00:00:03,500";
        view.SyncLinkedRows(); view.AddMainSelection();
        Assert.Single(rows);
        Assert.Equal("אחרי", rows[0].Text);
        Assert.Equal("00:00:03,500", rows[0].TC);
        Assert.Equal("הערה פרטית", rows[0].Notes);
        Assert.Equal(id, rows[0].SourceId);
        view.TrySaveAndStop();
    }

    [AvaloniaFact]
    public void ReopenedProjectRelinksByExactTextAndTimeAndPreservesNotes()
    {
        var view = MakeWindow();
        var oldId = Guid.NewGuid(); var newId = Guid.NewGuid();
        Call(view, "ApplyProjectState", new ProjectState { Entries = new() { new() { SourceId = oldId, StartTime = "00:00:02,000", Text = "נשמר", Notes = "הערה" } } });
        view.SourceRowsProvider = () => new() { new() { SourceId = newId, StartTime = "00:00:02,000", Text = "נשמר" } };
        view.SyncLinkedRows();
        Assert.Contains(newId, view.LinkedIds()); Assert.DoesNotContain(oldId, view.LinkedIds());
        Assert.Equal("הערה", Field<ObservableCollection<OstItemVm>>(view, "_ostItems")[0].Notes);
        view.TrySaveAndStop();
    }

    [AvaloniaFact]
    public async Task CustomUpdatesKeepOfficialInstallerOutOfOurBuild()
    {
        using var client = new System.Net.Http.HttpClient();
        var service = new Nikse.SubtitleEdit.Features.Help.CheckForUpdates.UpdateCheckService(client);
        var result = await service.CheckForUpdates();
        Assert.False(result.IsNewVersionAvailable);
        Assert.Equal(Se.Version, result.LatestVersion);
        var vm = new Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CheckForUpdatesViewModel(service, null!, null!);
        await vm.CheckForUpdates();
        Assert.False(vm.IsInstallVisible);
        Assert.Contains("Settings.json", vm.ChangeLogText);
        Assert.StartsWith("https://github.com/Elior-sasi/subtitleedit-elior/", Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateChannel.DownloadPage);
    }

    [Fact]
    public void CustomUpdatePackagesRequireValidDigestAndLiveCustomArtifact()
    {
        var valid = "{\"artifacts\":[{\"id\":9,\"name\":\"SE-5.4.0-beta6-RTL-Hebrew-OST-AutoUpdate\",\"expired\":false,\"digest\":\"sha256:" + new string('a',64) + "\"}]}";
        var package = Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateChannel.ParsePackage(valid, 123);
        Assert.NotNull(package); Assert.Equal(123L, package.RunId); Assert.Equal(9L, package.ArtifactId);
        Assert.Null(Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateChannel.ParsePackage(valid.Replace("sha256:", "md5:"),123));
        Assert.Null(Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateChannel.ParsePackage(valid.Replace("false", "true"),123));
        Assert.Null(Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateChannel.ParsePackage(valid.Replace("RTL-Hebrew-OST", "Official"),123));
    }

    [Fact]
    public async Task DamagedDownloadIsRejectedBeforeInstallation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            await File.WriteAllTextAsync(path, "not a valid download");
            await Assert.ThrowsAsync<InvalidDataException>(() => Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateChannel.VerifyArchiveAsync(path, new string('0',64), System.Threading.CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomInstallerPreservesSettingsAndSubtitleFiles(bool simulateFailure)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "SE בדיקה ' " + Guid.NewGuid());
        var source = Path.Combine(root, "stage", "payload");
        var target = Path.Combine(root, "installed");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        var script = Path.Combine(root, "installer.ps1");
        FileStream? locked = null;
        try
        {
            await File.WriteAllTextAsync(script, Nikse.SubtitleEdit.Features.Help.CheckForUpdates.CustomUpdateInstaller.Script, new System.Text.UTF8Encoding(true));
            foreach (var pair in new[] { ("SubtitleEdit.exe", "new executable"), ("Settings.json", "wrong settings"), ("work.srt", "wrong subtitles") })
                await File.WriteAllTextAsync(Path.Combine(source, pair.Item1), pair.Item2);
            await File.WriteAllTextAsync(Path.Combine(target, "SubtitleEdit.exe"), "old executable");
            await File.WriteAllTextAsync(Path.Combine(target, "Settings.json"), "my settings");
            await File.WriteAllTextAsync(Path.Combine(target, "work.srt"), "my subtitles");
            Directory.CreateDirectory(Path.Combine(source, "CloudMfa")); Directory.CreateDirectory(Path.Combine(target, "CloudMfa"));
            await File.WriteAllTextAsync(Path.Combine(source, "CloudMfa", "token.dpapi"), "wrong token");
            await File.WriteAllTextAsync(Path.Combine(target, "CloudMfa", "token.dpapi"), "my token");
            if (simulateFailure)
            {
                await File.WriteAllTextAsync(Path.Combine(source, "zz-locked.dll"), "new locked file");
                await File.WriteAllTextAsync(Path.Combine(target, "zz-locked.dll"), "old locked file");
                locked = File.Open(Path.Combine(target, "zz-locked.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-WaitProcessId", "0", "-SourceDirectory", source, "-TargetDirectory", target, "-SkipLaunch" }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == (simulateFailure ? 1 : 0), await output + await errors);
            Assert.Equal(simulateFailure ? "old executable" : "new executable", await File.ReadAllTextAsync(Path.Combine(target, "SubtitleEdit.exe")));
            Assert.Equal("my settings", await File.ReadAllTextAsync(Path.Combine(target, "Settings.json")));
            Assert.Equal("my subtitles", await File.ReadAllTextAsync(Path.Combine(target, "work.srt")));
            Assert.Equal("my token", await File.ReadAllTextAsync(Path.Combine(target, "CloudMfa", "token.dpapi")));
        }
        finally { locked?.Dispose(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void ActualOstFlyoutKeepsHebrewPunctuationAndLatinWordsInTextDirection()
    {
        var services = new ServiceCollection();
        services.AddSubtitleEditServices();
        Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900, FlowDirection = FlowDirection.RightToLeft };
        MainView.NextHostWindow = window;
        var main = new MainView();
        window.Content = main;
        MenuFlyout? flyout = null;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var vm = (MainViewModel)main.DataContext!;
            window.SuppressSaveChangesPromptOnClose(vm);
            var options = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OstOptionsToolbarButton");
            flyout = Assert.IsType<MenuFlyout>(options.Flyout);
            flyout.ShowAt(options);
            Dispatcher.UIThread.RunJobs();
            var items = flyout.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(5, items.Length);
            Assert.Equal("כותרת ופרטי OST…", ((TextBlock)items[0].Header!).Text);
            Assert.Equal("ייצוא OST ל־Word…", ((TextBlock)items[1].Header!).Text);
            foreach (var item in items)
            {
                var header = Assert.IsType<TextBlock>(item.Header);
                Assert.True(header.IsVisible);
                Assert.NotNull(header.GetVisualParent());
                Assert.Equal(FlowDirection.RightToLeft, header.FlowDirection);
                Assert.Equal(FlowDirection.LeftToRight, item.FlowDirection);
                Assert.Equal(header.Text, Avalonia.Automation.AutomationProperties.GetName(item));
                header.Measure(new Size(500, 100));
                if (header.Text!.EndsWith("…"))
                {
                    Assert.True(header.TextLayout.HitTestTextPosition(header.Text.Length - 1).X < header.TextLayout.HitTestTextPosition(0).X,
                        "The actual OST flyout must put trailing punctuation after the Hebrew caption.");
                }
                foreach (var word in new[] { "OST", "Word" })
                {
                    var start = header.Text!.IndexOf(word, StringComparison.Ordinal);
                    if (start >= 0)
                        Assert.True(header.TextLayout.HitTestTextPosition(start).X < header.TextLayout.HitTestTextPosition(start + word.Length - 1).X,
                            "Latin words inside Hebrew captions must remain LTR.");
                }
            }
            Assert.Equal(FlowDirection.LeftToRight, vm.ContentGrid.FlowDirection);
        }
        finally { flyout?.Hide(); window.Close(); }
    }

    [AvaloniaFact]
    public void AllMainMenusSubmenusAndToolbarFlyoutsUseNativeCaptionFont()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 };
        MainView.NextHostWindow = window;
        var main = new MainView(); window.Content = main;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var menu = vm.Menu;
            var native = menu.Items.OfType<MenuItem>().First().Items.OfType<MenuItem>().First();
            var checkedItems = 0; var customHeaders = 0;
            void CheckMenu(MenuItem parent)
            {
                if (!parent.IsVisible || !parent.IsEnabled || parent.Items.Count == 0) return;
                parent.IsSubMenuOpen = true;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var children = parent.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToArray();
                foreach (var item in children)
                {
                    checkedItems++;
                    Assert.Equal(native.FontSize, item.FontSize);
                    if (item.Header is TextBlock header)
                    {
                        customHeaders++;
                        Assert.NotNull(header.GetVisualParent());
                        Assert.Equal(item.FontSize, header.FontSize);
                    }
                }
                foreach (var child in children.Where(i => i.Items.Count > 0)) CheckMenu(child);
                parent.IsSubMenuOpen = false;
                Dispatcher.UIThread.RunJobs();
            }
            foreach (var size in new[] { 13.0, 17.0 })
            {
                Nikse.SubtitleEdit.Logic.UiTheme.ApplyDenseMenuStyle(menu, size);
                Dispatcher.UIThread.RunJobs();
                foreach (var top in menu.Items.OfType<MenuItem>()) CheckMenu(top);
            }
            Assert.True(checkedItems > 20);
            Assert.True(customHeaders > 20);

            void CheckFlyout(MenuFlyout flyout)
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(flyout.IsOpen);
                foreach (var item in flyout.Items.OfType<MenuItem>())
                {
                    var header = Assert.IsType<TextBlock>(item.Header);
                    Assert.NotNull(header.GetVisualParent());
                    Assert.Equal(item.FontSize, header.FontSize);
                    item.FontSize += 2;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(item.FontSize, header.FontSize);
                    Assert.Equal(FlowDirection.RightToLeft, header.FlowDirection);
                }
                flyout.Hide();
            }
            var options = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OstOptionsToolbarButton");
            var ost = Assert.IsType<MenuFlyout>(options.Flyout);
            ost.ShowAt(options); CheckFlyout(ost);
            var qcButton = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CustomQcToolbarButton");
            qcButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            CheckFlyout(Field<MenuFlyout>(vm, "_customQcMenu"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HebrewMenuPunctuationFollowsTextWhileMenuAndShortcutsStayInPlace()
    {
        const string label = "תרגום אוטומטי...";
        var hebrew = new MenuItem { Header = label, InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.G, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift) };
        var english = new MenuItem { Header = "Export to Word..." };
        var top = new MenuItem { Header = "תרגום", Items = { hebrew, english } };
        var menu = new Menu { Items = { top }, FlowDirection = FlowDirection.LeftToRight };
        var window = new Window { Content = menu, Width = 700, Height = 400, FlowDirection = FlowDirection.LeftToRight };
        Nikse.SubtitleEdit.Features.Main.MainHelpers.MenuTextDirection.AddStyles(window.Styles);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            top.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var presenter = hebrew.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(c => c.Name == "PART_HeaderPresenter");
            Assert.Equal(FlowDirection.RightToLeft, presenter.FlowDirection);
            var text = presenter.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.AccessText>().Single();
            Assert.Equal(FlowDirection.RightToLeft, text.FlowDirection);
            text.Measure(new Size(500, 100));
            var start = text.TextLayout.HitTestTextPosition(0);
            var lastDot = text.TextLayout.HitTestTextPosition(label.Length - 1);
            Assert.True(lastDot.X < start.X, "The trailing dots must appear to the left of Hebrew text.");
            Assert.Equal(label, hebrew.Header);
            Assert.Equal(FlowDirection.LeftToRight, menu.FlowDirection);
            Assert.Equal(FlowDirection.LeftToRight, hebrew.FlowDirection);
            var gesture = hebrew.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_InputGestureText");
            Assert.Equal(FlowDirection.LeftToRight, gesture.FlowDirection);
            Assert.Equal(3, Grid.GetColumn(presenter));
            Assert.Equal(2, Grid.GetColumn(gesture));
            Assert.True(gesture.Bounds.Left < presenter.Bounds.Left, "Hebrew shortcuts must be physically left of their captions.");
            var englishPresenter = english.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(c => c.Name == "PART_HeaderPresenter");
            Assert.Equal(FlowDirection.LeftToRight, englishPresenter.FlowDirection);
        }
        finally { window.Close(); }
    }

    // A Hebrew text box flows right to left, so its context menu rows are mirrored by the
    // template. Moving the caption to the shortcut column there pushed Hebrew captions to the
    // far (left) side next to the submenu arrow, away from their icons.
    [AvaloniaFact]
    public void HebrewCaptionsStayInTheCaptionColumnOfRightToLeftContextMenus()
    {
        var item = new MenuItem { Header = "חפש באמצעות", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.F, Avalonia.Input.KeyModifiers.Control) };
        var flyout = new MenuFlyout { Items = { item } };
        var box = new TextBox { Text = "שלום", FlowDirection = FlowDirection.RightToLeft, ContextFlyout = flyout, Width = 300 };
        var window = new Window { Content = box, Width = 700, Height = 400, FlowDirection = FlowDirection.LeftToRight };
        Nikse.SubtitleEdit.Features.Main.MainHelpers.MenuTextDirection.AddStyles(window.Styles);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            flyout.ShowAt(box); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(FlowDirection.RightToLeft, item.FlowDirection);
            var presenter = item.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(c => c.Name == "PART_HeaderPresenter");
            var gesture = item.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_InputGestureText");
            Assert.Equal(2, Grid.GetColumn(presenter));
            Assert.Equal(3, Grid.GetColumn(gesture));
            flyout.Hide(); Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    }

    // Custom Hebrew items share popups with English captions (English UI, untranslated items).
    // The moved Hebrew caption used to widen the shared shortcut column for every row, so at a
    // large UI font English captions were cut off at the popup's maximum width.
    [AvaloniaFact]
    public void LargeFontEnglishCaptionsAreNotCutOffNextToCustomHebrewItems()
    {
        var oldFont = Se.Settings.Appearance.FontScale; var oldLayout = Se.Settings.Appearance.LayoutScale;
        var english = new MenuItem { Header = "Make new _empty translation from current subtitle" };
        var hebrew = new MenuItem { Header = new TextBlock { Text = "סקירה ממוקדת — ניגון שורות לבדיקה…", FlowDirection = FlowDirection.RightToLeft } };
        var top = new MenuItem { Header = "Tools", Items = { english, hebrew } };
        var menu = new Menu { Items = { top } };
        var window = new Window { Content = menu, Width = 1400, Height = 700, FlowDirection = FlowDirection.LeftToRight };
        Nikse.SubtitleEdit.Features.Main.MainHelpers.MenuTextDirection.AddStyles(window.Styles);
        try
        {
            Se.Settings.Appearance.FontScale = 20 / Nikse.SubtitleEdit.Logic.UiTheme.BaseFontSize;
            Nikse.SubtitleEdit.Logic.UiTheme.SetLayoutScale(1.0);
            window.Show(); Dispatcher.UIThread.RunJobs();
            top.IsSubMenuOpen = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(20, english.FontSize, 3);
            foreach (var row in new[] { english, hebrew })
            {
                var text = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name != "PART_InputGestureText" && !string.IsNullOrEmpty(t.Text));
                // Width the caption needs on its own, independent of how the row squeezed it.
                var alone = new TextBlock { Text = text is Avalonia.Controls.Primitives.AccessText ? text.Text!.Replace("_", "") : text.Text, FontSize = text.FontSize, FontFamily = text.FontFamily, FontWeight = text.FontWeight };
                alone.Measure(Size.Infinity);
                Assert.True(alone.DesiredSize.Width <= text.Bounds.Width + 1,
                    $"'{text.Text}' needs {alone.DesiredSize.Width:0} px but has {text.Bounds.Width:0} px");
            }
            top.IsSubMenuOpen = false; Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            Se.Settings.Appearance.FontScale = oldFont; Nikse.SubtitleEdit.Logic.UiTheme.SetLayoutScale(oldLayout);
        }
    }

    // With large text or UI scale the menu bar is wider than the window. The custom Frame and
    // Project menus made it longer, and the last menus (Options, Help) became unreachable past
    // the window edge. The menu bar now wraps to another row.
    [AvaloniaFact]
    public void EveryMainMenuStaysInsideANarrowWindowWithLargeText()
    {
        var oldFont = Se.Settings.Appearance.FontScale; var oldLayout = Se.Settings.Appearance.LayoutScale;
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 960, Height = 500 };
        try
        {
            Se.Settings.Appearance.FontScale = 20 / Nikse.SubtitleEdit.Logic.UiTheme.BaseFontSize;
            Nikse.SubtitleEdit.Logic.UiTheme.SetLayoutScale(1.5);
            MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
            Nikse.SubtitleEdit.Logic.UiTheme.ApplyScaleToWindow(window);
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            var tops = vm.Menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
            Assert.Contains(tops, i => i.Name == "CustomProjectMainMenu");
            foreach (var top in tops)
            {
                var origin = top.TranslatePoint(new Point(0, 0), window)!.Value;
                Assert.True(origin.X + top.Bounds.Width <= window.Bounds.Width + 1, $"menu '{top.Header}' ends at {origin.X + top.Bounds.Width:0} of {window.Bounds.Width:0}");
                Assert.True(top.Bounds.Height > 0);
            }
        }
        finally
        {
            window.Close();
            Se.Settings.Appearance.FontScale = oldFont; Nikse.SubtitleEdit.Logic.UiTheme.SetLayoutScale(oldLayout);
        }
    }
}

