namespace Nikse.SubtitleEdit.Controls.AudioVisualizerControl;

internal static class WaveformRtlMarker
{
    // Detect actual controls, not Hebrew/Arabic letters or the interface direction.
    // U+202B is what FixRtlViaUnicodeChars inserts, including in older saved files.
    internal static bool HasRtlControls(string? text)
    {
        if (text == null) return false;
        foreach (var character in text)
            if (character is '\u200F' or '\u202B' or '\u202E' or '\u2067' or '\u061C')
                return true;
        return false;
    }
}
