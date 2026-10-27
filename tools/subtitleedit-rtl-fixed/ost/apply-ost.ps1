$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = 'src/ui/Features/Tools/OstTable'
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item "$source/OstTableWindow.cs" "$target/OstTableWindow.cs" -Force
Copy-Item "$source/MainViewModel.OstTable.cs" 'src/ui/Features/Main/MainViewModel.OstTable.cs' -Force
Copy-Item "$source/OstTableTests.cs" 'tests/UI/Features/Main/OstTableTests.cs' -Force

$path = 'src/ui/Features/Main/Layout/InitToolbar.cs'
$text = Get-Content $path -Raw
$needle = '        if (appearance.ToolbarShowFind)'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'OST toolbar anchor changed upstream.' }
$button = @'
        stackPanelLeft.Children.Add(vm.MakeOstToolbar());
        isLastSeparator = false;

'@
$text = $text.Replace($needle, $button + $needle)
Set-Content $path $text -Encoding utf8

# Professional editing access reuses the native frame/split/compare engines.
Copy-Item "$source/MainViewModel.ProEditing.cs" 'src/ui/Features/Main/MainViewModel.ProEditing.cs' -Force
Copy-Item "$source/SubtitleLineViewModel.ProEditing.cs" 'src/ui/Features/Main/SubtitleLineViewModel.ProEditing.cs' -Force
Copy-Item "$source/ProEditingTests.cs" 'tests/UI/Features/Main/ProEditingTests.cs' -Force
$path = 'src/ui/Features/Main/Layout/InitToolbar.cs'
$text = Get-Content $path -Raw
$needle = '        stackPanelLeft.Children.Add(vm.MakeOstToolbar());'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Frame toolbar anchor changed upstream.' }
$text = $text.Replace($needle, $needle + "`n        // Frame actions moved to main menu.")
Set-Content $path $text -Encoding utf8

# Direct frame transport in the video player controls, right after the Play slot.
# The player's own item order/visibility is respected: a hidden Play stays hidden.
$path = 'src/ui/Controls/VideoPlayer/VideoPlayerControl.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
function ExactVideo([string]$text, [string]$needle, [string]$replacement) {
    if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw "Video frame controls anchor changed upstream: $needle" }
    return $text.Replace($needle, $replacement)
}
$text = ExactVideo $text '        private readonly StackPanel _panelVolume;' @'
        private readonly StackPanel _panelVolume;
        private Control? _customFrameControls;

        /// <summary>Frame back/forward arrows placed directly after the Play slot.</summary>
        public Control? CustomFrameControls
        {
            get => _customFrameControls;
            set { _customFrameControls = value; ApplyControlsLayout(Se.Settings.Video.ControlsItems); }
        }

        private void AddCustomFrameControls(ref SeVideoControlsItemType? previousType)
        {
            if (_customFrameControls == null) return;
            var frameColumn = _gridProgress.ColumnDefinitions.Count;
            _gridProgress.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _customFrameControls.Margin = previousType == null ? new Thickness(0) : new Thickness(3, 0, 0, 0);
            _gridProgress.Add(_customFrameControls, 0, frameColumn);
            previousType = SeVideoControlsItemType.Play;
        }
'@
$text = ExactVideo $text "                if (control == null)`n" @'
                if (item.Type == SeVideoControlsItemType.Play && !item.IsVisible)
                {
                    AddCustomFrameControls(ref previousType);
                    continue;
                }

                if (control == null)
'@ + "`n"
$text = ExactVideo $text '                _gridProgress.Add(control, 0, column);' @'
                _gridProgress.Add(control, 0, column);
                if (item.Type == SeVideoControlsItemType.Play)
                {
                    AddCustomFrameControls(ref previousType);
                }
'@
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Main/Layout/InitVideoPlayer.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$text = ExactVideo $text '        vm.VideoPlayerControl = control;' "        vm.VideoPlayerControl = control;`n        control.CustomFrameControls = vm.MakeVideoFrameControls();"
Set-Content $path $text -Encoding utf8

# Local edition matching is proposal-only until individually approved.
Copy-Item "$source/VideoEditionSync.cs" 'src/ui/Features/Main/MainHelpers/VideoEditionSync.cs' -Force
Copy-Item "$source/MainViewModel.VideoEdition.cs" 'src/ui/Features/Main/MainViewModel.VideoEdition.cs' -Force
Copy-Item "$source/VideoEditionTests.cs" 'tests/UI/Features/Main/VideoEditionTests.cs' -Force
Copy-Item "$source/DirectorQuestions.cs" 'src/ui/Features/Main/MainHelpers/DirectorQuestions.cs' -Force
Copy-Item "$source/MainViewModel.DirectorQuestions.cs" 'src/ui/Features/Main/MainViewModel.DirectorQuestions.cs' -Force
Copy-Item "$source/DirectorQuestionsTests.cs" 'tests/UI/Features/Main/DirectorQuestionsTests.cs' -Force


$path = 'src/ui/UI.csproj'
$text = Get-Content $path -Raw
$match = [regex]::Match($text, '<PackageReference Include="Avalonia" Version="([^"]+)"')
if (-not $match.Success) { throw 'Could not determine Avalonia version.' }
$reference = '<PackageReference Include="Avalonia.Controls.DataGrid" Version="' + '12.1.2' + '" />'
$text = $text.Replace('<PackageReference Include="Avalonia.Desktop"', $reference + "`n`t`t<PackageReference Include=`"Avalonia.Desktop`"")
Set-Content $path $text -Encoding utf8

# Main UI keeps its physical layout regardless of UI language. Subtitle text remains RTL.
$path = 'src/ui/Features/Main/MainView.cs'
$text = Get-Content $path -Raw
$needle = '        var root = new DockPanel { Background = EditorChrome.WindowBrush };'
if (-not $text.Contains($needle)) { throw 'Main root layout anchor changed.' }
$text = $text.Replace($needle, '        var root = new DockPanel { Background = EditorChrome.WindowBrush, FlowDirection = Avalonia.Media.FlowDirection.LeftToRight };')
$needle = '        root.Children.Add(_vm.ContentGrid);'
if (-not $text.Contains($needle)) { throw 'Main content anchor changed.' }
$text = $text.Replace($needle, '        root.Children.Add(_vm.MakeOstHost(_vm.ContentGrid));')
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Main/MainHelpers/RightToLeftHelper.cs'
$text = Get-Content $path -Raw
$text = $text.Replace('            tableView.FlowDirection = flowDirection;', '            tableView.FlowDirection = FlowDirection.LeftToRight;')
# The embedded OST panel controls its own direction; the main RTL sweep must not touch it.
$needle = '        if (visual is ComboBox)'
$text = $text.Replace($needle, "        if (visual is Nikse.SubtitleEdit.Features.Tools.OstTable.OstTableView) return;`n`n" + $needle)
Set-Content $path $text -Encoding utf8

# Save the OST project before the main editor starts its close/cleanup sequence.
$path = 'src/ui/Features/Main/MainViewModel.cs'
$text = Get-Content $path -Raw
$needle = "    internal async void OnClosing(object? sender, WindowClosingEventArgs e)`n    {"
if (-not $text.Contains($needle)) {
    $text = $text.Replace("`r`n", "`n")
}
if (-not $text.Contains($needle)) { throw 'Main close handler anchor changed.' }
$text = $text.Replace($needle, $needle + "`n        if (!SaveOstBeforeClosing()) { e.Cancel = true; return; }")
$needle = "    private async Task<bool> HasChangesContinue()`n    {"
if (-not $text.Contains($needle)) { throw 'Document switch guard anchor changed.' }
$text = $text.Replace($needle, $needle + "`n        if (!SaveOstBeforeClosing()) return false;")
$needle = "    private void ResetSubtitle(SubtitleFormat? format = null)`n    {"
if (-not $text.Contains($needle)) { throw 'New document anchor changed.' }
$text = $text.Replace($needle, $needle + "`n        NewOstDocument();")
Set-Content $path $text -Encoding utf8

# A display-only marker shares the existing number column, preserving saved widths/order.
$path = 'src/ui/Features/Main/SubtitleLineViewModel.cs'
$text = Get-Content $path -Raw
$needle = '    public string NumberDisplay => IsReferenceOnly ? string.Empty : Number.ToString(CultureInfo.InvariantCulture);'
if (-not $text.Contains($needle)) { throw 'Number display anchor changed.' }
$text = $text.Replace($needle, @'
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    private bool _isInOst;

    public string NumberDisplay => IsReferenceOnly ? string.Empty : Number.ToString(CultureInfo.InvariantCulture) + (IsInOst ? " ✓" : "");
'@)
Set-Content $path $text -Encoding utf8

# Update the toolbar action immediately when the selection changes.
$path = 'src/ui/Features/Main/Layout/InitListViewAndEditBox.cs'
$text = Get-Content $path -Raw
$needle = '        vm.SubtitleGrid.SelectionChanged += vm.SubtitleGrid_SelectionChanged;'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Main selection anchor changed.' }
$text = $text.Replace($needle, $needle + "`n        vm.SubtitleGrid.SelectionChanged += (_, _) => vm.RefreshOstToolbar();")
Set-Content $path $text -Encoding utf8

# Text direction is separate from the fixed physical editor layout.
Copy-Item "$source/MenuTextDirection.cs" 'src/ui/Features/Main/MainHelpers/MenuTextDirection.cs' -Force
$path = 'src/ui/Features/Main/MainView.cs'
$text = Get-Content $path -Raw
$needle = '        ApplyEditorStyles();'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Main menu style anchor changed.' }
$text = $text.Replace($needle, $needle + "`n        MainHelpers.MenuTextDirection.AddStyles(Styles);")
Set-Content $path $text -Encoding utf8

# Preconfigure the existing local AI review flow without shipping user settings.
Copy-Item "$source/CustomHebrewAiReview.cs" 'src/ui/Features/Tools/AiReview/CustomHebrewAiReview.cs' -Force
$path = 'src/ui/Features/Tools/AiReview/AiReviewViewModel.cs'
$text = Get-Content $path -Raw
$needle = '        _windowService = windowService;'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'AI review settings anchor changed upstream.' }
$text = $text.Replace($needle, $needle + "`n        if (CustomHebrewAiReview.ApplyDefaults(Se.Settings.Tools.AiReview)) Se.SaveSettings();")
Set-Content $path $text -Encoding utf8

# The editor's break/unbreak action also cleans spacing in the selected rows.
$path = 'src/ui/Features/Main/MainViewModel.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$needle = '            s.Text = Utilities.AutoBreakLine(s.Text, language);'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Auto-break spacing anchor changed upstream.' }
$text = $text.Replace($needle, '            s.Text = Utilities.AutoBreakLine(SubtitleLineSpacing.Clean(s.Text, language), language);')
$needle = @'
            s.Text = hasBreak
                ? Utilities.UnbreakLine(s.Text)
                : Utilities.AutoBreakLine(s.Text, Se.Settings.General.SubtitleLineMaximumLength, 0, language);
'@
$needle = $needle.Replace("`r`n", "`n")
$replacement = @'
            var cleanText = SubtitleLineSpacing.Clean(s.Text, language);
            s.Text = hasBreak
                ? SubtitleLineSpacing.ToSingleLine(cleanText)
                : Utilities.AutoBreakLine(cleanText, Se.Settings.General.SubtitleLineMaximumLength, 0, language);
'@
$replacement = $replacement.Replace("`r`n", "`n")
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Toggle-break spacing anchor changed upstream.' }
$text = $text.Replace($needle, $replacement)
Set-Content $path $text -Encoding utf8

# Custom deterministic QC lives in the main list and shares the existing rule profiles.
Copy-Item "$source/CustomQc.cs" 'src/ui/Features/Main/MainHelpers/CustomQc.cs' -Force
Copy-Item "$source/MainViewModel.CustomQc.cs" 'src/ui/Features/Main/MainViewModel.CustomQc.cs' -Force
Copy-Item "$source/SeCustomQc.cs" 'src/ui/Logic/Config/SeCustomQc.cs' -Force
$path = 'src/ui/Logic/Config/SeTools.cs'
$text = Get-Content $path -Raw
$needle = '    public SeAiReview AiReview { get; set; } = new();'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'QC settings anchor changed upstream.' }
$text = $text.Replace($needle, $needle + "`n    public SeCustomQc CustomQc { get; set; } = new();")
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Main/Layout/InitToolbar.cs'
$text = Get-Content $path -Raw
$needle = '        stackPanelLeft.Children.Add(vm.MakeOstToolbar());'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'QC toolbar anchor changed upstream.' }
$text = $text.Replace($needle, $needle + "`n        stackPanelLeft.Children.Add(vm.MakeCustomQcToolbar());")
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Main/SubtitleLineViewModel.cs'
$text = Get-Content $path -Raw
$needle = '    private bool _isInOst;'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'QC marker anchor changed upstream.' }
$fields = @'
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    [NotifyPropertyChangedFor(nameof(AccessibleErrorText))]
    private string _customQcSummary = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    [NotifyPropertyChangedFor(nameof(AccessibleErrorText))]
    private string _customNameSummary = string.Empty;
'@
$text = $text.Replace($needle, $needle + "`n`n" + $fields)
$needle = '+ (IsInOst ? " ✓" : "");'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'QC number display anchor changed upstream.' }
$text = $text.Replace($needle, '+ (IsInOst ? " ✓" : "") + (string.IsNullOrEmpty(CustomQcSummary) ? "" : " ⚠") + (string.IsNullOrEmpty(CustomNameSummary) ? "" : CustomNameSummary.StartsWith("✕", StringComparison.Ordinal) ? " שמות ✕" : CustomNameSummary.StartsWith("?", StringComparison.Ordinal) ? " שמות ?" : " שמות ✓");')
$needle = '            return errors?.ToString() ?? string.Empty;'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'QC accessibility anchor changed upstream.' }
$text = $text.Replace($needle, '            if (!string.IsNullOrEmpty(CustomNameSummary)) Add(CustomNameSummary);' + "`n" + '            if (!string.IsNullOrEmpty(CustomQcSummary)) Add(CustomQcSummary.Replace("\n", ", "));' + "`n" + $needle)
Set-Content $path $text -Encoding utf8

# Project workflow, installed-user data and settings backup controls.
foreach ($name in @('CustomProject.cs', 'CustomWindowsIntegration.cs', 'CustomSettingsUi.cs', 'ProjectArchive.cs', 'CloudMfaClient.cs', 'CustomTranslateBatch.cs')) {
    Copy-Item "$source/$name" "src/ui/Features/Main/MainHelpers/$name" -Force
}
Copy-Item "$source/ProjectEpisodeContext.cs" 'src/ui/Features/Main/MainHelpers/ProjectEpisodeContext.cs' -Force
Copy-Item "$source/MainViewModel.EpisodeContext.cs" 'src/ui/Features/Main/MainViewModel.EpisodeContext.cs' -Force
Copy-Item "$source/EpisodeContextTests.cs" 'tests/UI/Features/Main/EpisodeContextTests.cs' -Force
Copy-Item "$source/MainViewModel.CustomProject.cs" 'src/ui/Features/Main/MainViewModel.CustomProject.cs' -Force
Copy-Item "$source/MainViewModel.ProjectArchive.cs" 'src/ui/Features/Main/MainViewModel.ProjectArchive.cs' -Force
Copy-Item "$source/MainViewModel.BatchTranslate.cs" 'src/ui/Features/Main/MainViewModel.BatchTranslate.cs' -Force
$path = 'src/ui/Features/Main/Layout/InitToolbar.cs'
$text = Get-Content $path -Raw
$needle = '        stackPanelLeft.Children.Add(vm.MakeCustomQcToolbar());'
if (-not $text.Contains($needle)) { throw 'Project toolbar anchor changed.' }
$text = $text.Replace($needle, $needle + "`n        // Project actions moved to main menu.")
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Main/SubtitleLineViewModel.cs'
$text = Get-Content $path -Raw
$needle = '    public string NumberDisplay =>'
if (-not $text.Contains($needle)) { throw 'Project display marker anchor changed.' }
$text = $text.Replace($needle, @'
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDisplay))]
    private string _customWorkMarker = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleErrorText))]
    private string _customWorkDescription = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRowHidden))]
    private bool _isOstFilterExcluded;
    public bool IsCustomRowHidden => IsHidden || IsOstFilterExcluded;

    public string NumberDisplay =>
'@)
$text = $text.Replace('(IsInOst ? " ✓" : "")', '(IsInOst ? " ✓" : "") + CustomWorkMarker')
$needle = '    private bool _isHidden;'
if (-not $text.Contains($needle)) { throw 'Hidden row anchor changed.' }
$text = $text.Replace($needle, '    [NotifyPropertyChangedFor(nameof(IsCustomRowHidden))]' + "`n" + $needle)
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Main/Layout/InitListViewAndEditBox.cs'
$text = Get-Content $path -Raw
$text = $text.Replace('new Binding(nameof(SubtitleLineViewModel.IsHidden)) { Converter = inverseBooleanConverter }', 'new Binding(nameof(SubtitleLineViewModel.IsCustomRowHidden)) { Converter = inverseBooleanConverter }')
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Logic/Config/Se.cs'
$text = Get-Content $path -Raw
$needle = '        IsPortable = !IsInstalledInProgramFiles;'
if (-not $text.Contains($needle)) { throw 'Installed data anchor changed.' }
$text = $text.Replace($needle, '        var customInstalled = System.IO.File.Exists(Path.Combine(AppContext.BaseDirectory, "custom-installed.json"));' + "`n" + '        IsPortable = !IsInstalledInProgramFiles && !customInstalled;')
$needle = '        DataFolder = ResolveDataFolder(IsPortable, ExePath, GetApplicationDataFolder());'
$text = $text.Replace($needle, '        DataFolder = customInstalled ? Path.Combine(GetApplicationDataFolder(), "Subtitle Edit Elior") : ResolveDataFolder(IsPortable, ExePath, GetApplicationDataFolder());')
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Options/Settings/SettingsWindow.cs'
$text = Get-Content $path -Raw
$needle = '        Content = page;'
if (-not $text.Contains($needle)) { throw 'Settings page anchor changed.' }
$text = $text.Replace($needle, '        Content = Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomSettingsUi.Wrap(page, this, vm.CustomReloadSettings);')
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Options/Settings/SettingsViewModel.cs'
$text = Get-Content $path -Raw
$needle = '    private async Task ImportSettings()'
if (-not $text.Contains($needle)) { throw 'Settings import anchor changed.' }
# Insert before the command attribute, so the new method is not treated as a relay command.
$text = $text.Replace("    [RelayCommand]`n" + $needle, "    internal void CustomReloadSettings() { LoadSettings(); SettingsImported = true; SettingsReplaced = true; }`n`n    [RelayCommand]`n" + $needle)
if (-not $text.Contains('internal void CustomReloadSettings')) {
    $text = $text.Replace("`r`n", "`n")
    $text = $text.Replace("    [RelayCommand]`n" + $needle, "    internal void CustomReloadSettings() { LoadSettings(); SettingsImported = true; SettingsReplaced = true; }`n`n    [RelayCommand]`n" + $needle)
}
if (-not $text.Contains('internal void CustomReloadSettings')) { throw 'Settings reload hook missing.' }
Set-Content $path $text -Encoding utf8

Copy-Item "$source/ProjectWorkflowTests.cs" 'tests/UI/Features/Main/ProjectWorkflowTests.cs' -Force
Copy-Item "$source/BatchTranslateTests.cs" 'tests/UI/Features/Main/BatchTranslateTests.cs' -Force

$path = 'src/ui/Features/Main/SubtitleLineViewModel.cs'
$text = Get-Content $path -Raw
$needle = '            return errors?.ToString() ?? string.Empty;'
$text = $text.Replace($needle, '            if (!string.IsNullOrEmpty(CustomWorkDescription)) Add(CustomWorkDescription);' + "`n" + $needle)
Set-Content $path $text -Encoding utf8

# Opt-in single-row advance after the ordinary stamp-end command. Keep all early
# guards, batch timing, explicit end-and-next commands and manual editors intact.
$path = 'src/ui/Logic/Config/SeTools.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$needle = '    public SeCustomQc CustomQc { get; set; } = new();'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Timing settings anchor changed upstream.' }
$text = $text.Replace($needle, $needle + "`n    public bool AutoAdvanceOnSetEnd { get; set; } = false;")
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Main/MainViewModel.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$start = $text.IndexOf('    private void WaveformSetEnd()')
$end = $text.IndexOf('    [RelayCommand]', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Stamp-end method anchor changed upstream.' }
$method = $text.Substring($start, $end - $start)
$selectionAnchor = '        var videoPositionSeconds = vp.Position;'
if (([regex]::Matches($method, [regex]::Escape($selectionAnchor))).Count -ne 1) { throw 'Stamp-end selection snapshot anchor changed upstream.' }
$selectionPatch = @'
        TableViewExtras.SyncSelectedItemsWithSelection(SubtitleGrid);
        var advanceAfterStamp = Se.Settings.Tools.AutoAdvanceOnSetEnd && SubtitleGridSelectedCount == 1;
        var videoPositionSeconds = vp.Position;
'@
$method = $method.Replace($selectionAnchor, $selectionPatch)
$needle = '        _updateAudioVisualizer = true;' 
if (([regex]::Matches($method, [regex]::Escape($needle))).Count -ne 1) { throw 'Stamp-end completion anchor changed upstream.' }
$replacement = @'
        _updateAudioVisualizer = true;
        if (advanceAfterStamp)
        {
            // Resolve from the stamped row: SelectedSubtitleIndex can lag the
            // TwoWay selection binding when a document has just been opened.
            var index = Subtitles.IndexOf(s);
            if (index >= 0 && index + 1 < Subtitles.Count)
            {
                SelectAndScrollToRowCentered(index + 1);
            }
        }
'@
$patched = $method.Replace($needle, $replacement)
$text = $text.Substring(0, $start) + $patched + $text.Substring($end)
Set-Content $path $text -Encoding utf8


$path = 'src/ui/Features/Main/Layout/InitMenu.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$needle = "        menu.DataContext = vm;`n        menu.Items.Clear();"
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Main menu anchor changed.' }
$frameAnchor = "        menu.Items.Add(new MenuItem`n        {`n            Header = l.Synchronization,"
$projectAnchor = "        menu.Items.Add(new MenuItem`n        {`n            Header = l.Options,"
foreach ($anchor in @($frameAnchor, $projectAnchor)) {
    if (([regex]::Matches($text, [regex]::Escape($anchor))).Count -ne 1) { throw 'Custom menu placement anchor changed.' }
}
$text = $text.Replace($frameAnchor, "        menu.Items.Add(vm.MakeProEditingMenu());`n`n" + $frameAnchor)
$text = $text.Replace($projectAnchor, "        menu.Items.Add(vm.MakeCustomProjectMenu());`n`n" + $projectAnchor)
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Options/Settings/SettingsViewModel.cs'
$text = Get-Content $path -Raw
$needle = 'UiTheme.GetUnscaledContent(Window) is SettingsPage page'
if (-not $text.Contains($needle)) { throw 'Settings page lookup anchor changed.' }
$text = $text.Replace($needle, 'Nikse.SubtitleEdit.Features.Main.MainHelpers.CustomSettingsUi.FindPage(Window) is SettingsPage page')
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Controls/VideoPlayer/VideoPlayerControl.cs'
$text = Get-Content $path -Raw
$text = $text.Replace('Se.Language.Options.Settings.VideoControlsPreviousSubtitle,', '"הכתובית הקודמת",').Replace('Se.Language.Options.Settings.VideoControlsNextSubtitle,', '"הכתובית הבאה",')
Set-Content $path $text -Encoding utf8

# Preserve native player for preferences unrelated to player/layout.
$path = 'src/ui/Features/Main/MainViewModel.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
function ReplaceSettingsAnchor([string]$needle, [string]$replacement) {
    if (([regex]::Matches($script:text, [regex]::Escape($needle))).Count -ne 1) { throw "Player settings anchor changed: $needle" }
    $script:text = $script:text.Replace($needle, $replacement)
}
ReplaceSettingsAnchor '        var oldSettngsSerialized = SettingsChangeSnapshot.Take();' @'
        var oldSettngsSerialized = SettingsChangeSnapshot.Take();
        _customPlayerSettingsBaseline = oldSettngsSerialized;
'@
ReplaceSettingsAnchor "    public void ApplySettings()`n    {" @'
    public void ApplySettings()
    {
        var currentPlayerSettings = SettingsChangeSnapshot.Take();
        var preserveCustomPlayer = !_applyLayoutAndZoomFromSettings &&
            CustomCanKeepPlayer(_customPlayerSettingsBaseline, currentPlayerSettings);
'@
ReplaceSettingsAnchor "            InitializeLibMpv();`n            InitializeFfmpeg();" @'
            if (!preserveCustomPlayer)
            {
                InitializeLibMpv();
                InitializeFfmpeg();
            }
'@
ReplaceSettingsAnchor "        RefreshGridColumnsFromSettings();`n        if (AreVideoControlsUndocked)" @'
        RefreshGridColumnsFromSettings();
        if (preserveCustomPlayer)
        {
            GetVideoPlayerControl()?.ApplyControlsLayout(Se.Settings.Video.ControlsItems);
        }
        else if (AreVideoControlsUndocked)
'@
$start = $text.IndexOf('    public void ApplySettings()')
$end = $text.IndexOf('        UpdateVideoOffsetStatus();', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'ApplySettings scope missing.' }
$part = $text.Substring($start, $end - $start)
$needle = "        if (vp != null)`n        {"
if (([regex]::Matches($part, [regex]::Escape($needle))).Count -ne 1) { throw 'Player refresh anchor changed.' }
$part = $part.Replace($needle, "        if (vp != null && !preserveCustomPlayer)`n        {")
$part = $part.Replace('        _applyLayoutAndZoomFromSettings = false;', "        _applyLayoutAndZoomFromSettings = false;`n        _customPlayerSettingsBaseline = currentPlayerSettings;")
$text = $text.Substring(0, $start) + $part + $text.Substring($end)
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Main/Layout/InitMenu.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$needle = '        menu.Items.Add(menuItemSsaTools);'
if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw 'Custom action redistribution anchor changed.' }
$text = $text.Replace($needle, $needle + @'

        vm.DistributeCustomProjectActions(
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, l.Synchronization)),
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, l.Translate)),
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, l.Tools)),
            menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, l.Options)));
'@)
Set-Content $path $text -Encoding utf8


