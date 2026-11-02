using System.Text;
using System.Text.RegularExpressions;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal static class HebrewNiqqud
{
    private static readonly Regex Tags = new(@"<(?:[^<>""']|""[^""]*""|'[^']*')+>|\{\\[^}]*\}");
    // Vowels, dagesh/mapiq, meteg, rafe, shin/sin dots and qamats qatan.
    // Cantillation, punctuation (maqaf/paseq/sof pasuq), letters and bidi marks remain.
    internal static bool IsPoint(char c) => c is >= '\u05B0' and <= '\u05BD' or '\u05BF' or '\u05C1' or '\u05C2' or '\u05C7';
    internal static string Remove(string text)
    {
        var output = new StringBuilder(text.Length); var protectedTags = Tags.Matches(text); var tag = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (tag < protectedTags.Count && i == protectedTags[tag].Index)
            { var match = protectedTags[tag++]; output.Append(match.Value); i += match.Length - 1; }
            else if (!IsPoint(text[i])) output.Append(text[i]);
        }
        return output.ToString();
    }
    internal static int Count(string text) => text.Length - Remove(text).Length;
}
