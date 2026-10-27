namespace Nikse.SubtitleEdit.Features.Help.CheckForUpdates;

internal static class CustomUpdateInstaller
{
    internal const string Script = """
param(
    [int]$WaitProcessId,
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$TargetDirectory,
    [switch]$SkipLaunch
)
$ErrorActionPreference = 'Stop'
$backup = Join-Path (Split-Path $SourceDirectory -Parent) 'backup'
$changed = [Collections.Generic.List[string]]::new()
$created = [Collections.Generic.List[string]]::new()
try {
    if ($WaitProcessId -gt 0) { Wait-Process -Id $WaitProcessId -ErrorAction SilentlyContinue }
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory 'SubtitleEdit.exe'))) { throw 'Missing SubtitleEdit.exe' }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $root = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\') + '\'
    foreach ($file in (Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($root.Length)
        # User files are never update payloads, even if accidentally packaged.
        if ($relative -match '(?i)(^|\\)(Settings[^\\]*\.json|.*\.sework\.json|.*\.dpapi|.*\.(srt|ass|ssa|vtt|ostproject))$' -or $relative -match '(?i)^(CloudMfa|CustomProjects|SettingsBackups)\\') { continue }
        $target = Join-Path $TargetDirectory $relative
        $old = Join-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        if (Test-Path -LiteralPath $target) {
            New-Item -ItemType Directory -Path (Split-Path $old -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $old -Force
            $changed.Add($relative)
        } else { $created.Add($relative) }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    if (-not $SkipLaunch) { Start-Process -FilePath (Join-Path $TargetDirectory 'SubtitleEdit.exe') -WorkingDirectory $TargetDirectory }
    try { Remove-Item -LiteralPath (Split-Path $SourceDirectory -Parent) -Recurse -Force } catch { }
    exit 0
} catch {
    $failure = $_.Exception.Message
    foreach ($relative in $changed) {
        try { Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination (Join-Path $TargetDirectory $relative) -Force } catch { }
    }
    foreach ($relative in $created) {
        try { Remove-Item -LiteralPath (Join-Path $TargetDirectory $relative) -Force -ErrorAction SilentlyContinue } catch { }
    }
    $failure | Set-Content -LiteralPath (Join-Path (Split-Path $SourceDirectory -Parent) 'update-error.txt') -Encoding utf8
    if (-not $SkipLaunch) {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show("The update could not finish. Your settings were preserved. Recovery files are saved next to this installer.`n$failure", 'Subtitle Edit update') | Out-Null
    }
    exit 1
}
""";
}

