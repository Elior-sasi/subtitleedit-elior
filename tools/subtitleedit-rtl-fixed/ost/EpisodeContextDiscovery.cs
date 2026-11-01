using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

internal sealed record EpisodeIdentity(string Series, int Season, int Episode);
internal sealed record EpisodeCandidate(string Path, string Name, int Season, int Episode, long Length, long WrittenUtcTicks, bool MultipleVersions = false, string Root = "");
internal sealed record EpisodeDiscoveryResult(EpisodeIdentity Current, IReadOnlyList<EpisodeCandidate> Candidates, int SkippedLinks);

internal static class EpisodeContextDiscovery
{
    internal const int MaxEntries = 5000, MaxDirectories = 256, MaxDepth = 3, MaxCandidates = 256;
    private static readonly Regex EpisodeToken = new(@"(?<![\p{L}\p{N}])(?:S(?<s>\d{1,3})E(?<e>\d{1,4})|(?<s>\d{1,3})x(?<e>\d{1,4}))(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal static EpisodeIdentity? Identify(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var matches = EpisodeToken.Matches(name);
        if (matches.Count != 1) return null;
        var m = matches[0];
        var tail = name[(m.Index + m.Length)..].TrimStart('.', '_', ' ');
        if (Regex.IsMatch(tail, @"^(?:E\d|[-–+&]\s*E?\d|\d{1,3}(?:$|[ ._-]))", RegexOptions.IgnoreCase)) return null;
        var prefix = Regex.Replace(name[..m.Index], @"\[[^\]]*\]", " ").Normalize(NormalizationForm.FormKC);
        var series = string.Join(" ", Regex.Matches(prefix.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(x => x.Value));
        if (series.Length == 0 || !int.TryParse(m.Groups["s"].Value, out var season) || !int.TryParse(m.Groups["e"].Value, out var episode) || season == 0 || episode == 0) return null;
        return new(series, season, episode);
    }
    internal static string ValidateFolder(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("תיקיית הסדרה אינה זמינה. בחר תיקייה מחדש.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("בחר תיקייה ממשית ולא קישור לתיקייה.");
        return full;
    }
    internal static EpisodeDiscoveryResult Scan(string folder, string currentFile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var current = Identify(currentFile) ?? throw new InvalidDataException("לא זוהו שם סדרה ועונה/פרק בקובץ הנוכחי. נתמכים שמות כמו Series.S02E07.srt או Series.2x07.srt. אפשר להמשיך בייבוא ידני.");
        var root = ValidateFolder(folder); var currentPath = Path.GetFullPath(currentFile);
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
        var found = new List<EpisodeCandidate>(); var entries = 0; var directories = 0; var skippedLinks = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            if (++directories > MaxDirectories) throw new InvalidDataException("תיקיית הסדרה רחבה מדי. בחר תיקיית עונה או סדרה מצומצמת יותר.");
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) throw new InvalidDataException("הסריקה מוגבלת ל־5,000 פריטים. בחר תיקייה מצומצמת יותר.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { skippedLinks++; continue; }
                if ((attributes & FileAttributes.Hidden) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                { if (depth < MaxDepth) queue.Enqueue((path, depth + 1)); continue; }
                if (!Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(path), currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                var id = Identify(path);
                if (id == null || id.Series != current.Series || id.Season > current.Season || id.Season == current.Season && id.Episode >= current.Episode) continue;
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > 2_000_000) continue;
                found.Add(new(path, Path.GetRelativePath(root, path), id.Season, id.Episode, info.Length, info.LastWriteTimeUtc.Ticks, Root: root));
                if (found.Count > MaxCandidates) throw new InvalidDataException("נמצאו יותר מ־256 מועמדים. בחר תיקיית עונה מצומצמת יותר.");
            }
        }
        var duplicates = found.GroupBy(c => (c.Season, c.Episode)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return new(current, found.Select(c => c with { MultipleVersions = duplicates.Contains((c.Season, c.Episode)) }).OrderByDescending(c => c.Season).ThenByDescending(c => c.Episode).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray(), skippedLinks);
    }
    internal static EpisodeContextDocument ReadVerified(EpisodeCandidate candidate, CancellationToken token = default)
    {
        void Check()
        {
            token.ThrowIfCancellationRequested();
            var root = ValidateFolder(candidate.Root);
            var relative = Path.GetRelativePath(root, Path.GetFullPath(candidate.Path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidDataException("המועמד אינו בתיקייה שנבחרה.");
            for (var directory = Path.GetDirectoryName(candidate.Path); directory != null && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase); directory = Path.GetDirectoryName(directory))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("תיקיית מועמד הוחלפה בקישור. סרוק מחדש.");
            var file = new FileInfo(candidate.Path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != candidate.Length || file.LastWriteTimeUtc.Ticks != candidate.WrittenUtcTicks)
                throw new InvalidDataException("קובץ מועמד השתנה מאז הסריקה. סרוק מחדש: " + candidate.Name);
        }
        Check(); var doc = ProjectEpisodeContext.Read(candidate.Path, token); Check(); return doc;
    }
    internal static List<EpisodeContextDocument> ImportApproved(IEnumerable<EpisodeCandidate> candidates, CancellationToken token = default)
    {
        var picked = candidates.ToArray();
        if (picked.Length == 0 || picked.Length > ProjectEpisodeContext.MaxDocuments) throw new InvalidDataException("סמן בין פרק אחד ל־32 פרקים לצירוף.");
        var documents = picked.Select(c => ReadVerified(c, token)).ToArray();
        return ProjectEpisodeContext.Merge(Array.Empty<EpisodeContextDocument>(), documents);
    }
    internal static string LoadFolder(string preferencesPath, string profileId) => CustomProject.Load<Dictionary<string, string>>(preferencesPath).GetValueOrDefault(profileId, "");
    internal static void SaveFolder(string preferencesPath, string profileId, string folder)
    {
        if (string.IsNullOrWhiteSpace(profileId)) throw new InvalidDataException("בחר פרויקט תחילה.");
        var folders = CustomProject.Load<Dictionary<string, string>>(preferencesPath);
        folders[profileId] = ValidateFolder(folder); CustomProject.Save(preferencesPath, folders);
    }
}
