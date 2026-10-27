using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal sealed record QcIssue(Guid RowId, int Number, string Kind, string Message);
internal sealed record QcRow(Guid Id, int Number, string Text, TimeSpan Start, TimeSpan End, double Cps);
internal sealed record QcLimits(int LineLength, int Lines, double Cps, int MinimumMs, int MaximumMs, double GapMs);
internal sealed record QcTerm(string Preferred, string[] Variants);

internal static class CustomQc
{
    internal static readonly IReadOnlyDictionary<char, string> InvisibleCharacters = new Dictionary<char, string>
    {
        ['\u200B'] = "רווח ברוחב אפס", ['\uFEFF'] = "BOM / רווח נסתר", ['\u00A0'] = "רווח קשיח",
        ['\u200E'] = "סימן כיוון LTR", ['\u200F'] = "סימן כיוון RTL",
        ['\u202A'] = "הטמעת LTR", ['\u202B'] = "הטמעת RTL", ['\u202C'] = "סיום הטמעת כיוון",
        ['\u202D'] = "כפיית LTR", ['\u202E'] = "כפיית RTL",
        ['\u2066'] = "בידוד LTR", ['\u2067'] = "בידוד RTL", ['\u2068'] = "בידוד כיוון", ['\u2069'] = "סיום בידוד כיוון",
    };
    private static readonly Regex Formatting = new(@"<(?:[^<>""']|""[^""]*""|'[^']*')+>|\{\\[^}]*\}");
    internal static string Visible(string text) => HtmlUtil.RemoveHtmlTags(Formatting.Replace(text ?? string.Empty, string.Empty), true);

    internal static bool TryParseTerms(string text, out List<QcTerm> terms, out string error)
    {
        terms = new(); error = string.Empty;
        var lineNumber = 0;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var pair = line.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
            { error = $"שורה {lineNumber}: כתוב שם תקני=חלופה1|חלופה2"; return false; }
            var preferred = pair[0].Trim();
            var variants = pair[1].Split('|').Select(v => v.Trim()).Where(v => v.Length > 0 && !string.Equals(v, preferred, StringComparison.Ordinal)).Distinct().ToArray();
            if (variants.Length == 0) { error = $"שורה {lineNumber}: נדרשת לפחות חלופה שונה מהשם התקני"; return false; }
            terms.Add(new(preferred, variants));
        }
        return true;
    }

    private static string NamePattern(string term)
    {
        var conjunction = term.Length > 0 && term[0] >= '\u05D0' && term[0] <= '\u05EA' ? "ו?" : "";
        return @"(?<![\p{L}\p{M}\p{N}_])" + conjunction + Regex.Escape(term) + @"(?![\p{L}\p{M}\p{N}_])";
    }

    internal static bool ContainsName(string text, string term) =>
        !string.IsNullOrWhiteSpace(term) && Regex.IsMatch(text, NamePattern(term), RegexOptions.CultureInvariant);

    // Literal approved glossary matches only. Unknown names never receive a checkmark.
    internal static string NameSummary(string text, IReadOnlyList<QcTerm> terms)
    {
        var visible = Visible(text);
        var wrong = terms.SelectMany(t => t.Variants.Where(v => !string.IsNullOrWhiteSpace(v) && v != t.Preferred)
            .Where(v => ContainsName(visible, v)).Select(v => $"„{v}” — יש לכתוב „{t.Preferred}”")).Distinct().ToList();
        if (wrong.Count > 0) return "✕ שמות: " + string.Join("; ", wrong);
        var approved = terms.Where(t => !string.IsNullOrWhiteSpace(t.Preferred) && ContainsName(visible, t.Preferred))
            .Select(t => t.Preferred).Distinct().ToList();
        return approved.Count == 0 ? string.Empty : "✓ שמות לפי המילון: " + string.Join(", ", approved);
    }

    internal static string CombineNameSummaries(string approved, string detected)
    {
        if (approved.StartsWith("✕", StringComparison.Ordinal)) return approved;
        if (detected.StartsWith("✕", StringComparison.Ordinal)) return detected;
        if (detected.StartsWith("?", StringComparison.Ordinal)) return detected;
        return string.IsNullOrEmpty(approved) ? detected : approved;
    }

    // Gemini detects entities; exact guide evidence controls whether spelling can be assessed.
    // A detected alternative is an AI suspicion, never a silently approved glossary rule.
    internal static string DetectedNameSummary(string text, string guide, IReadOnlyList<QcTerm> terms)
    {
        var visible = Visible(text);
        var guideText = Visible(guide);
        var wrong = new List<string>();
        var correct = new List<string>();
        var unknown = new List<string>();
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.Preferred)) continue;
            var guideHasPreferred = ContainsName(guideText, term.Preferred);
            var variants = term.Variants.Where(v => !string.IsNullOrWhiteSpace(v) && v != term.Preferred && ContainsName(visible, v)).ToArray();
            var hasPreferred = ContainsName(visible, term.Preferred);
            if (guideHasPreferred)
            {
                foreach (var variant in variants)
                    wrong.Add($"„{variant}” — במדריך „{term.Preferred}”");
                if (hasPreferred) correct.Add(term.Preferred);
            }
            else
            {
                if (hasPreferred) unknown.Add(term.Preferred);
                unknown.AddRange(variants);
            }
        }
        if (wrong.Count > 0) return "✕ שמות — חשד AI לכתיב שונה מהמדריך, לבדיקה: " + string.Join("; ", wrong.Distinct());
        if (unknown.Count > 0) return "? שמות שזוהו בפרק ללא כתיב מוסכם במדריך: " + string.Join(", ", unknown.Distinct());
        return correct.Count == 0 ? "" : "✓ שמות תואמים למדריך: " + string.Join(", ", correct.Distinct());
    }

    internal static List<QcIssue> Scan(IReadOnlyList<QcRow> rows, QcLimits limits, IReadOnlyList<QcTerm> terms)
    {
        var issues = new List<QcIssue>();
        var patterns = terms.SelectMany(t => t.Variants.Select(v => (t.Preferred, Variant: v,
            Pattern: new Regex(NamePattern(v), RegexOptions.CultureInvariant)))).ToList();
        foreach (var row in rows)
        {
            void Add(string kind, string message) => issues.Add(new(row.Id, row.Number, kind, message));
            var visible = Visible(row.Text);
            var duration = (row.End - row.Start).TotalMilliseconds;
            if (row.Start < TimeSpan.Zero || duration <= 0) Add("time", "תזמון לא תקין: זמן סיום חייב להיות אחרי זמן ההתחלה");
            else
            {
                if (limits.MinimumMs > 0 && duration < limits.MinimumMs) Add("duration", $"משך קצר מדי: {duration:0} אלפיות שנייה");
                if (limits.MaximumMs > 0 && duration > limits.MaximumMs) Add("duration", $"משך ארוך מדי: {duration:0} אלפיות שנייה");
                if (limits.Cps > 0 && double.IsFinite(row.Cps) && row.Cps > limits.Cps) Add("reading", $"מהירות קריאה גבוהה: {row.Cps:0.0} תווים לשנייה (מקסימום {limits.Cps:0.0})");
            }
            var lines = visible.Replace("\r", "").Split('\n');
            if (limits.Lines > 0 && lines.Length > limits.Lines) Add("length", $"יותר מ־{limits.Lines} שורות");
            if (limits.LineLength > 0 && lines.Any(l => StringInfo.ParseCombiningCharacters(l).Length > limits.LineLength)) Add("length", $"שורה ארוכה מ־{limits.LineLength} תווים");
            if (string.IsNullOrWhiteSpace(visible)) Add("empty", "כתובית ריקה");
            var stack = new Stack<char>();
            var badBrackets = false;
            foreach (var ch in visible)
            {
                if (ch is '(' or '[' or '{') stack.Push(ch);
                else if (ch is ')' or ']' or '}')
                {
                    var expected = ch == ')' ? '(' : ch == ']' ? '[' : '{';
                    if (stack.Count == 0 || stack.Pop() != expected) badBrackets = true;
                }
            }
            if (badBrackets || stack.Count > 0) Add("punctuation", "סוגריים שאינם תואמים או שלא נסגרו");
            // Hebrew geresh/gershayim inside words are not quotation marks.
            var quotes = Regex.Matches(visible, "(?<![\\p{L}\\p{N}])\"|\"(?![\\p{L}\\p{N}])").Count;
            if (quotes % 2 != 0 || visible.Count(c => c is '“' or '„') != visible.Count(c => c == '”') || visible.Count(c => c == '«') != visible.Count(c => c == '»')) Add("punctuation", "מירכאות חשודות: בדוק אם נפתחו ונסגרו");
            if (Regex.IsMatch(visible, @"[,;:]{2,}|[!?]{2,}|\.{4,}")) Add("punctuation", "פיסוק כפול חשוד — לבדיקה");
            foreach (var group in row.Text.Where(InvisibleCharacters.ContainsKey).GroupBy(c => c))
                Add("invisible", $"{InvisibleCharacters[group.Key]} — U+{(int)group.Key:X4} × {group.Count()}");
            foreach (var term in patterns)
                if (term.Pattern.IsMatch(visible)) Add("term", $"מונח לא אחיד: „{term.Variant}” — במילון הוגדר „{term.Preferred}”");
        }
        QcRow? furthest = null;
        foreach (var row in rows.OrderBy(r => r.Start).ThenBy(r => r.Number))
        {
            if (row.End <= row.Start) continue;
            if (furthest != null)
            {
                var gap = (row.Start - furthest.End).TotalMilliseconds;
                if (gap < 0) issues.Add(new(row.Id, row.Number, "overlap", $"חפיפה עם כתובית {furthest.Number}"));
                else if (limits.GapMs > 0 && gap < limits.GapMs) issues.Add(new(row.Id, row.Number, "gap", $"רווח זמן קטן מדי: {gap:0} אלפיות שנייה"));
            }
            if (furthest == null || row.End > furthest.End) furthest = row;
        }
        return issues;
    }

    internal static List<string> CheckOst(IEnumerable<(string Time, string Text)> rows)
    {
        var issues = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var index = 0;
        foreach (var row in rows)
        {
            index++;
            if (string.IsNullOrWhiteSpace(Visible(row.Text))) issues.Add($"שורת OST {index}: טקסט ריק");
            if (!Regex.IsMatch(row.Time ?? "", @"^\d{2,}:\d{2}:\d{2}[,.]\d{3}$") || !TimeSpan.TryParse((row.Time ?? "").Replace(',', '.'), CultureInfo.InvariantCulture, out var time) || time < TimeSpan.Zero)
                issues.Add($"שורת OST {index}: תזמון חסר או לא תקין");
            if (!seen.Add(row.Time + "\n" + row.Text)) issues.Add($"שורת OST {index}: כפילות בטקסט ובתזמון");
        }
        if (index == 0) issues.Add("טבלת OST ריקה");
        return issues;
    }
}

