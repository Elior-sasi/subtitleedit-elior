using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal sealed record LocalNameAnchor(string Text, bool GuideOnly);
internal sealed record LocalNameHit(Guid RowId, int Number, string Observed, string[] Similar, bool HasGuideAnchors)
{
    internal string Message => $"? כתיב שונה — בדיקה מקומית: „{Observed}” לעומת {string.Join(" / ", Similar.Take(5).Select(s => "„" + s + "”"))}. " +
        (Similar.Length > 5 ? " ועוד אפשרויות. " : " ") + (HasGuideAnchors ? "מקור: מילון ו/או שדות מדריך מפורשים; שדות מדריך אינם אישור מילוני. " : "מקור: מילון מאושר. ") + "הכתיב שונה; אין קביעה שזה אותו אדם או שזו טעות. לא בוצע תיקון.";
}
internal sealed record LocalNameScan(IReadOnlyList<LocalNameHit> Hits, int Anchors, bool Limited, IReadOnlyList<LocalNameAnchor>? KnownNames = null);

// Offline spelling suggestions, not entity recognition or semantic approval. Nothing is
// rewritten or persisted. A deletion index avoids comparing every word with every name.
internal static class LocalNameSuggestions
{
    private static readonly Regex Words = new(@"[א-ת](?:[א-ת\u0591-\u05BD\u05BF-\u05C2\u05C4-\u05C5\u05C7]|['׳](?=[א-ת]))*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> Common = new("אנחנו אתם אתן אותם אותן הוא היא הם הן אני אתה שאת זאת הזה הזהו אלה אילו איזה איזו יהיה היתה הייתה להיות הייתי היינו הייתם הייתה שלהם שלהן שלנו שלכם שלכן שלי שלך שלו שלה אבל בגלל בשביל כאשר עכשיו יותר פחות מאוד תמיד בכלל משהו מישהו כולם הכל אחרי לפני תודה בבקשה בסדר נכון רוצה יכול צריך אולי באמת כלום למה איך איפה אשר אלא האם הנה כבר עדיין שוב עוד אשר גם רק כמו אין יש לא כן על אל את עם של כל אם או כי זה זו".Split(' '));
    internal static string Normalize(string text) => string.Join(" ", Words.Matches(text).Select(m => new string(m.Value.Normalize(NormalizationForm.FormD).Replace('׳', '\'').Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())));

    internal static LocalNameAnchor[] Anchors(IReadOnlyList<QcTerm> approved, string guide)
    {
        if (approved.Count > 500 || guide.Length > 200_000) throw new InvalidDataException("יותר מדי חומר לאיתור מקומי. הסריקה מוגבלת ל־500 מונחים ול־200,000 תווי מדריך.");
        var names = approved.Select(t => new LocalNameAnchor(t.Preferred, false)).ToList();
        // Only explicit name fields or an unambiguous Hebrew/Latin pair count as guide
        // anchors. Ordinary prose is never mined into an automatically approved dictionary.
        foreach (var line in guide.Replace("\r", "").Split('\n'))
        {
            var field = Regex.Match(line.Trim(), @"^(?:שם|שם דמות|שם בעברית|Name)\s*:\s*([א-ת'׳\s]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (field.Success) names.Add(new(field.Groups[1].Value.Trim(), true));
            else
            {
                var pair = Regex.Split(line.Trim(), @"\s*=\s*|\t+|\s+[–—]\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (pair.Length != 2) continue;
                foreach (var (he, latin) in new[] { (pair[0], pair[1]), (pair[1], pair[0]) })
                    if (Regex.IsMatch(he, @"^[א-ת'׳ ]+$") && Regex.IsMatch(latin, @"^[A-Za-z][A-Za-z .'-]{1,60}$")) names.Add(new(he.Trim(), true));
            }
            if (names.Count > 1000) throw new InvalidDataException("יותר מדי שדות שמות במדריך לאיתור מקומי.");
        }
        return names.Where(n => n.Text.Length <= 64 && Regex.IsMatch(n.Text, @"^[א-ת'׳\s\u0591-\u05C7]+$") &&
                Normalize(n.Text).Length >= 2)
            .GroupBy(n => Normalize(n.Text)).Select(g => g.OrderBy(n => n.GuideOnly).First()).Take(501).ToArray();
    }

    internal static bool OneEdit(string a, string b)
    {
        if (a == b || Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.Length == b.Length)
        {
            var positions = Enumerable.Range(0, a.Length).Where(position => a[position] != b[position]).Take(3).ToArray();
            return positions.Length == 1 || positions.Length == 2 && positions[1] == positions[0] + 1 && a[positions[0]] == b[positions[1]] && a[positions[1]] == b[positions[0]];
        }
        if (a.Length > b.Length) (a, b) = (b, a);
        var i = 0; var j = 0; var skipped = false;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; }
            else { if (skipped) return false; skipped = true; j++; }
        }
        return true;
    }
    private static IEnumerable<string> Keys(string value)
    {
        yield return value;
        for (var i = 0; i < value.Length; i++) yield return value.Remove(i, 1);
    }

    internal static LocalNameScan Scan(IReadOnlyList<QcRow> rows, IReadOnlyList<QcTerm> approved, string guide, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (rows.Count > 10_000 || rows.Sum(r => (long)(r.Text?.Length ?? 0)) > 1_000_000) throw new InvalidDataException("הקובץ גדול מדי לאיתור מקומי (עד 10,000 כתוביות ומיליון תווים). לא הוצגו תוצאות חלקיות.");
        var anchors = Anchors(approved, guide);
        if (anchors.Length > 500) throw new InvalidDataException("נמצאו יותר מ־500 שמות ומונחים לאיתור מקומי.");
        var known = anchors.ToDictionary(a => Normalize(a.Text));
        var exact = approved.Select(t => Normalize(t.Preferred)).Concat(approved.SelectMany(t => t.Variants ?? Array.Empty<string>()).Select(Normalize)).Where(s => s.Length > 0).ToHashSet();
        exact.UnionWith(known.Keys);
        var index = new Dictionary<string, HashSet<string>>();
        foreach (var name in known.Keys.Where(n => n.Length >= 4 && n.Split(' ').Length <= 3 && !Common.Contains(n)))
            foreach (var key in Keys(name))
            { if (!index.TryGetValue(key, out var set)) index[key] = set = new(); set.Add(name); }
        if (index.Count == 0) return new(Array.Empty<LocalNameHit>(), anchors.Length, false, anchors);
        var hits = new List<LocalNameHit>(); var examined = 0; var comparisons = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var visible = CustomQc.Visible(row.Text); var words = Words.Matches(visible).ToArray(); var seen = new HashSet<string>();
            for (var start = 0; start < words.Length; start++)
                for (var count = 1; count <= 3 && start + count <= words.Length; count++)
                {
                    token.ThrowIfCancellationRequested();
                    if (++examined > 100_000) throw new InvalidDataException("יותר מדי צירופי מילים לאיתור מקומי. לא הוצגו תוצאות חלקיות.");
                    var last = words[start + count - 1];
                    if (count > 1)
                    {
                        var previous = words[start + count - 2];
                        if (!Regex.IsMatch(visible[(previous.Index + previous.Length)..last.Index], @"^[\s\u200E\u200F]*$")) break;
                    }
                    var observed = visible[words[start].Index..(last.Index + last.Length)]; var value = Normalize(observed);
                    if (value.Length is < 3 or > 64 || count == 1 && Common.Contains(value) || exact.Contains(value) || value.StartsWith('ו') && exact.Contains(value[1..])) continue;
                    var forms = value.StartsWith('ו') && value.Length > 4 ? new[] { value, value[1..] } : new[] { value };
                    var suggestions = new HashSet<string>();
                    foreach (var form in forms)
                        foreach (var key in Keys(form))
                            if (index.TryGetValue(key, out var possible))
                                foreach (var name in possible)
                                {
                                    if (++comparisons > 1_000_000) throw new InvalidDataException("מגבלת ההשוואות לאיתור מקומי הושגה. לא הוצגו תוצאות חלקיות.");
                                    token.ThrowIfCancellationRequested();
                                    if (name.Count(c => c == ' ') == count - 1 && OneEdit(form, name)) suggestions.Add(name);
                                }
                    if (suggestions.Count == 0 || !seen.Add(value)) continue;
                    var matches = suggestions.OrderBy(s => s, StringComparer.Ordinal).ToArray();
                    hits.Add(new(row.Id, row.Number, observed, matches.Select(n => known[n].Text).ToArray(), matches.Any(n => known[n].GuideOnly)));
                    if (hits.Count == 500) return new(hits, anchors.Length, true, anchors);
                }
        }
        return new(hits, anchors.Length, false, anchors);
    }
}
