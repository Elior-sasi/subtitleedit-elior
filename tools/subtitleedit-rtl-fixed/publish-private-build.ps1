$ErrorActionPreference = 'Stop'
function GhChecked([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub release operation failed: $($Arguments[0])" }
    return $output
}
if ($env:GITHUB_REPOSITORY -ne 'Elior-sasi/subtitleedit-elior' -or $env:GITHUB_REF -ne 'refs/heads/main') { throw 'Private custom delivery is restricted to the authorized repository and branch.' }
$repo = GhChecked -Arguments @('api', 'repos/Elior-sasi/subtitleedit-elior') | ConvertFrom-Json
if ($repo.private -ne $false) { throw 'This publication workflow requires the dedicated public repository.' }
$runId = [long]$env:GITHUB_RUN_ID
$attempt = [int]$env:GITHUB_RUN_ATTEMPT
$tag = "custom-build-$runId-$attempt"
$manifest = Get-Content './subtitleedit/publish/compact/custom-build.json' -Raw | ConvertFrom-Json
if ($manifest.commit -ne $env:GITHUB_SHA -or $manifest.run_id -ne $runId -or $manifest.channel -ne 'Elior-sasi/subtitleedit-elior/main' -or $manifest.upstream -notmatch '^v[0-9][0-9A-Za-z.\-]*$') { throw 'Delivery manifest does not match this tested custom build.' }
$version = $manifest.upstream.Substring(1)
$zip = (Resolve-Path "./subtitleedit/SE-$version-RTL-Hebrew-OST-AutoUpdate.zip").Path
$installer = (Resolve-Path "./custom/tools/subtitleedit-rtl-fixed/installer/output/SubtitleEdit-Elior-$version-Setup.exe").Path
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$installerHash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums = Join-Path $env:RUNNER_TEMP 'SHA256SUMS.txt'
@("$zipHash  $(Split-Path $zip -Leaf)", "$installerHash  $(Split-Path $installer -Leaf)") | Set-Content $checksums -Encoding ascii
$notes = Join-Path $env:RUNNER_TEMP 'custom-release-notes.md'
@("Custom Windows build based on $($manifest.upstream).", "", "Commit: $($manifest.commit)", "Run: $runId / attempt $attempt", "Channel: $($manifest.channel)", "", "Windows UI regressions, publish, package exclusions and installer install/reinstall checks passed before upload.", "Settings.json, CloudMfa credentials and user subtitle/project/media files are excluded and preserved by installation.", "", 'Changes:', ($manifest.changes | ForEach-Object { "- $_" }), "", "Update ZIP SHA-256: $zipHash", "Installer SHA-256: $installerHash") | Set-Content $notes -Encoding utf8
# Draft until the authenticated round-trip and manifest checks pass; never overwrite assets.
GhChecked -Arguments @('release', 'create', $tag, '--repo', $env:GITHUB_REPOSITORY, '--target', $env:GITHUB_SHA, '--draft', '--prerelease', '--latest=false', '--title', "Subtitle Edit Elior $($manifest.upstream) - build $runId", '--notes-file', $notes, $zip, $installer, $checksums) | Out-Null
$download = Join-Path $env:RUNNER_TEMP "release-verify-$runId-$attempt"
New-Item -ItemType Directory $download -Force | Out-Null
GhChecked -Arguments @('release', 'download', $tag, '--repo', $env:GITHUB_REPOSITORY, '--dir', $download) | Out-Null
foreach ($source in @($zip, $installer, $checksums)) {
    $copy = Join-Path $download (Split-Path $source -Leaf)
    if (-not (Test-Path $copy) -or (Get-FileHash $copy -Algorithm SHA256).Hash -ne (Get-FileHash $source -Algorithm SHA256).Hash) { throw 'Private release download failed SHA-256 verification.' }
}
$drafts = GhChecked -Arguments @('api', "repos/$($env:GITHUB_REPOSITORY)/releases?per_page=100") | ConvertFrom-Json
$matches = @($drafts | Where-Object { $_.tag_name -eq $tag })
if ($matches.Count -ne 1) { throw 'Cannot identify the uploaded draft release.' }
$release = $matches[0]
if ($release.target_commitish -ne $env:GITHUB_SHA -or -not $release.draft -or -not $release.prerelease) { throw 'Draft release identity mismatch.' }
foreach ($pair in @(@((Split-Path $zip -Leaf), $zipHash), @((Split-Path $installer -Leaf), $installerHash))) {
    $asset = @($release.assets | Where-Object name -eq $pair[0])
    if ($asset.Count -ne 1 -or $asset[0].digest -ne "sha256:$($pair[1])") { throw 'GitHub release asset digest mismatch.' }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $download (Split-Path $zip -Leaf)))
try {
    $entry = $archive.GetEntry('custom-build.json')
    if (-not $entry) { throw 'Downloaded release is missing its manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $readback = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($readback.commit -ne $env:GITHUB_SHA -or $readback.run_id -ne $runId -or $readback.channel -ne $manifest.channel -or $readback.upstream -ne $manifest.upstream) { throw 'Downloaded release manifest mismatch.' }
} finally { $archive.Dispose() }
GhChecked -Arguments @('release', 'edit', $tag, '--repo', $env:GITHUB_REPOSITORY, '--draft=false', '--latest=false') | Out-Null
$published = GhChecked -Arguments @('api', "repos/$($env:GITHUB_REPOSITORY)/releases/tags/$tag") | ConvertFrom-Json
if ($published.draft -or $published.target_commitish -ne $env:GITHUB_SHA) { throw 'Release publication was not verified.' }
Write-Host "Private release authenticated download, GitHub digests and manifest verified: $($published.html_url)"
Write-Host "Release update ZIP SHA256: $zipHash"
Write-Host "Release installer SHA256: $installerHash"
"Verified private download: $($published.html_url)" | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8

