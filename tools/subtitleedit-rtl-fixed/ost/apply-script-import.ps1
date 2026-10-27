$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
Copy-Item "$source/CcTiming.cs" 'src/ui/Features/Main/MainHelpers/CcTiming.cs' -Force
Copy-Item "$source/MainViewModel.CcTiming.cs" 'src/ui/Features/Main/MainViewModel.CcTiming.cs' -Force
Copy-Item "$source/CcTimingTests.cs" 'tests/UI/Features/Main/CcTimingTests.cs' -Force
Copy-Item "$source/ScriptImport.cs" 'src/ui/Features/Main/MainHelpers/ScriptImport.cs' -Force
Copy-Item "$source/MainViewModel.ScriptImport.cs" 'src/ui/Features/Main/MainViewModel.ScriptImport.cs' -Force
Copy-Item "$source/ImportPlainTextViewModel.CustomScript.cs" 'src/ui/Features/Files/ImportPlainText/ImportPlainTextViewModel.CustomScript.cs' -Force
Copy-Item "$source/CustomScriptEngineIntegrity.cs" 'src/ui/Features/Video/SpeechToText/CustomScriptEngineIntegrity.cs' -Force
Copy-Item "$source/ScriptImportTests.cs" 'tests/UI/Features/Main/ScriptImportTests.cs' -Force
Copy-Item "$source/ScriptTranscriptReview.cs" 'src/ui/Features/Main/MainHelpers/ScriptTranscriptReview.cs' -Force
Copy-Item "$source/MainViewModel.ScriptTranscriptReview.cs" 'src/ui/Features/Main/MainViewModel.ScriptTranscriptReview.cs' -Force
Copy-Item "$source/SpeechToTextViewModel.CustomScriptReview.cs" 'src/ui/Features/Video/SpeechToText/SpeechToTextViewModel.CustomScriptReview.cs' -Force
Copy-Item "$source/ScriptTranscriptReviewTests.cs" 'tests/UI/Features/Main/ScriptTranscriptReviewTests.cs' -Force
Copy-Item "$source/MainViewModel.CloudMfa.cs" 'src/ui/Features/Main/MainViewModel.CloudMfa.cs' -Force
Copy-Item "$source/CloudMfaTests.cs" 'tests/UI/Features/Main/CloudMfaTests.cs' -Force
function Exact([string]$text, [string]$needle, [string]$replacement) {
    if (([regex]::Matches($text, [regex]::Escape($needle))).Count -ne 1) { throw "Script import anchor changed: $needle" }
    return $text.Replace($needle, $replacement)
}
$speechPath = 'src/ui/Features/Video/SpeechToText/SpeechToTextViewModel.cs'
$speech = (Get-Content $speechPath -Raw).Replace("`r`n", "`n")
$speech = Exact $speech "    private void SaveSettings()`n    {" "    private void SaveSettings()`n    {`n        if (_customScriptReview) return; // isolated comparison dialog must not replace existing STT preferences"
Set-Content $speechPath $speech -Encoding utf8
$path = 'src/ui/Features/Files/ImportPlainText/ImportPlainTextViewModel.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$text = Exact $text "    private void MarkDirty()`n    {" "    private void MarkDirty()`n    {`n        CustomScriptInvalidate();"
$text = Exact $text '    private void Ok()' '    private async Task Ok()'
$needle = '        // Remember what was picked - reopening the dialog used to come back at "Auto" and the'
$text = Exact $text $needle @'
        await EnsurePreviewFlushedAsync();
        if (CustomScriptMode)
        {
            if (!CustomScriptCanImport)
            {
                await MessageBox.Show(Window!, "תסריט לכתוביות", "הפעל תחילה תזמון מול השמע ובדוק את התוצאה. לאחר שינוי טקסט או חלוקה נדרש תזמון מחדש.", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            OkPressed = true;
            Close();
            return;
        }
        // Remember what was picked - reopening the dialog used to come back at "Auto" and the
'@
$text = Exact $text '            Window, viewModel => viewModel.Initialize());' '            Window, viewModel => { viewModel.CustomScriptSetup = CustomScriptMode; viewModel.Initialize(); });'
$text = Exact $text '            RefreshSubtitlesFrom(lines);' "            RefreshSubtitlesFrom(lines);`n            CustomScriptRecordAlignment();"
$text = Exact $text '        await RunForcedAlignerAsync(setupVm.ExecutablePath, setupVm.AlignerModelPath, setupVm.EndsFromIsolatedSpeech);' @'
        if (CustomScriptMode && NeedsHebrewAlignerWarning(PlainText, Se.Settings.Tools.AudioToText.CrispAsrForcedAligner)
            && await MessageBox.Show(Window!, "תסריט לכתוביות", HebrewAlignerWarning, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        CustomScriptInvalidate();
        await RunForcedAlignerAsync(setupVm.ExecutablePath, setupVm.AlignerModelPath, setupVm.EndsFromIsolatedSpeech);
'@
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Files/ImportPlainText/ImportPlainTextWindow.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$text = Exact $text '        buttonOk.Classes.Add("primary");' @'
        if (vm.CustomScriptMode)
        {
            Title = "תסריט לכתוביות — הפעל תזמון, בדוק ואשר ייבוא";
            buttonAlignViaWhisper.IsVisible = false;
            buttonAlignViaForcedAligner.Content = "תזמן מול השמע / התקן מנוע…";
            buttonOk.Content = "בדקתי — ייבא למסמך חדש";
        }
        buttonOk.Classes.Add("primary");
'@
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Files/ImportPlainText/ForcedAlignerSetup/ForcedAlignerSetupViewModel.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$text = Exact $text '    public bool OkPressed { get; private set; }' "    public bool OkPressed { get; private set; }`n    internal bool CustomScriptSetup { get; set; }"
$needle = '        var configured = ForcedAlignerOption.FromChoice(Se.Settings.Tools.AudioToText.CrispAsrForcedAligner);'
$text = Exact $text $needle ($needle + "`n        if (CustomScriptSetup && configured.IsBuiltIn) configured = ForcedAlignerOption.Wav2Vec2(ForcedAlignerOption.Wav2Vec2EnChoice);")
$text = Exact $text '                    viewModel.Engine = _engine;' @'
                    viewModel.Engine = _engine;
                    if (CustomScriptSetup && OperatingSystem.IsWindows())
                    {
                        viewModel.CrispAsrWindowsVariant = "cpu";
                        viewModel.CustomScriptVerifyCpuDownload = true;
                    }
'@
# System is not imported by upstream.
$text = "using System;`n" + $text
Set-Content $path $text -Encoding utf8
$path = 'src/ui/Features/Video/SpeechToText/DownloadSpeechToTextEngineViewModel.cs'
$text = (Get-Content $path -Raw).Replace("`r`n", "`n")
$text = Exact $text '                    WriteCrispAsrInstalledHash(folder);' @'
                    if (CustomScriptVerifyCpuDownload)
                    {
                        if (CrispAsrWindowsVariant != "cpu") throw new InvalidDataException("Unexpected script timing engine variant.");
                        VerifyCustomScriptEngine(_downloadStream);
                    }
                    WriteCrispAsrInstalledHash(folder);
'@
Set-Content $path $text -Encoding utf8


