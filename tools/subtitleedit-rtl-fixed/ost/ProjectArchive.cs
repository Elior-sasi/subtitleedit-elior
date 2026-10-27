using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal sealed class TranslationMemoryEntry
{
    public string English { get; set; } = "";
    public string Hebrew { get; set; } = "";
    public string Document { get; set; } = "";
    public DateTime ApprovedAtUtc { get; set; }
}
internal sealed class ProjectBackupSettings
{
    public string Folder { get; set; } = "";
    public bool Automatic { get; set; }
}
internal sealed record MemoryMatch(TranslationMemoryEntry Entry, double Similarity);
internal static class ProjectArchive
{
    internal static string Normalize(string text) => Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"<[^>]*>|\{\\[^}]*\}", " "), @"[^\p{L}\p{M}\p{N}]+", " ").Trim();
    internal static List<MemoryMatch> Find(IEnumerable<TranslationMemoryEntry> memory, string source)
    {
        var normalized = Normalize(source); if (normalized.Length == 0) return new();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return memory.Select(e => {
            var n = Normalize(e.English); var other = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            // Short phrases need an exact match; word overlap is a candidate score, never semantic certainty.
            var score = n == normalized ? 1 : tokens.Count >= 4 && other.Count >= 4 ? (double)tokens.Intersect(other).Count() / tokens.Union(other).Count() : 0;
            return new MemoryMatch(e, score);
        }).Where(m => m.Similarity >= .55).OrderByDescending(m => m.Similarity).ThenByDescending(m => m.Entry.ApprovedAtUtc).Take(8).ToList();
    }
    internal static void Approve(ProjectProfile profile, string english, string hebrew, string document)
    {
        if (string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(hebrew)) throw new InvalidDataException("נדרשים מקור ותרגום לאישור.");
        if (profile.Memory.Any(e => e.English == english && e.Hebrew == hebrew && e.Document == Path.GetFileName(document))) return;
        profile.Memory.Add(new TranslationMemoryEntry { English = english, Hebrew = hebrew, Document = Path.GetFileName(document), ApprovedAtUtc = DateTime.UtcNow });
    }
    internal static string Fingerprint(IReadOnlyDictionary<string, string> files) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files.OrderBy(p => p.Key).Select(p => new[] { p.Key, p.Value })))));
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "subtitles.srt", "subtitles.ass", "subtitles.ssa", "subtitles.vtt", "project.sework.json", "table.ostproject", "guide-and-memory.json" };
    internal static string WriteSnapshot(string folder, string document, IReadOnlyDictionary<string, string> files, double videoOffsetMilliseconds = 0)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("תיקיית הגיבוי אינה זמינה. הגיבוי המקומי הרגיל נשאר ללא שינוי.");
        if (files.Count == 0 || files.Keys.Any(k => !Allowed.Contains(k))) throw new InvalidDataException("תוכן גיבוי לא מוכר.");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(document).ToUpperInvariant())))[..16];
        var root = Path.Combine(folder, "SubtitleEdit-Elior-Backups", identity); Directory.CreateDirectory(root);
        var final = Path.Combine(root, DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        var temp = final + ".partial";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = files.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Value))));
                foreach (var pair in files) { using var writer = new StreamWriter(zip.CreateEntry(pair.Key).Open(), new UTF8Encoding(false)); writer.Write(pair.Value); }
                using var metadata = new StreamWriter(zip.CreateEntry("backup.json").Open(), new UTF8Encoding(false));
                metadata.Write(JsonSerializer.Serialize(new { version = 1, document = Path.GetFileName(document), createdAtUtc = DateTime.UtcNow, videoOffsetMilliseconds, hashes = manifest }));
            }
            File.Move(temp, final); return final;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static string RestoreSnapshot(string archive, string destination, double currentVideoOffsetMilliseconds = 0)
    {
        if (new FileInfo(archive).Length > 50_000_000) throw new InvalidDataException("הגיבוי גדול מדי.");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 8 || zip.Entries.Any(e => (e.FullName != "backup.json" && !Allowed.Contains(e.FullName)) || e.Length > 40_000_000) || zip.Entries.Sum(e => e.Length) > 60_000_000 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count) throw new InvalidDataException("מבנה הגיבוי אינו תקין.");
        var info = zip.GetEntry("backup.json") ?? throw new InvalidDataException("חסר תיאור הגיבוי.");
        using var reader = new StreamReader(info.Open()); using var json = JsonDocument.Parse(reader.ReadToEnd());
        if (json.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("גרסת גיבוי לא מוכרת.");
        var hashes = json.RootElement.GetProperty("hashes").Deserialize<Dictionary<string, string>>()!;
        var data = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries.Where(e => e.FullName != "backup.json"))
        {
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); var bytes = output.ToArray();
            if (!hashes.TryGetValue(entry.FullName, out var hash) || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("בדיקת שלמות הגיבוי נכשלה.");
            data.Add(entry.FullName, bytes);
        }
        if (!data.ContainsKey("subtitles.srt") || data.Count != hashes.Count) throw new InvalidDataException("הגיבוי חסר.");
        var savedOffset = json.RootElement.TryGetProperty("videoOffsetMilliseconds", out var offsetValue) ? offsetValue.GetDouble() : 0;
        var delta = savedOffset - currentVideoOffsetMilliseconds;
        if (!double.IsFinite(delta)) throw new InvalidDataException("היסט גיבוי לא תקין.");
        if (delta != 0 && data.TryGetValue("project.sework.json", out var workBytes))
        {
            var work = JsonSerializer.Deserialize<ProjectWork>(workBytes) ?? throw new InvalidDataException("פרטי פרויקט לא תקינים.");
            foreach (var note in work.Notes) { note.Start += delta; note.End += delta; }
            work.TargetAnchor1 += delta; work.TargetAnchor2 += delta;
            data["project.sework.json"] = JsonSerializer.SerializeToUtf8Bytes(work);
        }
        var target = Path.Combine(destination, "SubtitleEdit-Restored-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
        // Restore to a new directory only. Never overwrite an open document or settings.
        foreach (var pair in data) File.WriteAllBytes(Path.Combine(target, pair.Key switch { "project.sework.json" => "subtitles.sework.json", "table.ostproject" => "subtitles.ostproject", _ => pair.Key }), pair.Value);
        return target;
    }
}

