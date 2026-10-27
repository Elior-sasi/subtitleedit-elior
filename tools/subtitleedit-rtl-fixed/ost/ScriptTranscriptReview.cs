using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

// Text similarity is only a review aid. All times are copied from actual transcript cues;
// no duration interpolation, semantic matching, or automatic acceptance happens here.
internal static class ScriptTranscriptReview
{
    internal sealed record Cue(double Start, double End, string Text);
    internal sealed record Suggestion(int First, int Last, double Similarity, bool Ambiguous, bool SearchLimited);
    internal enum TextChoice { Script, Transcript }
    internal sealed record Decision(int Row, int First, int Last, TextChoice Choice, bool Skip = false);
    internal sealed record Difference(string[] ScriptOnly, string[] TranscriptOnly);
    internal const int MaxCues = 5000;
    private const int MaxWords = 160;
    private static readonly Regex Tokens = new(@"[\p{L}\p{N}]+", RegexOptions.None, TimeSpan.FromSeconds(2));

    internal static string[] PrepareRows(string text)
    {
        if (text.Length > ScriptImport.MaxText) throw new InvalidDataException("התסריט ארוך מדי.");
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rows = text.Contains("\n\n") ? Regex.Split(text, @"\n[ \t]*\n", RegexOptions.None, TimeSpan.FromSeconds(2)) : text.Split('\n');
        rows = rows.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        if (rows.Length == 0 || rows.Length > MaxCues) throw new InvalidDataException("הכן 1–5000 קטעי תסריט. שורה ריקה מפרידה בין קטעים מרובי שורות.");
        return rows;
    }

    internal static List<Cue> ReadSrt(string text)
    {
        if (text.Length > ScriptImport.MaxText) throw new InvalidDataException("התמלול ארוך מדי. פצל אותו.");
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim().TrimStart('\uFEFF'), @"\n[ \t]*\n", RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            var lines = block.Split('\n');
            var at = lines.Length > 0 && Regex.IsMatch(lines[0].Trim(), @"^\d+$") ? 1 : 0;
            if (lines.Length <= at + 1) throw new InvalidDataException("תמלול SRT אינו תקין.");
            var m = Regex.Match(lines[at], @"^(\d{1,4}):([0-5]\d):([0-5]\d)[,.](\d{3})\s*-->\s*(\d{1,4}):([0-5]\d):([0-5]\d)[,.](\d{3})(?:[ \t].*)?$", RegexOptions.None, TimeSpan.FromSeconds(2));
            if (!m.Success) throw new InvalidDataException("תמלול SRT כולל שורת זמן שגויה.");
            double Ms(int i) => int.Parse(m.Groups[i].Value) * 3600000.0 + int.Parse(m.Groups[i + 1].Value) * 60000.0 + int.Parse(m.Groups[i + 2].Value) * 1000.0 + int.Parse(m.Groups[i + 3].Value);
            cues.Add(new Cue(Ms(1), Ms(5), string.Join("\n", lines.Skip(at + 1))));
            if (cues.Count > MaxCues) throw new InvalidDataException("פצל את התמלול לעד 5000 כתוביות.");
        }
        Validate(cues); return cues;
    }

    internal static string[] Words(string text)
    {
        if (text.Length > 12000) throw new InvalidDataException("קטע ארוך מדי להשוואה. פצל אותו.");
        var plain = Regex.Replace(text, @"<[^>]*>|\{\\[^}]*\}", "", RegexOptions.None, TimeSpan.FromSeconds(2));
        var normalized = new StringBuilder();
        foreach (var c in plain.Normalize(NormalizationForm.FormD))
        {
            var category = char.GetUnicodeCategory(c);
            if (category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.Format) continue;
            normalized.Append(char.ToLowerInvariant(c));
        }
        return Tokens.Matches(normalized.ToString()).Select(m => m.Value).ToArray();
    }

    internal static void Validate(IReadOnlyList<Cue> transcript)
    {
        if (transcript.Count == 0 || transcript.Count > MaxCues) throw new InvalidDataException("בחר תמלול מתוזמן עם 1–5000 כתוביות.");
        double previous = -1;
        long total = 0;
        foreach (var c in transcript)
        {
            total += c.Text?.Length ?? 0;
            if (total > ScriptImport.MaxText) throw new InvalidDataException("התמלול ארוך מדי. פצל אותו.");
            if (!double.IsFinite(c.Start) || !double.IsFinite(c.End) || c.Start < 0 || c.End <= c.Start || c.End > TimeSpan.MaxValue.TotalMilliseconds / 2 || c.Start < previous || string.IsNullOrWhiteSpace(c.Text))
                throw new InvalidDataException("התמלול כולל טקסט ריק, זמנים שגויים או סדר זמנים לא תקין.");
            if (Words(c.Text).Length > MaxWords) throw new InvalidDataException("כתובית בתמלול ארוכה מדי. פצל אותה לפני ההשוואה.");
            previous = c.Start;
        }
    }

    internal static Suggestion?[] Compare(IReadOnlyList<string> script, IReadOnlyList<Cue> transcript, CancellationToken cancel = default)
    {
        Validate(transcript);
        if (script.Count == 0 || script.Count > MaxCues) throw new InvalidDataException("פצל את התסריט לעד 5000 כתוביות להשוואה.");
        var spoken = transcript.Select(c => Words(c.Text)).ToArray();
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < spoken.Length; i++)
            foreach (var word in spoken[i].Distinct())
            {
                if (!index.TryGetValue(word, out var list)) index[word] = list = new List<int>();
                list.Add(i);
            }
        var result = new Suggestion?[script.Count];
        long work = 0;
        for (var row = 0; row < script.Count; row++)
        {
            cancel.ThrowIfCancellationRequested();
            var target = Words(script[row]);
            if (target.Length == 0 || target.Length > MaxWords) continue;
            // Rare literal words bound the search, without treating omitted Hebrew prefixes as equivalent.
            var postings = target.Distinct().Where(index.ContainsKey).Select(w => index[w]).OrderBy(l => l.Count).ToList();
            var starts = new SortedSet<int>(); var limited = false;
            foreach (var list in postings.Take(6))
            {
                if (list.Count > 100) { limited = true; continue; }
                foreach (var at in list)
                    for (var back = 0; back < 4; back++) if (at - back >= 0) starts.Add(at - back);
            }
            if (starts.Count > 512) { starts = new SortedSet<int>(starts.Take(512)); limited = true; }
            var candidates = new List<Suggestion>();
            foreach (var first in starts)
            {
                cancel.ThrowIfCancellationRequested();
                var words = new List<string>();
                for (var last = first; last < Math.Min(transcript.Count, first + 4); last++)
                {
                    words.AddRange(spoken[last]);
                    if (words.Count > MaxWords || transcript[last].End - transcript[first].Start > 120000 || (last > first && transcript[last].Start - transcript[last - 1].End > 3000)) break;
                    work += (long)target.Length * words.Count;
                    if (work > 60000000) throw new InvalidDataException("ההשוואה גדולה או חוזרת מדי. פצל למקטעים קטנים יותר.");
                    var score = Similarity(target, words.ToArray());
                    // Short common utterances require an exact word sequence, still reviewed manually.
                    if ((target.Length <= 2 && score < 1) || score < 0.55) continue;
                    candidates.Add(new Suggestion(first, last, score, false, limited));
                }
            }
            var ordered = candidates.OrderByDescending(c => c.Similarity).ThenBy(c => c.Last - c.First).ThenBy(c => c.First).ToList();
            if (ordered.Count == 0) continue;
            var best = ordered[0];
            // Competing boundaries, even overlapping ones, also need manual review.
            var ambiguous = ordered.Skip(1).Any(c => c.Similarity >= best.Similarity - 0.08 && (c.First != best.First || c.Last != best.Last));
            result[row] = best with { Ambiguous = ambiguous || limited };
        }
        return result;
    }

    private static int[,] Lcs(string[] a, string[] b)
    {
        var cells = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                cells[i, j] = a[i] == b[j] ? 1 + cells[i + 1, j + 1] : Math.Max(cells[i + 1, j], cells[i, j + 1]);
        return cells;
    }

    private static double Similarity(string[] a, string[] b) => a.Length + b.Length == 0 ? 0 : 2.0 * Lcs(a, b)[0, 0] / (a.Length + b.Length);

    internal static Difference Diff(string script, string transcript)
    {
        var a = Words(script); var b = Words(transcript);
        if (a.Length > MaxWords || b.Length > MaxWords) throw new InvalidDataException("קטע ארוך מדי להצגת הבדלים.");
        var cells = Lcs(a, b); var removed = new List<string>(); var added = new List<string>();
        var i = 0; var j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j]) { i++; j++; }
            else if (i < a.Length && (j == b.Length || cells[i + 1, j] >= cells[i, j + 1])) removed.Add(a[i++]);
            else added.Add(b[j++]);
        }
        return new Difference(removed.ToArray(), added.ToArray());
    }

    internal static List<Cue> Build(IReadOnlyList<string> script, IReadOnlyList<Cue> transcript, IReadOnlyList<Decision> decisions)
    {
        Validate(transcript);
        if (decisions.Count != script.Count || decisions.Select(d => d.Row).Distinct().Count() != script.Count || decisions.Any(d => d.Row < 0 || d.Row >= script.Count))
            throw new InvalidDataException("יש לאשר בחירה או דילוג מפורש לכל שורת תסריט.");
        var output = new List<Cue>(); var used = new HashSet<int>();
        foreach (var d in decisions.OrderBy(d => d.Row))
        {
            if (d.Skip) continue;
            if (!Enum.IsDefined(d.Choice) || d.First < 0 || d.Last < d.First || d.Last >= transcript.Count || d.Last - d.First >= 12)
                throw new InvalidDataException("בחר טווח תמלול תקין של עד 12 כתוביות.");
            var first = transcript[d.First]; var last = transcript[d.Last];
            if (last.End - first.Start > 120000 || output.Any(c => c.End > first.Start))
                throw new InvalidDataException("הבחירות יוצרות חפיפה, סדר הפוך או קטע ארוך משתי דקות. תקן את הטווחים.");
            for (var n = d.First; n <= d.Last; n++)
            {
                if (!used.Add(n)) throw new InvalidDataException("אותה שורת תמלול נבחרה יותר מפעם אחת.");
                if (transcript[n].End > last.End) throw new InvalidDataException("טווח התמלול כולל זמנים חופפים לא תקינים.");
                if (n > d.First && transcript[n].Start - transcript[n - 1].End > 3000) throw new InvalidDataException("טווח התמלול כולל הפסקה ארוכה; בחר קטע רציף קצר יותר.");
            }
            var text = d.Choice == TextChoice.Script ? script[d.Row] : string.Join("\n", transcript.Skip(d.First).Take(d.Last - d.First + 1).Select(c => c.Text));
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("הטקסט הנבחר ריק.");
            output.Add(new Cue(first.Start, last.End, text));
        }
        if (output.Count == 0) throw new InvalidDataException("לא אושרה אף כתובית לייבוא.");
        return output;
    }
}

