$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'output/SubtitleEdit-Elior-5.4.0-rc3-Setup.exe'
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
# Simulate legacy Open With registrations and an unrelated extension default.
# Protected UserChoice values are observed, never synthesized or rewritten.
$formats = @{
    srt = 'SubRip subtitle file'
    ass = 'Advanced SubStation Alpha subtitle file'
    ssa = 'SubStation Alpha subtitle file'
    vtt = 'WebVTT subtitle file'
}
$userChoices = @{}
foreach ($ext in $formats.Keys) {
    $extension = "HKCU:/Software/Classes/.$ext"
    New-Item "$extension/OpenWithProgids" -Force | Out-Null
    Set-Item $extension -Value 'Unrelated.Existing.Application'
    New-ItemProperty "$extension/OpenWithProgids" -Name 'Unrelated.Existing.Application' -Value '' -Force | Out-Null
    New-ItemProperty "$extension/OpenWithProgids" -Name 'Elior.SubtitleEdit.Subtitle' -Value '' -Force | Out-Null
    $choice = "HKCU:/Software/Microsoft/Windows/CurrentVersion/Explorer/FileExts/.$ext/UserChoice"
    $userChoices[$ext] = if (Test-Path $choice) { (Get-ItemProperty $choice | Select-Object ProgId, Hash | ConvertTo-Json -Compress) } else { 'absent' }
}
function Test-FileAssociations {
    foreach ($ext in $formats.Keys) {
        $id = 'Elior.SubtitleEdit.' + $ext.ToUpperInvariant()
        $key = "HKCU:/Software/Classes/$id"
        if ((Get-Item $key).GetValue('') -cne $formats[$ext]) { throw "Wrong friendly type for $ext" }
        if ((Get-Item $key).GetValue('AppUserModelID') -ne 'Elior.SubtitleEdit.Hebrew') { throw "Wrong shell identity for $ext" }
        if ((Get-Item "$key/DefaultIcon").GetValue('') -ne "$target\SubtitleEdit.exe,0") { throw "Wrong icon for $ext" }
        if ((Get-Item "$key/shell/open/command").GetValue('') -ne ('"' + $target + '\SubtitleEdit.exe" "%1"')) { throw "Wrong quoted open command for $ext" }
        $caps = Get-Item 'HKCU:/Software/SubtitleEditElior/Capabilities/FileAssociations'
        if ($caps.GetValue(".$ext") -ne $id) { throw "Wrong capability for $ext" }
        $extension = "HKCU:/Software/Classes/.$ext"
        $openWith = Get-Item "$extension/OpenWithProgids"
        if ($openWith.GetValueNames() -notcontains $id) { throw "Missing Open With entry for $ext" }
        if ($openWith.GetValueNames() -contains 'Elior.SubtitleEdit.Subtitle') { throw "Legacy generic Open With entry remains for $ext" }
        if ($openWith.GetValueNames() -notcontains 'Unrelated.Existing.Application') { throw "Other application registration changed for $ext" }
        if ((Get-Item $extension).GetValue('') -ne 'Unrelated.Existing.Application') { throw "Extension default changed for $ext" }
        $choice = "HKCU:/Software/Microsoft/Windows/CurrentVersion/Explorer/FileExts/.$ext/UserChoice"
        $after = if (Test-Path $choice) { (Get-ItemProperty $choice | Select-Object ProgId, Hash | ConvertTo-Json -Compress) } else { 'absent' }
        if ($after -cne $userChoices[$ext]) { throw "Protected UserChoice changed for $ext" }
    }
    $legacy = Get-Item 'HKCU:/Software/Classes/Elior.SubtitleEdit.Subtitle/shell/open/command'
    if ($legacy.GetValue('') -ne ('"' + $target + '\SubtitleEdit.exe" "%1"')) { throw 'Existing legacy selections can no longer open subtitles.' }
    Write-Host 'All four format names, shell commands, Open With migration and existing defaults verified.'
}
Install
Test-FileAssociations
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
Test-FileAssociations
if ((Get-FileHash (Join-Path $data 'Settings.json')).Hash -ne $existing) { throw 'Reinstallation replaced existing settings.' }
if (((Get-FileHash (Join-Path $data 'CloudMfa/config.json')).Hash + (Get-FileHash (Join-Path $data 'CloudMfa/token.dpapi')).Hash) -ne $cloud) { throw 'Reinstallation changed cloud alignment settings or token.' }
if (Test-Path (Join-Path $target 'CloudMfa')) { throw 'Cloud alignment data appeared in the program folder.' }
Write-Host "Installer install/reinstall, immutable settings, shortcut and registry checks passed."
Write-Host "Installer SHA256: $((Get-FileHash $setup).Hash)"

