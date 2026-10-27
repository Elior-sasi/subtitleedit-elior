using System;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Tools.AiReview;

internal static class CustomHebrewAiReview
{
    internal const string ModelFileName = "Qwen_Qwen3.5-4B-Q4_K_M.gguf";
    internal const string Prompt =
        "You are a careful subtitle proofreader. Review actual spelling, grammar and punctuation errors in {language}. " +
        "For Hebrew, keep natural modern Hebrew and check agreement and punctuation in context. " +
        "Do not translate, paraphrase, soften, censor or change the meaning, tone, jokes or speaker attribution. " +
        "Preserve names, transliterations, terminology, slang and intentional dialect. Leave uncertain wording unchanged. " +
        "Preserve all formatting tags and existing line breaks exactly. Suggest only clear corrections, with concise reasons in Hebrew for Hebrew subtitles.";

    // Apply only when the local review engine has not been configured yet.
    // Download/start remains in the existing Review flow on the user's machine.
    internal static bool ApplyDefaults(SeAiReview settings)
    {
        if (settings.Engine != SeAiReview.EngineLlamaCpp ||
            !string.IsNullOrWhiteSpace(settings.LlamaCppModelFileName))
            return false;

        settings.LlamaCppModelFileName = ModelFileName;
        if (string.IsNullOrWhiteSpace(settings.Prompt) ||
            string.Equals(settings.Prompt, SeAiReview.DefaultPrompt, StringComparison.Ordinal))
            settings.Prompt = Prompt;
        return true;
    }
}

