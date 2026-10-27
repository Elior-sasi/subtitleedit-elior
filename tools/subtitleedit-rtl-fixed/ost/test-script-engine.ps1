$ErrorActionPreference = 'Stop'
$folder = Join-Path $env:RUNNER_TEMP ('ScriptEngine-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $folder | Out-Null
try {
    $zip = Join-Path $folder 'engine.zip'
    Invoke-WebRequest 'https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.43/crispasr-windows-x86_64-cpu.zip' -OutFile $zip
    $expected = '997bc39f541e5d5d0bd14144159c038bd95d6ab8b523718c34a54e03ab404f47'
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Official script engine digest mismatch.' }
    Expand-Archive $zip (Join-Path $folder 'engine')
    $exe = Get-ChildItem (Join-Path $folder 'engine') -Recurse -Filter 'crispasr.exe' -File | Select-Object -First 1
    if (-not $exe) { throw 'Official script engine executable missing.' }
    $info = [Diagnostics.ProcessStartInfo]::new($exe.FullName)
    $info.UseShellExecute = $false; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.CreateNoWindow = $true
    $info.ArgumentList.Add('--help')
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(45000)) { $process.Kill($true); throw 'Script engine help smoke timed out.' }
    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or $output -notmatch 'align-only') { throw "Script engine does not expose align-only: $output" }
    Write-Host 'Official CrispASR CPU SHA-256 and real align-only CLI help smoke passed.'
} finally { Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue }

