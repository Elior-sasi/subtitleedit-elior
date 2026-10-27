using System;
using System.Linq;
using Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines;

namespace Nikse.SubtitleEdit.Features.Video.SpeechToText;

public partial class SpeechToTextViewModel
{
    private bool _customScriptReview;
    internal void InitializeCustomScriptReview(string media)
    {
        _customScriptReview = true;
        // Only the existing local Whisper CPP family is reachable in this particular dialog.
        // Keep the normal speech-to-text window and its saved engine options unchanged elsewhere.
        var local = Engines.OfType<WhisperCppEngine>().FirstOrDefault()
            ?? throw new InvalidOperationException("מנוע Whisper מקומי אינו זמין.");
        Engines.Clear(); Engines.Add(local); SelectedEngine = local; EngineChanged();
        Initialize(media, -1);
        IsBatchMode = false; IsBatchModeVisible = false; IsSingleModeVisible = false;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == "he" || l.Code == "heb") ?? SelectedLanguage;
        DoTranslateToEnglish = false;
        DoPostProcessing = false;
        DoDetectSpeakers = false;
    }
}

