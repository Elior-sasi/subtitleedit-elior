using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal sealed class EpisodeContextDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public int CueCount { get; set; }
    public bool IncludeInAi { get; set; } = true;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed record EpisodeExcerpt(string Episode, int FirstCue, string Text);

internal static class ProjectEpisodeContext
{
    internal const int MaxDocuments = 32;
    internal const int MaxDocumentCharacters = 100_000;
    internal const int MaxTotalCharacters = 1_000_000;
    internal const int MaxPromptCharacters = 16_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static EpisodeContextDocument Read(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("פרקים קודמים: בחר קובצי SRT בעברית או באנגלית.");
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("קובץ הפרק גדול מדי; עד 2 MB לקובץ SRT.");
        List<SourceCue> cues;
        try { cues = CustomProject.ReadSrt(File.ReadAllText(path)); }
        catch (Exception e) when (e is FormatException or OverflowException)
        { throw new InvalidDataException("קובץ הפרק מכיל תזמון SRT לא תקין.", e); }
        token.ThrowIfCancellationRequested();
        if (cues.Count == 0 || cues.Count > 5000 || cues.Any(c => c.Start < 0 || c.End <= c.Start))
            throw new InvalidDataException("קובץ הפרק אינו SRT תקין, או מכיל יותר מ־5,000 כתוביות.");
        // Context is display text only. The original file and current document are never edited.
        var text = string.Join("\n", cues.Select(c => CustomQc.Visible(c.Text).Replace("\r", "").Replace("\n", " ").Trim()));
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxDocumentCharacters)
            throw new InvalidDataException("טקסט הפרק ריק או ארוך מדי; עד 100,000 תווים לפרק.");
        return new() { Name = Path.GetFileName(path), Text = text, CueCount = cues.Count };
    }

    internal static List<EpisodeContextDocument> Merge(IEnumerable<EpisodeContextDocument> existing, IEnumerable<EpisodeContextDocument> incoming)
    {
        var result = existing.ToList();
        foreach (var item in incoming)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > MaxDocumentCharacters)
                throw new InvalidDataException("פרטי פרק רקע אינם תקינים.");
            if (result.Any(e => e.Text == item.Text)) continue;
            if (result.Any(e => string.Equals(e.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("כבר קיים פרק בשם " + item.Name + ". להסרת הגרסה הקודמת השתמש ברשימה לפני צירוף גרסה אחרת.");
            result.Add(item);
        }
        if (result.Count > MaxDocuments || result.Sum(e => (long)e.Text.Length) > MaxTotalCharacters)
            throw new InvalidDataException("עד 32 פרקי רקע ועד מיליון תווים בסך הכול לפרויקט.");
        return result;
    }

    internal static string Fingerprint(ProjectProfile? profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(profile?.PreviousEpisodes ?? new(), JsonOptions))));

    internal static IReadOnlyList<EpisodeExcerpt> Select(ProjectProfile profile, string query)
    {
        var docs = (profile.PreviousEpisodes ?? new()).Where(d => d.IncludeInAi && !string.IsNullOrWhiteSpace(d.Text)).Take(MaxDocuments).ToList();
        // Most callers supply JSON with escaped Hebrew. Retrieve against string values, not JSON keys or escapes.
        try
        {
            using var data = JsonDocument.Parse(query);
            query = string.Join(" ", Strings(data.RootElement));
        }
        catch (JsonException) { }
        var tokens = Regex.Matches(query ?? "", @"[\p{L}\p{N}]{3,}").Select(m => m.Value.ToLowerInvariant()).Distinct().Take(256).ToHashSet();
        var candidates = new List<(EpisodeExcerpt Excerpt, int Score, int Order)>();
        var order = 0;
        foreach (var doc in docs)
        {
            var lines = doc.Text[..Math.Min(doc.Text.Length, MaxDocumentCharacters)].Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i += 6)
            {
                var text = string.Join("\n", lines.Skip(i).Take(6));
                if (text.Length > 1400) text = text[..1400];
                var score = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Distinct().Count(tokens.Contains);
                candidates.Add((new(doc.Name, i + 1, text), score, order++));
            }
        }
        // Relevant passages first; deterministic opening/closing context is the fallback.
        var ranked = candidates.Where(c => c.Score > 0).OrderByDescending(c => c.Score).ThenBy(c => c.Order)
            .Concat(candidates.Where(c => c.Excerpt.FirstCue == 1).Reverse())
            .Concat(candidates.AsEnumerable().Reverse());
        var chosen = new List<EpisodeExcerpt>();
        var seen = new HashSet<int>();
        foreach (var candidate in ranked)
        {
            if (!seen.Add(candidate.Order)) continue;
            var next = chosen.Append(candidate.Excerpt).ToArray();
            if (JsonSerializer.Serialize(next, JsonOptions).Length > MaxPromptCharacters) continue;
            chosen.Add(candidate.Excerpt);
            if (chosen.Count == 12) break;
        }
        return chosen;
    }

    private static IEnumerable<string> Strings(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.String => new[] { data.GetString() ?? "" },
        JsonValueKind.Object => data.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => data.EnumerateArray().SelectMany(Strings),
        _ => Array.Empty<string>(),
    };

    internal static string PromptContext(ProjectProfile profile, string query) => JsonSerializer.Serialize(Select(profile, query), JsonOptions);
}

