using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nikse.SubtitleEdit.Core.Common
{
    /// <summary>
    /// Hebrew-aware two-line subtitle breaker for Elior's workflow.
    /// Priority: syntax/readability first, visual balance second.
    /// </summary>
    internal static class HebrewLineBreaker
    {
        private const int HardPenalty = 100000;

        private static readonly HashSet<string> HardNoBreakAfter = new(StringComparer.Ordinal)
        {
            "את", "של",
            "עם", "על", "אל", "מול", "אצל", "בלי", "בין", "ליד", "בתוך", "מתוך",
            "לפני", "אחרי", "נגד", "עבור", "בגלל", "בשביל", "במהלך", "באמצעות", "כלפי",
            "תחת", "מעל", "לגבי"
        };

        private static readonly HashSet<string> StrongNoBreakAfter = new(StringComparer.Ordinal)
        {
            "לא", "אל", "אין", "אינו", "אינה", "אינם", "אינן",
            "כי", "אם", "אבל", "אלא", "כדי", "כאשר", "למרות",
            "כמה", "איזה", "איזו", "אילו", "מי", "מה", "כמו", "כל",
            "איך", "כיצד", "מתי", "איפה", "היכן", "מדוע", "לאן", "מאין",
            // Keep prefixed interrogative/relative words with their clause:
            // e.g. "למה שהתמודדת איתו", not "למה" stranded on the line above.
            "למה", "במה", "ממה", "למי", "במי", "ממי"
        };

        // A bounded lexicon avoids treating every pair beginning with ה as a noun phrase.
        private static readonly HashSet<string> DefiniteAdjectives = new(StringComparer.Ordinal)
        {
            "המוזר", "המוזרה", "המוזרים", "המוזרות",
            "החדש", "החדשה", "החדשים", "החדשות",
            "הישן", "הישנה", "הישנים", "הישנות",
            "הגדול", "הגדולה", "הגדולים", "הגדולות",
            "הקטן", "הקטנה", "הקטנים", "הקטנות",
            "הטוב", "הטובה", "הטובים", "הטובות",
            "הרע", "הרעה", "הרעים", "הרעות",
            "הראשון", "הראשונה", "הראשונים", "הראשונות",
            "האחרון", "האחרונה", "האחרונים", "האחרונות",
            "החשוב", "החשובה", "החשובים", "החשובות"
        };

        private static readonly HashSet<string> ModalVerbs = new(StringComparer.Ordinal)
        {
            "יכול", "יכולה", "יכולים", "יכולות", "רוצה", "רוצים", "רוצות",
            "צריך", "צריכה", "צריכים", "צריכות", "מוכרח", "מוכרחה", "חייב", "חייבת",
            "מסוגל", "מסוגלת", "ניסה", "ניסתה", "מנסה", "מנסים", "מנסות"
        };

        private static readonly HashSet<string> ClauseStarters = new(StringComparer.Ordinal)
        {
            "אבל", "כי", "אם", "כאשר", "אלא", "למרות"
        };

        private static readonly HashSet<string> ShortPronouns = new(StringComparer.Ordinal)
        {
            "לי", "לך", "לו", "לה", "לנו", "לכם", "לכן", "להם", "להן",
            "שלי", "שלך", "שלו", "שלה", "שלנו", "שלכם", "שלכן", "שלהם", "שלהן"
        };

        private static readonly HashSet<string> Demonstratives = new(StringComparer.Ordinal)
        {
            "הזה", "הזאת", "הזו", "האלה", "האלו", "ההוא", "ההיא", "ההם", "ההן"
        };

        private static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal)
        {
            "אחד", "אחת", "שני", "שתי", "שניים", "שתיים", "שלושה", "שלוש", "ארבעה", "ארבע",
            "חמישה", "חמש", "שישה", "שש", "שבעה", "שבע", "שמונה", "תשעה", "תשע", "עשרה", "עשר",
            "עשרים", "שלושים", "ארבעים", "חמישים", "שישים", "שבעים", "שמונים", "תשעים",
            "מאה", "מאתיים", "אלף", "אלפיים"
        };

        private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
        {
            "שקל", "שקלים", "ש״ח", "ש\"ח", "₪",
            "אחוז", "אחוזים", "%",
            "דקה", "דקות", "שעה", "שעות", "שנייה", "שניות", "שניה", "שניות",
            "יום", "ימים", "שבוע", "שבועות", "חודש", "חודשים", "שנה", "שנים",
            "ק״מ", "ק\"מ", "קילומטר", "קילומטרים", "מטר", "מטרים", "ס״מ", "ס\"מ",
            "ק״ג", "ק\"ג", "קילו", "קילוגרם", "קילוגרמים", "גרם", "גרמים",
            "מעלה", "מעלות", "פעם", "פעמים"
        };

        // Known lexical units. Hard phrases are never split unless every possible break is worse.
        private static readonly string[] HardPhrases =
        {
            "רועי טבק", "תל אביב", "ראשון לציון"
        };

        // Strong preferences: keep these together when a better syntactic break exists.
        private static readonly string[] StrongPhrases =
        {
            "בית ספר", "בית הספר", "יום ההולדת", "ראש הממשלה", "חדר העבודה", "בית החולים",
            "אף על פי כן", "בסופו של דבר", "קודם כול", "בדרך כלל", "מצד שני", "כל כך", "לא משנה",
            "כמה זמן", "איזה סרט", "איזה מהם", "כמה אנשים", "מי זה", "מה קרה",
            "משום ש", "מפני ש", "בזמן ש"
        };

        internal static string? TryBreak(string text, int maximumLength, int mergeLinesShorterThan, string language, int maxNumberOfLines)
        {
            if (string.IsNullOrWhiteSpace(text) || maxNumberOfLines > 2 || !LooksHebrew(text, language))
            {
                return null;
            }

            var normalized = NormalizeToOneLine(text);
            var visible = Visible(normalized);
            if (string.IsNullOrWhiteSpace(visible))
            {
                return null;
            }

            // Let Subtitle Edit's normal dialog handling own multi-speaker cues.
            if (visible.TrimStart().StartsWith("- ", StringComparison.Ordinal) ||
                visible.TrimStart().StartsWith("– ", StringComparison.Ordinal))
            {
                return null;
            }

            var words = SplitWords(visible);
            if (words.Length < 2)
            {
                return normalized;
            }

            // Any subtitle that is comfortably short should stay on one line, even at 7+ words.
            var comfortableLimit = Math.Min(maximumLength, Math.Max(1, mergeLinesShorterThan));
            if (visible.Length <= comfortableLimit)
            {
                return normalized;
            }

            Candidate? best = null;
            foreach (var i in SubtitleLineSpacing.BreakPositions(normalized))
            {

                var leftRaw = normalized.Substring(0, i).TrimEnd();
                var rightRaw = normalized.Substring(i + 1).TrimStart();
                var left = Visible(leftRaw);
                var right = Visible(rightRaw);
                if (left.Length == 0 || right.Length == 0)
                {
                    continue;
                }

                var leftWords = SplitWords(left);
                var rightWords = SplitWords(right);
                if (leftWords.Length == 0 || rightWords.Length == 0)
                {
                    continue;
                }

                var score = ScoreBreak(left, right, leftWords, rightWords, maximumLength);
                if (best == null || score < best.Value.Score)
                {
                    best = new Candidate(i, score);
                }
            }

            if (best == null)
            {
                return null;
            }

            return normalized.Substring(0, best.Value.Index).TrimEnd() + Environment.NewLine +
                   normalized.Substring(best.Value.Index + 1).TrimStart();
        }

        private static int ScoreBreak(string left, string right, string[] leftWords, string[] rightWords, int maximumLength)
        {
            var score = 0;
            var leftLast = CleanWord(leftWords[^1]);
            var rightFirst = CleanWord(rightWords[0]);

            // Line-length constraints outrank cosmetic balance.
            var leftOverflow = Math.Max(0, left.Length - maximumLength);
            var rightOverflow = Math.Max(0, right.Length - maximumLength);
            score += (leftOverflow + rightOverflow) * 5000;

            // Hard syntactic units: never strand a function word at line end.
            // Keep the definite noun and adjective together. Before this whole
            // phrase a preposition such as על may remain on the first line;
            // את and של retain their stricter no-break rule.
            var beforeNounPhrase = rightWords.Length >= 2 &&
                IsDefiniteNounAdjectivePair(rightFirst, CleanWord(rightWords[1]));
            if (IsDefiniteNounAdjectivePair(leftLast, rightFirst))
            {
                score += HardPenalty;
            }
            if (HardNoBreakAfter.Contains(leftLast) &&
                !(beforeNounPhrase && leftLast != "את" && leftLast != "של"))
            {
                score += HardPenalty;
            }

            // Number + unit is a single reading unit.
            if ((IsNumeric(leftLast) || NumberWords.Contains(leftLast)) && Units.Contains(rightFirst))
            {
                score += HardPenalty;
            }

            if (BreaksPhrase(leftWords, rightWords, HardPhrases))
            {
                score += HardPenalty;
            }

            // Strong Hebrew reading preferences.
            if (StrongNoBreakAfter.Contains(leftLast))
            {
                score += 1200;
            }

            // A modal and its infinitive form one verbal unit.
            if (ModalVerbs.Contains(leftLast) && rightFirst.Length > 2 &&
                rightFirst.StartsWith("ל", StringComparison.Ordinal) && rightFirst != "לא")
            {
                score += 1200;
            }

            // A conjunction belongs to the following clause. Syntax still outranks this bonus.
            if (ClauseStarters.Contains(rightFirst) ||
                (rightFirst.Length > 3 && rightFirst.StartsWith("כש", StringComparison.Ordinal)))
            {
                score -= 220;
            }

            // Prefer keeping the possessed noun with a following "של" phrase when possible.
            if (rightFirst == "של")
            {
                score += 450;
            }

            if (ShortPronouns.Contains(rightFirst))
            {
                score += 650;
            }

            if (Demonstratives.Contains(rightFirst))
            {
                score += 650;
            }

            if (NumberWords.Contains(leftLast))
            {
                score += 500;
            }

            if (BreaksPhrase(leftWords, rightWords, StrongPhrases))
            {
                score += 1000;
            }

            // Natural punctuation boundaries are good candidates, but never override syntax.
            if (EndsWithBoundaryPunctuation(left))
            {
                score -= 220;
            }

            // Avoid a whole line containing just one word, even when the word is long.
            if (leftWords.Length == 1 || rightWords.Length == 1)
            {
                score += 900;
            }

            // Avoid tiny orphan lines.
            var shorter = Math.Min(left.Length, right.Length);
            if (shorter < 8)
            {
                score += (8 - shorter) * 80;
            }

            // Visual balance comes last.
            score += Math.Abs(left.Length - right.Length) * 4;
            // Only a tie-break preference: a slightly longer second line.
            if (left.Length > right.Length)
            {
                score += 1;
            }

            return score;
        }

        private static bool IsDefiniteNounAdjectivePair(string noun, string adjective)
        {
            return noun.Length > 2 && noun[0] == 'ה' && DefiniteAdjectives.Contains(adjective);
        }

        private static bool BreaksPhrase(string[] leftWords, string[] rightWords, IEnumerable<string> phrases)
        {
            foreach (var phrase in phrases)
            {
                var p = SplitWords(phrase).Select(CleanWord).ToArray();
                if (p.Length < 2)
                {
                    continue;
                }

                for (var leftCount = 1; leftCount < p.Length; leftCount++)
                {
                    var rightCount = p.Length - leftCount;
                    if (leftWords.Length < leftCount || rightWords.Length < rightCount)
                    {
                        continue;
                    }

                    var ok = true;
                    for (var i = 0; i < leftCount; i++)
                    {
                        if (!string.Equals(CleanWord(leftWords[leftWords.Length - leftCount + i]), p[i], StringComparison.Ordinal))
                        {
                            ok = false;
                            break;
                        }
                    }

                    if (!ok)
                    {
                        continue;
                    }

                    for (var i = 0; i < rightCount; i++)
                    {
                        if (!string.Equals(CleanWord(rightWords[i]), p[leftCount + i], StringComparison.Ordinal))
                        {
                            ok = false;
                            break;
                        }
                    }

                    if (ok)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool LooksHebrew(string text, string language)
        {
            if (!string.IsNullOrWhiteSpace(language) &&
                (language.Equals("he", StringComparison.OrdinalIgnoreCase) ||
                 language.Equals("heb", StringComparison.OrdinalIgnoreCase) ||
                 language.Equals("Hebrew", StringComparison.OrdinalIgnoreCase) ||
                 language.StartsWith("he-", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var hebrew = 0;
            var letters = 0;
            foreach (var ch in Visible(text))
            {
                if (!char.IsLetter(ch))
                {
                    continue;
                }

                letters++;
                if (ch >= '\u0590' && ch <= '\u05FF')
                {
                    hebrew++;
                }
            }

            return letters > 0 && hebrew * 2 >= letters;
        }

        private static string NormalizeToOneLine(string text)
        {
            return SubtitleLineSpacing.ToSingleLine(text);
        }

        private static string Visible(string text)
        {
            return HtmlUtil.RemoveHtmlTags(text ?? string.Empty, true).Trim();
        }

        private static string[] SplitWords(string text)
        {
            return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string CleanWord(string word)
        {
            return (word ?? string.Empty).Trim('"', '\'', '“', '”', '„', '׳', '״', '.', ',', '!', '?', '…', ':', ';', '(', ')', '[', ']', '{', '}', '-', '–', '—');
        }

        private static bool IsNumeric(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                return false;
            }

            return word.All(ch => char.IsDigit(ch) || ch == '.' || ch == ',' || ch == ':' || ch == '-');
        }

        private static bool EndsWithBoundaryPunctuation(string text)
        {
            var t = text.TrimEnd();
            return t.EndsWith(".", StringComparison.Ordinal) ||
                   t.EndsWith(",", StringComparison.Ordinal) ||
                   t.EndsWith("!", StringComparison.Ordinal) ||
                   t.EndsWith("?", StringComparison.Ordinal) ||
                   t.EndsWith(":", StringComparison.Ordinal) ||
                   t.EndsWith(";", StringComparison.Ordinal) ||
                   t.EndsWith("…", StringComparison.Ordinal);
        }

        private readonly struct Candidate
        {
            internal Candidate(int index, int score)
            {
                Index = index;
                Score = score;
            }

            internal int Index { get; }
            internal int Score { get; }
        }
    }
}

