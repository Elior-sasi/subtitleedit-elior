$ErrorActionPreference = 'Stop'
Copy-Item "$PSScriptRoot/PrivateReleaseTests.cs" 'tests/UI/Features/Main/PrivateReleaseTests.cs' -Force
Copy-Item "$PSScriptRoot/CustomUpdateChannel.cs" 'src/ui/Features/Help/CheckForUpdates/CustomUpdateChannel.cs' -Force
Copy-Item "$PSScriptRoot/CustomUpdateInstaller.cs" 'src/ui/Features/Help/CheckForUpdates/CustomUpdateInstaller.cs' -Force

function Replace-Section([string]$text, [string]$start, [string]$end, [string]$replacement) {
    $text = $text.Replace("`r`n", "`n")
    $a = $text.IndexOf($start, [StringComparison]::Ordinal)
    if ($a -lt 0) { throw "Custom update start anchor changed: $start" }
    $b = $text.IndexOf($end, $a + $start.Length, [StringComparison]::Ordinal)
    if ($b -lt 0) { throw "Custom update end anchor changed: $end" }
    return $text.Substring(0, $a) + $replacement + "`n`n" + $text.Substring($b)
}

# Suppress upstream-only startup alerts; the private channel is opened with the
# user's signed-in browser. An official installer must never overwrite our build.
$path = 'src/ui/Features/Help/CheckForUpdates/UpdateCheckService.cs'
$text = Get-Content $path -Raw
$text = Replace-Section $text '    public async Task<UpdateCheckResult> CheckForUpdates()' "    /// <summary>`n    /// Downloads the changelog" @'
    public Task<UpdateCheckResult> CheckForUpdates() => CustomUpdateChannel.CheckForUpdatesAsync();
'@
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Help/CheckForUpdates/CheckForUpdatesViewModel.cs'
$text = Get-Content $path -Raw
$old = '    private const string ReleasesUrl = "https://github.com/SubtitleEdit/subtitleedit/releases";'
if (-not $text.Contains($old)) { throw 'Custom download URL anchor changed.' }
$text = $text.Replace($old, '    private const string ReleasesUrl = CustomUpdateChannel.DownloadPage;')
$text = Replace-Section $text '    public async Task CheckForUpdates()' "    [RelayCommand]`n    private async Task OpenDownloadPage()" @'
    public async Task CheckForUpdates()
    {
        if (IsDownloading) return;
        IsDownloadLinkVisible = false;
        IsInstallVisible = false;
        StatusText = "בודק עדכונים של הגרסה שלנו…";
        try
        {
            var result = await _updateCheckService.CheckForUpdates();
            ChangeLogText = result.ChangeLogText;
            StatusText = CustomUpdateChannel.Status;
            IsDownloadLinkVisible = CustomUpdateChannel.AuthenticationRequired;
            IsInstallVisible = result.IsNewVersionAvailable && !CustomUpdateChannel.IsInstallPending;
        }
        catch
        {
            StatusText = "לא ניתן לבדוק עדכונים כרגע. נסה שוב מאוחר יותר.";
            ChangeLogText = CustomUpdateChannel.Description;
        }
    }
'@
$text = Replace-Section $text "    [RelayCommand]`n    private async Task OpenDownloadPage()" "    [RelayCommand]`n    private async Task InstallUpdate()" @'
    [RelayCommand]
    private async Task OpenDownloadPage()
    {
        if (IsDownloading) return;
        _downloadCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        IsDownloading = true;
        IsDownloadLinkVisible = false;
        StatusText = "התחברות חד־פעמית ל־GitHub: הדבק בדפדפן את הקוד שהועתק ללוח.";
        try
        {
            await CustomUpdateChannel.LoginAsync(message => Dispatcher.UIThread.Post(() => StatusText = message), _downloadCancellation.Token);
        }
        catch (OperationCanceledException) { StatusText = "ההתחברות בוטלה."; }
        catch (Exception ex) { StatusText = ex.Message; IsDownloadLinkVisible = true; }
        finally { IsDownloading = false; }
        await CheckForUpdates();
    }
'@
$text = Replace-Section $text "    [RelayCommand]`n    private async Task InstallUpdate()" '    private bool CanOpenSettings' @'
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (IsDownloading || CustomUpdateChannel.IsInstallPending) return;
        IsDownloading = true;
        IsInstallVisible = false;
        DownloadProgress = 0;
        StatusText = "מוריד את העדכון שלנו. אפשר לשמור את העבודה לפני הסגירה.";
        _downloadCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            var progress = new Progress<float>(p => DownloadProgress = p * 100);
            var payload = await CustomUpdateChannel.DownloadAsync(progress, _downloadCancellation.Token);
            CustomUpdateChannel.StartInstallAfterExit(payload);
            StatusText = "העדכון יותקן לאחר שכל חלונות התוכנה ייסגרו.";
            Window?.Close();
            Dispatcher.UIThread.Post(MainWindowFactory.CloseAllMainWindows);
        }
        catch (OperationCanceledException) { StatusText = "ההורדה בוטלה. הגרסה הקיימת לא שונתה."; }
        catch (Exception ex) { StatusText = ex.Message; IsInstallVisible = true; }
        finally { IsDownloading = false; }
    }
'@
$text = $text.Replace('private bool CanOpenSettings => !IsDownloading;', 'private bool CanOpenSettings => false;')
if ($text.Contains('_autoUpdateService.DownloadAsync')) { throw 'An upstream install path remains in the custom update dialog.' }
Set-Content $path $text -Encoding utf8

$path = 'src/ui/Features/Help/CheckForUpdates/CheckForUpdatesWindow.cs'
$text = Get-Content $path -Raw
$old = 'EditorChrome.MakeTextToolButton(IconNames.Web, Se.Language.Help.CheckForUpdatesDownloadNewVersion, vm.OpenDownloadPageCommand)'
if (-not $text.Contains($old)) { throw 'Custom authentication button anchor changed.' }
$text = $text.Replace($old, 'EditorChrome.MakeTextToolButton(IconNames.Web, "התחברות ל־GitHub", vm.OpenDownloadPageCommand)')
$text = $text.Replace('EditorChrome.MakeTextToolButton(IconNames.Download, Se.Language.Help.CheckForUpdatesInstallAndRestart, vm.InstallUpdateCommand)', 'EditorChrome.MakeTextToolButton(IconNames.Download, "הורד והתקן", vm.InstallUpdateCommand)')
$text = $text.Replace('FontSize = 13.5,', 'FontSize = 17,').Replace('FontSize = 12.5,', 'FontSize = 16,').Replace('TextWrapping = TextWrapping.NoWrap,', 'TextWrapping = TextWrapping.Wrap,')
Set-Content $path $text -Encoding utf8


