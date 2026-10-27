using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Nikse.SubtitleEdit.Core.Common;

public static class SubtitleLineSpacing
{
    // Protect HTML attributes (including quoted '>') and ASS overrides byte-for-byte.
    private static readonly Regex Tags = new(@"<(?:[^<>""']|""[^""]*""|'[^']*')+>|\{\\[^}]*\}");

    public static string Clean(string text, string language) => Normalize(text, language, false, true);
    public static string ToSingleLine(string text) => Normalize(text, string.Empty, true, false);

    public static IEnumerable<int> BreakPositions(string text)
    {
        var tags = Tags.Matches(text);
        var tag = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (tag < tags.Count && i == tags[tag].Index)
            {
                i += tags[tag++].Length - 1;
                continue;
            }
            if (text[i] == ' ') yield return i;
        }
    }

    private static string Normalize(string text, string language, bool mergeLines, bool cleanPunctuation)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (mergeLines)
            text = Regex.Replace(text, @"</?br\s*/?>", " ", RegexOptions.IgnoreCase);

        var marker = "\uE000";
        while (text.Contains(marker, StringComparison.Ordinal)) marker += "\uE000";
        var tags = new List<string>();
        text = Tags.Replace(text, match =>
        {
            var index = tags.Count;
            tags.Add(match.Value);
            return marker + index + marker;
        });
        var token = Regex.Escape(marker) + @"\d+" + Regex.Escape(marker);
        if (mergeLines) text = Regex.Replace(text, @"\r\n?|\n", " ");
        text = Regex.Replace(text, @"[ \t]+", " ");
        // Tags are invisible when deciding whether a space is at a line edge.
        text = Regex.Replace(text, @"(?m)^(?:" + token + @"| )+", m => m.Value.Replace(" ", string.Empty));
        text = Regex.Replace(text, @"(?m)(?:" + token + @"| )+(?=\r?$)", m => m.Value.Replace(" ", string.Empty));
        if (cleanPunctuation)
        {
            var french = language.Equals("fr", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("fr-", StringComparison.OrdinalIgnoreCase);
            var punctuation = french ? @"[,.)\]…]" : @"[,.;:!?)\]…]";
            text = Regex.Replace(text, @" +(?=(?:" + token + @"| )*" + punctuation + @")", string.Empty);
        }
        return Regex.Replace(text, "(" + token + ")", match =>
        {
            var index = int.Parse(match.Value.Substring(marker.Length, match.Value.Length - marker.Length * 2), System.Globalization.CultureInfo.InvariantCulture);
            return tags[index];
        });
    }
}

