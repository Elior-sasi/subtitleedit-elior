using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

// Batch translation inside the existing "review against source / translate with the guide" flow.
// Pure logic (pairing, prompt, parsing, cost) so it can be tested without UI or network.
internal sealed record BatchRow(Guid Id, double Start, double End, string Text, string Before, string After);
internal sealed record BatchPairing(BatchRow Row, List<SourceCue> Candidates, SourceCue? Proposed, string Reason)
{
    internal bool IsUnambiguous => Proposed != null;
}
internal sealed record BatchItem(string Key, BatchRow Row, string English, List<TranslationMemoryEntry> Memory);
internal sealed record BatchProposal(string Key, string Text, string Note, string? Problem);
internal sealed record GeminiModelInfo(string Id, string DisplayName, int InputTokenLimit, int OutputTokenLimit);

internal static class CustomTranslateBatch
{
    internal const int MaxRows = 40;
    internal const int MaxPromptChars = 150_000;
    internal const string DefaultModel = "gemini-4-argon";

    internal static bool IsHebrew(string text) => Regex.IsMatch(text, @"\p{IsHebrew}");

    // A row is paired automatically only when exactly one English cue overlaps it in the
    // confirmed mapped time and that cue belongs to no other row of the batch. Anything else
    // needs the user's choice. Row order or index is never used for pairing.
    internal static List<BatchPairing> Pair(ProjectWork work, IReadOnlyList<BatchRow> rows, IReadOnlyList<SourceCue> english)
    {
        if (!work.AlignmentConfirmed) throw new InvalidOperationException("אשר תחילה את התאמת הזמנים של המקור באנגלית.");
        double Overlap(SourceCue c, BatchRow r) => Math.Min(work.Map(c.End), r.End) - Math.Max(work.Map(c.Start), r.Start);
        var overlaps = rows.ToDictionary(r => r.Id, r => english.Where(c => Overlap(c, r) > 0).ToList());
        var owners = english.ToDictionary(c => c, c => rows.Count(r => Overlap(c, r) > 0));
        return rows.Select(r =>
        {
            var hits = overlaps[r.Id];
            var near = CustomProject.Candidates(work, r.Start, r.End);
            var candidates = hits.Concat(near).Distinct().OrderBy(c => c.Start).Take(9).ToList();
            if (hits.Count == 0) return new BatchPairing(r, candidates, null, "אין משפט מקור בזמן הזה - בחר ידנית או דלג.");
            if (hits.Count > 1) return new BatchPairing(r, candidates, null, $"{hits.Count} משפטי מקור חופפים - בחר אילו מתאימים.");
            if (owners[hits[0]] > 1) return new BatchPairing(r, candidates, null, "משפט המקור משותף לכמה שורות - בחר ידנית.");
            return new BatchPairing(r, candidates, hits[0], "התאמה חד-משמעית לפי זמן - נדרש אישור.");
        }).ToList();
    }

    internal static string Tags(string text) => string.Join("\n", Regex.Matches(text, @"<(?:[^<>""']|""[^""]*""|'[^']*')+>|\{\\[^}]*\}").Select(m => m.Value));

    internal static string BuildPrompt(ProjectProfile profile, IReadOnlyList<BatchItem> items)
    {
        if (items.Count == 0) throw new InvalidOperationException("לא נבחרו שורות עם מקור מאושר.");
        if (items.Count > MaxRows) throw new InvalidOperationException($"עד {MaxRows} שורות בכל שליחה.");
        var data = JsonSerializer.Serialize(items.Select(i => new
        {
            key = i.Key, english = i.English, currentHebrew = i.Row.Text, previousLine = i.Row.Before, nextLine = i.Row.After,
            approvedMemory = i.Memory.Select(m => new { english = m.English, hebrew = m.Hebrew }),
        }), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var prompt = CustomProject.BuildPrompt(profile,
            "Translate each item's English source into Hebrew subtitle text following the project instructions, guide and approved glossary. " +
            "currentHebrew may be empty, a draft, or already final: treat it as context, do not assume it is English or wrong. " +
            "approvedMemory pairs were approved by the translator; reuse them only when the meaning really matches. " +
            "Keep every markup tag (<i>, </i>, {\\...}) exactly as in currentHebrew, or as in the English source when currentHebrew is empty. " +
            "Do not add or remove items, timestamps, numbering or subtitle separators. Return exactly one entry per key: " +
            "{\"translations\":[{\"key\":\"r1\",\"text\":\"...\",\"note\":\"short Hebrew note, or empty\"}]}.", data);
        if (prompt.Length > MaxPromptChars) throw new InvalidOperationException("הבקשה ארוכה מדי. בחר פחות שורות או קצר את המדריך.");
        return prompt;
    }

    internal static List<BatchProposal> Parse(string json, IReadOnlyList<BatchItem> items)
    {
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("translations").EnumerateArray().ToList();
        var byKey = items.ToDictionary(i => i.Key);
        var seen = new HashSet<string>();
        var result = new List<BatchProposal>();
        foreach (var e in list)
        {
            var key = e.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (!byKey.TryGetValue(key, out var item)) throw new System.IO.InvalidDataException("התשובה כוללת שורה שלא נשלחה. לא בוצע שינוי.");
            if (!seen.Add(key)) throw new System.IO.InvalidDataException("התשובה כוללת שורה כפולה. לא בוצע שינוי.");
            var text = (e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").Replace("\r\n", "\n").Trim();
            var note = e.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            string? problem = null;
            var expectedTags = string.IsNullOrWhiteSpace(item.Row.Text) ? Tags(item.English) : Tags(item.Row.Text);
            if (text.Length == 0) problem = "ההצעה ריקה.";
            else if (text.Contains("-->") || Regex.IsMatch(text, @"\n\s*\n")) problem = "ההצעה שינתה את מבנה הכתובית.";
            else if (Tags(text) != expectedTags) problem = "ההצעה שינתה תגיות עיצוב.";
            else if (text.Length > Math.Max(400, item.English.Length * 4)) problem = "ההצעה ארוכה באופן חריג.";
            result.Add(new BatchProposal(key, text, note, problem));
        }
        foreach (var missing in items.Where(i => !seen.Contains(i.Key)))
            result.Add(new BatchProposal(missing.Key, "", "", "לא התקבלה הצעה לשורה הזו."));
        return result.OrderBy(p => int.Parse(p.Key[1..], CultureInfo.InvariantCulture)).ToList();
    }

    // Published list price for gemini-4-argon (ai.google.dev, checked 2026-10-25): USD per 1M tokens.
    // Other models: price unknown here, so no number is shown.
    internal static (double Input, double Output)? Price(string model, DateTime utcNow) =>
        model != DefaultModel ? null : utcNow < new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc) ? (2.0, 10.0) : (4.0, 20.0);

    internal static string Estimate(string model, string prompt, int outputTokenLimit, DateTime utcNow)
    {
        var inputTokens = (int)Math.Ceiling(prompt.Length / 3.0); // conservative for Hebrew/English text
        var price = Price(model, utcNow);
        if (price is not (double input, double output)) return $"כ־{inputTokens:N0} טוקנים בקלט. מחיר המודל {model} אינו ידוע לתוכנה - בדוק בחשבון Google.";
        var low = inputTokens * input / 1e6;
        var high = low + outputTokenLimit * output / 1e6;
        return $"הערכה: כ־{inputTokens:N0} טוקנים בקלט (~${low:0.000}). הפלט כולל טוקני חשיבה ומוגבל ל־{outputTokenLimit:N0}, ולכן התקרה לשליחה זו כ־${high:0.00}.";
    }

    internal static bool IsValidModelId(string id) => Regex.IsMatch(id ?? "", @"^[a-z0-9][a-z0-9.\-]{2,80}$");

    internal static async Task<GeminiModelInfo?> GetModelAsync(HttpClient http, string key, string model, CancellationToken token)
    {
        if (!IsValidModelId(model)) throw new System.IO.InvalidDataException("מזהה מודל לא תקין.");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models/" + model);
        req.Headers.Add("x-goog-api-key", key);
        using var res = await http.SendAsync(req, token);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Google החזיר קוד {(int)res.StatusCode} בבדיקת המודל. בדוק מפתח והרשאות.");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token));
        return Model(doc.RootElement);
    }

    internal static async Task<List<GeminiModelInfo>> ListModelsAsync(HttpClient http, string key, CancellationToken token)
    {
        var all = new List<GeminiModelInfo>(); string? page = null;
        for (var i = 0; i < 10; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
            req.Headers.Add("x-goog-api-key", key);
            using var res = await http.SendAsync(req, token);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Google החזיר קוד {(int)res.StatusCode} ברשימת המודלים.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token));
            if (doc.RootElement.TryGetProperty("models", out var models))
                all.AddRange(models.EnumerateArray().Select(Model).Where(m => m != null && m.Id.StartsWith("gemini", StringComparison.Ordinal))!);
            page = doc.RootElement.TryGetProperty("nextPageToken", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(page)) break;
        }
        return all.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    private static GeminiModelInfo? Model(JsonElement m)
    {
        var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var id = name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name;
        if (!IsValidModelId(id)) return null;
        int I(string p) => m.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new GeminiModelInfo(id, m.TryGetProperty("displayName", out var d) ? d.GetString() ?? id : id, I("inputTokenLimit"), I("outputTokenLimit"));
    }
}

