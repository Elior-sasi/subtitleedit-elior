$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'output/SubtitleEdit-Elior-5.4.0-beta8-Setup.exe'
$root = Join-Path $env:RUNNER_TEMP ('SEInstallerTest-' + [guid]::NewGuid().ToString('N'))
$old = Join-Path $root 'old'
$target = Join-Path $root 'installed'
$data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Subtitle Edit Elior'
if (Test-Path $data) { throw 'Installer smoke test requires an isolated runner profile.' }
New-Item -ItemType Directory $old -Force | Out-Null
'{"General":{},"Appearance":{},"sentinel":"keep-user-settings"}' | Set-Content (Join-Path $old 'Settings.json') -Encoding utf8
'private subtitle' | Set-Content (Join-Path $old 'episode.srt') -Encoding utf8
$before = (Get-FileHash (Join-Path $old 'Settings.json')).Hash
function Install {
    $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $target + '"'), ('/SOURCESETTINGS="' + $old + '"'), ('/LOG="' + (Join-Path $root 'setup.log') + '"'))
    $process = Start-Process -FilePath $setup -ArgumentList $args -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
}
Install
if (-not (Test-Path (Join-Path $target 'SubtitleEdit.exe'))) { throw 'Installed executable missing.' }
if (-not (Test-Path (Join-Path $target 'custom-installed.json'))) { throw 'Installed data marker missing.' }
if (-not (Test-Path (Join-Path $target 'custom-updater/gh.exe'))) { throw 'Private updater helper missing.' }
if ((Get-FileHash (Join-Path $data 'Settings.json')).Hash -ne $before) { throw 'Settings migration changed bytes.' }
if ((Get-FileHash (Join-Path $old 'Settings.json')).Hash -ne $before) { throw 'Source settings changed.' }
if (Test-Path (Join-Path $target 'episode.srt')) { throw 'User subtitle unexpectedly installed as program file.' }
$prog = Get-ItemProperty 'HKCU:/Software/Classes/Elior.SubtitleEdit.Subtitle'
if ($prog.AppUserModelID -ne 'Elior.SubtitleEdit.Hebrew') { throw 'Shell identity missing.' }
$shell = New-Object -ComObject WScript.Shell
$linkRoots = @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('CommonPrograms')) | Select-Object -Unique
$links = @($linkRoots | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.lnk' -Recurse -File -ErrorAction SilentlyContinue } | Where-Object { $_.Name -like '*Subtitle Edit*' })
$shortcutFound = $false
foreach ($link in $links) {
    $destination = $shell.CreateShortcut($link.FullName).TargetPath
    Write-Host "Start shortcut: $($link.FullName) -> $destination"
    if ($destination -eq (Join-Path $target 'SubtitleEdit.exe')) { $shortcutFound = $true }
}
if (-not $shortcutFound) {
    Get-Content (Join-Path $root 'setup.log') -Tail 80 | Write-Host
    throw 'Installed Start shortcut missing.'
}
'{"General":{},"Appearance":{},"sentinel":"existing-custom-data"}' | Set-Content (Join-Path $data 'Settings.json') -Encoding utf8
$existing = (Get-FileHash (Join-Path $data 'Settings.json')).Hash
New-Item -ItemType Directory (Join-Path $data 'CloudMfa') -Force | Out-Null
'{"ServiceUrl":"https://example.invalid","Language":"english_mfa"}' | Set-Content (Join-Path $data 'CloudMfa/config.json') -Encoding utf8
[IO.File]::WriteAllBytes((Join-Path $data 'CloudMfa/token.dpapi'), [byte[]](1..32))
$cloud = (Get-FileHash (Join-Path $data 'CloudMfa/config.json')).Hash + (Get-FileHash (Join-Path $data 'CloudMfa/token.dpapi')).Hash
Install
if ((Get-FileHash (Join-Path $data 'Settings.json')).Hash -ne $existing) { throw 'Reinstallation replaced existing settings.' }
if (((Get-FileHash (Join-Path $data 'CloudMfa/config.json')).Hash + (Get-FileHash (Join-Path $data 'CloudMfa/token.dpapi')).Hash) -ne $cloud) { throw 'Reinstallation changed cloud alignment settings or token.' }
if (Test-Path (Join-Path $target 'CloudMfa')) { throw 'Cloud alignment data appeared in the program folder.' }
Write-Host "Installer install/reinstall, immutable settings, shortcut and registry checks passed."
Write-Host "Installer SHA256: $((Get-FileHash $setup).Hash)"

