$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$root = Join-Path $env:RUNNER_TEMP ('mfa-local-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root -Force | Out-Null
$wav = Join-Path $root 'sample.wav'
$voice = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $voice.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::NotSet, [System.Speech.Synthesis.VoiceAge]::NotSet, 0, [Globalization.CultureInfo]::GetCultureInfo('en-US'))
    $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $voice.SetOutputToWaveFile($wav, $format)
    $voice.Speak('Hello world. This is a test of local speech alignment. Please check every subtitle before saving the file.')
} finally { $voice.Dispose() }
$request = Join-Path $root 'request.json'
@{ cues = @(@{id=1; text='Hello world.'}, @{id=2; text='This is a test of local speech alignment.'}, @{id=3; text='Please check every subtitle before saving the file.'}) } | ConvertTo-Json -Depth 5 | Set-Content $request -Encoding utf8
$python = Join-Path $env:CONDA 'envs/subtitleedit-mfa/python.exe'
if (-not (Test-Path $python)) { throw 'MFA conda Python was not installed.' }
$runner = Join-Path $PSScriptRoot 'local-mfa/run_local_mfa.py'
$cache = Join-Path $root 'models'
try {
    & $python -I -u $runner --cache $cache --check
    if ($LASTEXITCODE -ne 0) { throw 'Real MFA CLI help failed.' }
    & $python -I -u $runner --cache $cache --download-models
    if ($LASTEXITCODE -ne 0) { throw 'Official English MFA model download failed.' }
    $inputHash = (Get-FileHash $request -Algorithm SHA256).Hash.ToLowerInvariant()
    $audioHash = (Get-FileHash $wav -Algorithm SHA256).Hash.ToLowerInvariant()
    & $python -I -u $runner --cache $cache --request $request --audio $wav --work $root
    if ($LASTEXITCODE -ne 0) { throw 'Real local MFA alignment failed.' }
    $result = Get-Content (Join-Path $root 'result.json') -Raw | ConvertFrom-Json
    if ($result.status -notin @('complete', 'needs_review') -or $result.cues.Count -ne 3) { throw 'Local MFA returned missing or partial cues.' }
    if ($result.input.audio_sha256 -ne $audioHash -or $result.input.cues_sha256 -ne $inputHash) { throw 'Local MFA input hashes differ.' }
    $previousEnd = 0.0
    foreach ($cue in $result.cues) {
        if ($cue.start -lt $previousEnd -or $cue.end -le $cue.start -or $cue.end -gt $result.input.audio_duration_s + 0.05) { throw 'Real MFA timing is invalid.' }
        $previousEnd = $cue.end
    }
    if ((Get-FileHash $request -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputHash) { throw 'The original request was changed.' }
    Write-Host 'Real Windows MFA 3.4.3 CLI, official English model download and synthetic English speech alignment passed. This is not human-audio quality validation.'
} finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
