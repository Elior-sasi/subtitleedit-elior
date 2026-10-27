using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private Button? _customQcButton;
    private MenuFlyout? _customQcMenu;
    private DispatcherTimer? _customQcTimer;
    private ObservableCollection<SubtitleLineViewModel>? _customQcRows;
    private readonly HashSet<SubtitleLineViewModel> _customQcWatched = new();
    private List<QcIssue> _customQcIssues = new();
    private bool _customQcDirty = true;
    private string _customQcSettings = string.Empty;
    private List<QcTerm> _customDetectedNames = new();
    private int _customDetectedNamesHash;
    private int _customDetectedNamesGeneration = -1;
    private string _customDetectedNamesProfile = "";
    private string _customDetectedNamesGuide = "";
    private int _customDetectedNamesEpisodeRevision;

    internal void SetDetectedNameSuggestions(IEnumerable<QcTerm> terms)
    {
        _customDetectedNames = terms.Take(500).ToList();
        _customDetectedNamesHash = GetFastHash();
        _customDetectedNamesGeneration = _ostDocumentGeneration;
        _customDetectedNamesProfile = ActiveProfile?.Id ?? "";
        _customDetectedNamesGuide = ActiveProfile?.Guide ?? "";
        _customDetectedNamesEpisodeRevision = _episodeContextRevision;
        _customQcDirty = true;
        ScanCustomQc();
    }

    private IReadOnlyList<QcTerm> CurrentDetectedNameSuggestions()
    {
        if (_customDetectedNames.Count > 0 &&
            (_customDetectedNamesHash != GetFastHash() || _customDetectedNamesGeneration != _ostDocumentGeneration ||
             _customDetectedNamesProfile != (ActiveProfile?.Id ?? "") ||
             _customDetectedNamesGuide != (ActiveProfile?.Guide ?? "") || _customDetectedNamesEpisodeRevision != _episodeContextRevision))
        {
            _customDetectedNames.Clear();
            _customQcDirty = true;
        }
        return _customDetectedNames;
    }
    internal IReadOnlyList<QcIssue> CustomQcIssues => _customQcIssues;

    internal Control MakeCustomQcToolbar()
    {
        var menu = new MenuFlyout();
        var button = new Button
        {
            Name = "CustomQcToolbarButton", Content = "בדיקה", FontSize = 16,
            MinHeight = 36, MinWidth = 65, Background = Brushes.Transparent,
            [AutomationProperties.NameProperty] = "בדיקת כתוביות ללא AI",
        };
        _customQcButton = button;
        _customQcMenu = menu;
        button.Click += (_, _) =>
        {
            if (menu.IsOpen) { menu.Hide(); return; }
            FillCustomQcMenu(menu);
            menu.ShowAt(button);
        };
        button.AttachedToVisualTree += (_, _) => StartCustomQc();
        button.DetachedFromVisualTree += (_, _) => { if (ReferenceEquals(_customQcButton, button)) StopCustomQc(); };
        return button;
    }

    private void WatchCustomQcRows()
    {
        if (!ReferenceEquals(_customQcRows, Subtitles))
        {
            if (_customQcRows != null) _customQcRows.CollectionChanged -= CustomQcCollectionChanged;
            _customQcRows = Subtitles;
            _customQcRows.CollectionChanged += CustomQcCollectionChanged;
        }
        var current = Subtitles.ToHashSet();
        foreach (var row in _customQcWatched.Where(r => !current.Contains(r)).ToList())
        { row.PropertyChanged -= CustomQcRowChanged; _customQcWatched.Remove(row); }
        foreach (var row in current.Where(r => !_customQcWatched.Contains(r)))
        { row.PropertyChanged += CustomQcRowChanged; _customQcWatched.Add(row); }
    }

    private void StartCustomQc()
    {
        WatchCustomQcRows();
        _customQcDirty = true;
        _customQcTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _customQcTimer.Tick -= CustomQcTick;
        _customQcTimer.Tick += CustomQcTick;
        _customQcTimer.Start();
    }

    private void StopCustomQc()
    {
        _customQcTimer?.Stop();
        _customQcMenu?.Hide();
        if (_customQcRows != null) _customQcRows.CollectionChanged -= CustomQcCollectionChanged;
        _customQcRows = null;
        foreach (var row in _customQcWatched) row.PropertyChanged -= CustomQcRowChanged;
        _customQcWatched.Clear();
    }

    private void CustomQcCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { WatchCustomQcRows(); _customQcDirty = true; }
    private void CustomQcRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Text" or "StartTime" or "EndTime" or "Number" or "IsReferenceOnly" or "IsComment" or "CharactersPerSecond") _customQcDirty = true;
    }
    private void CustomQcTick(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_customQcRows, Subtitles)) { WatchCustomQcRows(); _customQcDirty = true; }
        CurrentDetectedNameSuggestions();
        var g = Se.Settings.General;
        var signature = $"{g.CurrentProfile}|{g.SubtitleLineMaximumLength}|{g.MaxNumberOfLines}|{g.SubtitleMaximumCharactersPerSeconds}|{g.SubtitleMinimumDisplayMilliseconds}|{g.SubtitleMaximumDisplayMilliseconds}|{g.MinimumBetweenLines.GetMilliseconds()}|{g.CpsLineLengthStrategy}|{g.CpsLineLengthIgnoreTagsRegex}|{CustomQcGlossary()}|{System.Text.Json.JsonSerializer.Serialize(ActiveProfile?.Terms)}";
        if (signature != _customQcSettings) { _customQcSettings = signature; _customQcDirty = true; }
        if (_customQcDirty) ScanCustomQc();
    }
    private string CustomQcGlossary() => Se.Settings.Tools.CustomQc.GlossariesByProfile.TryGetValue(Se.Settings.General.CurrentProfile ?? "Default", out var value) ? value : string.Empty;

    internal void ScanCustomQc()
    {
        var g = Se.Settings.General;
        CustomQc.TryParseTerms(CustomQcGlossary(), out var terms, out _);
        if (ActiveProfile != null)
            terms.AddRange(ActiveProfile.Terms.Where(t => !string.IsNullOrWhiteSpace(t.Preferred))
                .Select(t => new QcTerm(t.Preferred, t.Variants)));
        var detected = CurrentDetectedNameSuggestions().Where(d => !terms.Any(t =>
            t.Preferred == d.Preferred || t.Variants.Contains(d.Preferred) || d.Variants.Contains(t.Preferred))).ToArray();
        var rows = Subtitles.Where(r => !r.IsReferenceOnly && !r.IsComment && !r.IsDefault).Select(r => new QcRow(r.Id, r.Number, r.Text, r.StartTime, r.EndTime, r.CharactersPerSecond)).ToList();
        _customQcIssues = CustomQc.Scan(rows, new(g.SubtitleLineMaximumLength, g.MaxNumberOfLines, g.SubtitleMaximumCharactersPerSeconds,
            g.SubtitleMinimumDisplayMilliseconds, g.SubtitleMaximumDisplayMilliseconds, g.MinimumBetweenLines.GetMilliseconds()), terms);
        var byRow = _customQcIssues.GroupBy(i => i.RowId).ToDictionary(x => x.Key, x => string.Join("\n", x.Select(i => i.Message).Distinct()));
        foreach (var row in Subtitles)
        {
            row.CustomQcSummary = byRow.GetValueOrDefault(row.Id, string.Empty);
            row.CustomNameSummary = row.IsReferenceOnly || row.IsComment || row.IsDefault
                ? string.Empty : CustomQc.CombineNameSummaries(CustomQc.NameSummary(row.Text, terms),
                    CustomQc.DetectedNameSummary(row.Text, ActiveProfile?.Guide ?? "", detected));
        }
        if (_customQcButton != null)
        {
            _customQcButton.Content = new TextBlock { Text = _customQcIssues.Count == 0 ? "בדיקה ✓" : $"בדיקה ⚠ {byRow.Count}", FontSize = 16, FlowDirection = FlowDirection.RightToLeft };
            AutomationProperties.SetName(_customQcButton, $"בדיקת כתוביות: {_customQcIssues.Count} התראות ב־{byRow.Count} כתוביות");
        }
        _customQcDirty = false;
    }

    private static MenuItem QcItem(string text, Action action)
    {
        var item = new MenuItem
        {
            Header = new TextBlock { Text = text, FlowDirection = FlowDirection.RightToLeft, MaxWidth = 580, TextWrapping = TextWrapping.Wrap },
            FlowDirection = FlowDirection.LeftToRight, [AutomationProperties.NameProperty] = text,
        };
        MenuTextDirection.ApplyHeaderFont(item);
        item.Click += (_, _) => action();
        return item;
    }

    private void FillCustomQcMenu(MenuFlyout menu)
    {
        ScanCustomQc();
        menu.Items.Clear();
        menu.Items.Add(QcItem($"פרופיל לקוח: {Se.Settings.General.CurrentProfile} — בחירה ועריכה…", () => ShowChooseProfileCommand.Execute(null)));
        menu.Items.Add(QcItem("זהה שמות מהמדריך ומהפרק באמצעות Gemini…", () => { _ = FindProjectTerms(); }));
        menu.Items.Add(QcItem("מילון שמות ומונחים לפרופיל…", () => Dispatcher.UIThread.Post(ShowCustomQcGlossary)));
        menu.Items.Add(QcItem("ניקוי תווים נסתרים בשורות מסומנות…", () => ShowToolsRemoveUnicodeCharactersSelectedLinesCommand.Execute(null)));
        menu.Items.Add(QcItem("בדיקת טבלת OST לפני ייצוא", () => Dispatcher.UIThread.Post(ShowCustomOstCheck)));
        menu.Items.Add(QcItem("דוח שגיאות מפורט…", () => ListErrorsCommand.Execute(null)));
        menu.Items.Add(new Separator());
        if (_customQcIssues.Count == 0)
            menu.Items.Add(QcNotice("לא נמצאו התראות לפי הכללים והמילון הנוכחיים"));
        else
        {
            menu.Items.Add(QcItem("עבור לכתובית הבעייתית הבאה", NavigateNextCustomQc));
            foreach (var issue in _customQcIssues.Take(40))
                menu.Items.Add(QcItem($"כתובית {issue.Number}: {issue.Message}", () => SelectCustomQcIssue(issue)));
            if (_customQcIssues.Count > 40)
                menu.Items.Add(QcNotice($"מוצגות 40 מתוך {_customQcIssues.Count} התראות; אפשר להמשיך עם ההתראה הבאה"));
        }
    }

    private static MenuItem QcNotice(string text)
    {
        var item = QcItem(text, () => { }); item.IsEnabled = false; return item;
    }

    internal void SelectCustomQcIssue(QcIssue issue)
    {
        var row = Subtitles.FirstOrDefault(r => r.Id == issue.RowId);
        if (row == null) return;
        SubtitleGrid.SelectedItem = row;
        SubtitleGrid.ScrollIntoView(row);
        ShowStatus(issue.Message, 8000);
    }
    private void NavigateNextCustomQc()
    {
        ScanCustomQc();
        if (_customQcIssues.Count == 0) return;
        var selected = SubtitleGrid.SelectedItem as SubtitleLineViewModel;
        var current = selected == null ? -1 : Subtitles.IndexOf(selected);
        var positions = Subtitles.Select((row, index) => (row.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var ordered = _customQcIssues.OrderBy(i => positions.GetValueOrDefault(i.RowId, int.MaxValue)).ToList();
        var next = ordered.FirstOrDefault(i => positions.GetValueOrDefault(i.RowId, -1) > current) ?? ordered[0];
        SelectCustomQcIssue(next);
    }

    private void ShowCustomQcGlossary()
    {
        if (_customQcButton == null || TopLevel.GetTopLevel(_customQcButton) == null) return;
        var profile = Se.Settings.General.CurrentProfile ?? "Default";
        var input = new TextBox { Text = CustomQcGlossary(), AcceptsReturn = true, Height = 180, Width = 480, FontSize = 16, FlowDirection = FlowDirection.RightToLeft, PlaceholderText = "שם תקני=חלופה1|חלופה2" };
        AutomationProperties.SetName(input, "מילון שמות ומונחים; שם תקני שווה חלופות המופרדות בקו אנכי");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, FontSize = 16, FlowDirection = FlowDirection.RightToLeft };
        var flyout = new Flyout();
        var save = new Button { Content = "שמור מילון", FontSize = 16 };
        save.Click += (_, _) =>
        {
            if (!CustomQc.TryParseTerms(input.Text ?? "", out _, out var error)) { status.Text = error; return; }
            Se.Settings.Tools.CustomQc.GlossariesByProfile[profile] = input.Text ?? "";
            Se.SaveSettings(); _customQcDirty = true; ScanCustomQc(); flyout.Hide();
        };
        flyout.Content = new StackPanel
        {
            Spacing = 10, Margin = new Thickness(12), FlowDirection = FlowDirection.LeftToRight,
            Children =
            {
                new TextBlock { Text = $"מילון לפרופיל {profile}\nכל שורה: שם תקני=חלופה1|חלופה2\nהמילון מסמן חלופות לבדיקה; ההכרעה נשארת שלך.", FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap, FontSize = 16, MaxWidth = 480 },
                input, status, save,
            },
        };
        flyout.ShowAt(_customQcButton);
        input.Focus();
    }

    private void ShowCustomOstCheck()
    {
        var errors = _ostView == null ? new List<string> { "טבלת OST ריקה" } : _ostView.ValidateForExport();
        if (_customQcButton == null || TopLevel.GetTopLevel(_customQcButton) == null) return;
        var flyout = new Flyout
        {
            Content = new ScrollViewer { MaxHeight = 380, MaxWidth = 580, Content = new TextBlock
            {
                Text = errors.Count == 0 ? "בדיקת OST עברה: טקסט ותזמונים קיימים, ללא כפילויות זהות." : string.Join("\n", errors),
                FontSize = 16, FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
            } },
        };
        flyout.ShowAt(_customQcButton);
    }
}


