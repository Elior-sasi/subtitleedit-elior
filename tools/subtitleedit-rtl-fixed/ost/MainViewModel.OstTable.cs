using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Tools.OstTable;
using Nikse.SubtitleEdit.Logic;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private OstTableView? _ostView;
    private string? _ostSourceFile;
    private DispatcherTimer? _ostMonitor;
    private bool _ostCloseHooked;
    private int _ostDocumentGeneration;
    private int _ostViewGeneration;
    private string _ostToolbarText = "OST +";
    private string _ostActionName = "הוסף כתוביות מסומנות לטבלת OST";
    public string OstToolbarText => _ostToolbarText;
    public string OstActionName => _ostActionName;

    internal void NewOstDocument() { _ostDocumentGeneration++; _customOstOnly = false; }

    // OST never takes any editor space. This hook only restores row membership.
    internal Control MakeOstHost(Control editor)
    {
        _ostMonitor ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ostMonitor.Tick -= MonitorOst;
        _ostMonitor.Tick += MonitorOst;
        _ostMonitor.Start();
        return editor;
    }

    private void MonitorOst(object? sender, EventArgs e)
    {
        if (!IsEmpty && EnsureOstProject()) { RefreshOstMarkers(); MonitorCustomProject(); }
    }

    internal bool SaveOstBeforeClosing() => _ostView?.TrySave() ?? true;

    private List<SubEntry> SourceOstRows(IEnumerable<SubtitleLineViewModel> rows) => rows
        .Where(line => !line.IsReferenceOnly).Select(line => new SubEntry
        {
            SourceId = line.Id,
            StartTime = (line.StartTime + TimeSpan.FromMilliseconds(Nikse.SubtitleEdit.Logic.Config.Se.Settings.General.CurrentVideoOffsetInMs)).ToString(@"hh\:mm\:ss\,fff"),
            Text = line.Text ?? "",
        }).ToList();

    private bool EnsureOstProject()
    {
        if (Window == null) return false;
        // Saving the same rows under a new name keeps their OST membership.
        var linkedIds = _ostView?.LinkedIds();
        if (_ostView != null && _ostViewGeneration == _ostDocumentGeneration && _ostSourceFile != _subtitleFileName &&
            Subtitles.Any(line => linkedIds!.Contains(line.Id)))
        {
            _ostSourceFile = _subtitleFileName;
            _ostView.RebindSubtitleFile(_subtitleFileName ?? "");
        }
        if (_ostView == null || _ostSourceFile != _subtitleFileName || _ostViewGeneration != _ostDocumentGeneration)
        {
            if (_ostView != null && !_ostView.TrySaveAndStop()) return false;
            var title = string.IsNullOrWhiteSpace(_subtitleFileName) ? "OST" : Path.GetFileNameWithoutExtension(_subtitleFileName);
            _ostView = new OstTableView(new List<SubEntry>(), title, UiTheme.IsDarkThemeEnabled(), _subtitleFileName ?? "");
            _ostSourceFile = _subtitleFileName;
            _ostViewGeneration = _ostDocumentGeneration;
            _ostView.DialogOwner = Window;
            _ostView.StatusChanged = message => ShowStatus(message, 6000);
            _ostView.SelectedRowsProvider = () => _ostSourceFile == _subtitleFileName && _ostViewGeneration == _ostDocumentGeneration ? SourceOstRows(SubtitleGridSelectedItems) : new();
            _ostView.SourceRowsProvider = () => _ostSourceFile == _subtitleFileName && _ostViewGeneration == _ostDocumentGeneration ? SourceOstRows(Subtitles) : new();
        }
        if (!_ostCloseHooked)
        {
            Window.Closed += (_, _) => { _ostMonitor?.Stop(); _ostView?.TrySaveAndStop(); };
            _ostCloseHooked = true;
        }
        return true;
    }

    internal void RefreshOstMarkers()
    {
        _ostView?.SyncLinkedRows();
        var ids = _ostSourceFile == _subtitleFileName && _ostViewGeneration == _ostDocumentGeneration ? _ostView?.LinkedIds() : null;
        foreach (var line in Subtitles) { line.IsInOst = !line.IsReferenceOnly && (ids?.Contains(line.Id) ?? false); line.IsOstFilterExcluded = _customOstOnly && !line.IsInOst; }
        RefreshOstToolbar();
    }

    internal void RefreshOstToolbar()
    {
        var selected = SubtitleGridSelectedItems;
        var remove = selected.Count > 0 && selected.All(line => line.IsInOst);
        var text = remove ? "OST −" : "OST +";
        var name = remove ? "הסר כתוביות מסומנות מטבלת OST" : "הוסף כתוביות מסומנות לטבלת OST";
        if (_ostToolbarText != text) { _ostToolbarText = text; OnPropertyChanged(nameof(OstToolbarText)); }
        if (_ostActionName != name) { _ostActionName = name; OnPropertyChanged(nameof(OstActionName)); }
    }

    [RelayCommand]
    private void ShowOstTable()
    {
        if (IsEmpty) { ShowSubtitleNotLoadedMessage(); return; }
        if (!EnsureOstProject()) return;
        var selected = SubtitleGridSelectedItems;
        if (selected.Count == 0) { ShowStatus("סמן כתובית ברשימה תחילה."); return; }
        _ostView!.SyncLinkedRows();
        var included = _ostView.LinkedIds();
        if (selected.All(line => included.Contains(line.Id))) _ostView.RemoveMainSelection();
        else _ostView.AddMainSelection();
        RefreshOstMarkers();
        _ostView.TrySave();
    }

    [RelayCommand]
    private void RemoveSelectedFromOst()
    {
        if (!EnsureOstProject()) return;
        _ostView!.RemoveMainSelection();
        RefreshOstMarkers();
        _ostView.TrySave();
    }

    [RelayCommand]
    private async Task ExportOstWord()
    {
        if (EnsureOstProject()) await _ostView!.ExportWordAsync();
    }

    [RelayCommand]
    private async Task SaveOstProject()
    {
        if (EnsureOstProject()) await _ostView!.SaveProjectAsync();
    }

    [RelayCommand]
    private async Task OpenOstProject()
    {
        if (!EnsureOstProject()) return;
        await _ostView!.OpenProjectAsync();
        RefreshOstMarkers();
        _ostView.TrySave();
    }

    internal Control MakeOstToolbar()
    {
        var add = new Button
        {
            Name = "OstTableToolbarButton", Command = ShowOstTableCommand,
            FontSize = 17, FontWeight = FontWeight.Bold, MinWidth = 68, MinHeight = 36,
            Background = Brushes.Transparent,
            [ToolTip.TipProperty] = new TextBlock { Text = "הוספה או הסרה מהטבלה בלחיצה אחת.\nסימן ✓ ליד המספר מציין שהכתובית נכללת בטבלה.", FlowDirection = FlowDirection.RightToLeft, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, FontSize = 16 },
        };
        add.Bind(ContentControl.ContentProperty, new Binding(nameof(OstToolbarText)) { Source = this });
        add.Bind(AutomationProperties.NameProperty, new Binding(nameof(OstActionName)) { Source = this });
        AttachOstTooltipDismissal(add);
        var menu = new MenuFlyout();
        menu.Items.Add(MakeOstMenuItem("כותרת ופרטי OST…", new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(EditOstDetails)));
        menu.Items.Add(MakeOstMenuItem("ייצוא OST ל־Word…", ExportOstWordCommand));
        menu.Items.Add(MakeOstMenuItem("הצג שורות OST בלבד / הצג הכול", new CommunityToolkit.Mvvm.Input.RelayCommand(ToggleCustomOstFilter)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeOstMenuItem("שמור פרויקט OST בשם…", SaveOstProjectCommand));
        menu.Items.Add(MakeOstMenuItem("פתח פרויקט OST…", OpenOstProjectCommand));
        var options = new Button
        {
            Name = "OstOptionsToolbarButton", Content = "▾", MinHeight = 36, MinWidth = 28,
            Flyout = menu, Background = Brushes.Transparent,
            [AutomationProperties.NameProperty] = "אפשרויות OST וייצוא ל־Word",
            [ToolTip.TipProperty] = new TextBlock { Text = "ייצוא ושמירת טבלת OST", FlowDirection = FlowDirection.RightToLeft, FontSize = 16 },
        };
        AttachOstTooltipDismissal(options);
        return new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight, Children = { add, options } };
    }

    private static MenuItem MakeOstMenuItem(string label, System.Windows.Input.ICommand command)
    {
        var item = new MenuItem
        {
            // Flyouts have their own presentation root. Set direction on the text
            // itself instead of relying on inheritance from the menu row/window.
            FlowDirection = FlowDirection.LeftToRight,
            Header = new TextBlock
            {
                Text = label, FlowDirection = FlowDirection.RightToLeft,
                TextAlignment = TextAlignment.Right,
            },
            Command = command,
            [AutomationProperties.NameProperty] = label,
        };
        Nikse.SubtitleEdit.Features.Main.MainHelpers.MenuTextDirection.ApplyHeaderFont(item);
        return item;
    }

    private static void AttachOstTooltipDismissal(Button button)
    {
        ToolTip.SetShouldUseOverlayLayer(button, true);
        ToolTip.SetShowDelay(button, 600);
        button.PointerExited += (_, _) => ToolTip.SetIsOpen(button, false);
        button.Click += (_, _) => ToolTip.SetIsOpen(button, false);
        button.DetachedFromVisualTree += (_, _) => ToolTip.SetIsOpen(button, false);
        button.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) =>
            DispatcherTimer.RunOnce(() => ToolTip.SetIsOpen(button, false), TimeSpan.FromSeconds(10)));
    }

}

