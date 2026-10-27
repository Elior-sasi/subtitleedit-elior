$ErrorActionPreference = 'Stop'

$utilitiesPath = 'src/libse/Common/Utilities.cs'
$breakerPath = '..\custom\tools\subtitleedit-rtl-fixed\HebrewLineBreaker.cs'
$targetBreaker = 'src/libse/Common/HebrewLineBreaker.cs'

if (-not (Test-Path $breakerPath)) { throw "Missing $breakerPath" }
Copy-Item $breakerPath $targetBreaker -Force
Copy-Item '..\custom\tools\subtitleedit-rtl-fixed\SubtitleLineSpacing.cs' 'src/libse/Common/SubtitleLineSpacing.cs' -Force

$source = Get-Content $utilitiesPath -Raw
$needle = @"
        public static string AutoBreakLine(string text, int maximumLength, int mergeLinesShorterThan, string language)
        {
            if (Configuration.Settings.General.MaxNumberOfLines <= 2)
"@
$replacement = @"
        public static string AutoBreakLine(string text, int maximumLength, int mergeLinesShorterThan, string language)
        {
            var hebrewBreak = HebrewLineBreaker.TryBreak(text, maximumLength, mergeLinesShorterThan, language, Configuration.Settings.General.MaxNumberOfLines);
            if (hebrewBreak != null)
            {
                return hebrewBreak;
            }

            if (Configuration.Settings.General.MaxNumberOfLines <= 2)
"@

$count = ([regex]::Matches($source, [regex]::Escape($needle))).Count
if ($count -ne 1) { throw "Could not patch AutoBreakLine safely; found $count targets" }
$source = $source.Replace($needle, $replacement)
Set-Content -Path $utilitiesPath -Value $source -Encoding utf8
Write-Host 'Hebrew line-break rules applied.'

