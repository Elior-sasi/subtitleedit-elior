using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

// Local audio-envelope fingerprints. Scores are similarity, not a probability of
// correctness. Different mixes, repeated music, speed changes and alternate takes
// can be ambiguous: proposals always require explicit human review.
internal static class VideoEditionSync
{
    internal const int Rate = 25;
    private const int Window = 400; // 16 seconds
    internal sealed record Anchor(double Source, double Target, double Score);
    internal sealed record Segment(double Start, double End, double Offset, double Score);
    internal sealed record Cue(Guid Id, string Text, TimeSpan Start, TimeSpan End);
    internal sealed record Proposal(Cue Before, TimeSpan Start, TimeSpan End);

    internal static async Task<float[]> ReadAudio(string ffmpeg, string file, int track, CancellationToken token)
    {
        if (!File.Exists(file) || track < 0 || track > 31) throw new InvalidDataException("בחר קובץ וערוץ שמע תקינים.");
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // Keep delayed audio on the video's clock rather than starting the first
        // decoded audio sample at zero. Otherwise a 3-second audio-track delay
        // would be mistaken for a 3-second edit in the other movie.
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-copyts", "-start_at_zero", "-protocol_whitelist", "file,pipe", "-i", Path.GetFullPath(file), "-map", $"0:a:{track}", "-vn", "-ac", "1", "-ar", "1000", "-af", "aresample=1000:async=1:first_pts=0", "-f", "f32le", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("לא ניתן להפעיל FFmpeg.");
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var result = new List<float>(); var buffer = new byte[160];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, buffer.Length, false, token);
                if (read == 0) break;
                if (result.Count >= 6 * 3600 * Rate) throw new InvalidDataException("ההשוואה מוגבלת לשש שעות לכל קובץ.");
                double sum = 0; var samples = read / 4;
                for (var i = 0; i < samples; i++) { var value = BitConverter.ToSingle(buffer, i * 4); if (!float.IsFinite(value)) throw new InvalidDataException("נתוני שמע לא תקינים."); sum += value * value; }
                if (samples > 0) result.Add((float)Math.Log(1e-9 + Math.Sqrt(sum / samples)));
                if (read != buffer.Length) break;
            }
            await process.WaitForExitAsync(token); var error = await errors;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException("קריאת השמע נכשלה. בדוק שיש בקובץ ערוץ שמע נבחר. " + error[..Math.Min(error.Length, 400)]);
            if (result.Count < Window * 2) throw new InvalidDataException("נדרש לפחות חצי דקה של שמע להשוואה.");
            return result.ToArray();
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(); await errors;
        }
    }

    // Encode the relative envelope slope, making the index insensitive to gain.
    private static IEnumerable<(uint Hash, int At)> Fingerprints(float[] signal)
    {
        uint hash = 0; const uint mask = (1u << 24) - 1;
        for (var i = 1; i < signal.Length; i++)
        {
            hash = ((hash << 1) | (signal[i] > signal[i - 1] ? 1u : 0u)) & mask;
            if (i >= 24 && hash != 0 && hash != mask) yield return (hash, i - 24);
        }
    }

    internal static List<Anchor> Match(float[] source, float[] target, CancellationToken token, IProgress<int>? progress = null)
    {
        var index = new Dictionary<uint, List<int>>();
        foreach (var (hash, at) in Fingerprints(target))
        {
            token.ThrowIfCancellationRequested();
            if (!index.TryGetValue(hash, out var positions)) index[hash] = positions = new();
            // Over-common patterns (silence, sustained/repeated sounds) are not evidence.
            if (positions.Count <= 80) positions.Add(at);
        }
        var prints = Fingerprints(source).ToArray(); var anchors = new List<Anchor>(); var printIndex = 0;
        for (var start = 0; start + Window <= source.Length; start += Window / 2)
        {
            token.ThrowIfCancellationRequested(); progress?.Report((int)(100L * start / source.Length));
            var votes = new Dictionary<int, int>();
            while (printIndex < prints.Length && prints[printIndex].At < start) printIndex++;
            for (var pi = printIndex; pi < prints.Length && prints[pi].At < start + Window - 24; pi++)
            {
                var (hash, at) = prints[pi];
                if (!index.TryGetValue(hash, out var positions) || positions.Count > 80) continue;
                foreach (var position in positions)
                {
                    var offset = position - at;
                    if (start + offset < 0 || start + offset + Window > target.Length) continue;
                    votes[offset] = votes.GetValueOrDefault(offset) + 1;
                }
            }
            var scores = votes.Where(v => v.Value >= 3).OrderByDescending(v => v.Value).Take(32)
                .Select(v => (Offset: v.Key, Score: Correlation(source, start, target, start + v.Key, Window))).OrderByDescending(v => v.Score).ToArray();
            if (scores.Length == 0) continue;
            var best = scores[0]; var runner = scores.Where(v => Math.Abs(v.Offset - best.Offset) > Rate).Select(v => v.Score).DefaultIfEmpty(-1).Max();
            if (best.Score < .9 || best.Score - runner < .08) continue;
            // A good overall score must not hide an edited passage inside the window.
            var clean = true;
            for (var k = 0; k < Window; k += Rate * 2)
                if (Correlation(source, start + k, target, start + k + best.Offset, Rate * 2) < .8) { clean = false; break; }
            if (clean) anchors.Add(new Anchor((start + Window / 2.0) / Rate, (start + Window / 2.0 + best.Offset) / Rate, best.Score));
        }
        progress?.Report(100); return anchors;
    }

    private static double Correlation(float[] a, int ai, float[] b, int bi, int length)
    {
        double ax = 0, bx = 0, aa = 0, bb = 0, ab = 0;
        for (var i = 0; i < length; i++) { double x = a[ai + i], y = b[bi + i]; ax += x; bx += y; aa += x*x; bb += y*y; ab += x*y; }
        var av = aa - ax*ax/length; var bv = bb - bx*bx/length;
        if (av < .01 || bv < .01) return -1; // no reliable evidence in silence/flat sound
        return (ab - ax*bx/length) / Math.Sqrt(av*bv);
    }

    internal static List<Segment> Segments(IReadOnlyList<Anchor> anchors)
    {
        var result = new List<Segment>();
        for (var i = 1; i < anchors.Count; i++)
        {
            var a = anchors[i - 1]; var b = anchors[i]; var offset = a.Target - a.Source;
            if (b.Source <= a.Source || b.Source - a.Source > 8.01 || b.Target <= a.Target || Math.Abs(b.Target - b.Source - offset) > .041) continue;
            // Only bridge adjacent, agreeing anchors, never an unexplained edit or gap.
            var segment = new Segment(a.Source, b.Source, offset, Math.Min(a.Score, b.Score));
            if (result.Count > 0 && Math.Abs(result[^1].End - segment.Start) < .001 && Math.Abs(result[^1].Offset - offset) < .001)
                result[^1] = result[^1] with { End = segment.End, Score = Math.Min(result[^1].Score, segment.Score) };
            else result.Add(segment);
        }
        // Reject crossed/reordered/duplicated target regions; no silent reordering.
        return result.Where(s => !result.Any(t => !ReferenceEquals(t, s) && s.Start < t.Start && s.End + s.Offset > t.Start + t.Offset)).ToList();
    }

    internal static List<Proposal> Propose(IEnumerable<Cue> cues, IReadOnlyList<Segment> segments, double videoOffsetMs)
    {
        var result = new List<Proposal>();
        foreach (var cue in cues)
        {
            var start = (cue.Start.TotalMilliseconds + videoOffsetMs) / 1000; var end = (cue.End.TotalMilliseconds + videoOffsetMs) / 1000;
            var matches = segments.Where(s => start >= s.Start && end <= s.End).ToArray();
            if (end <= start || matches.Length != 1) continue;
            var offset = matches[0].Offset;
            if (cue.Start.TotalSeconds + offset < 0 || Math.Abs(offset) < .001) continue;
            result.Add(new Proposal(cue, cue.Start + TimeSpan.FromSeconds(offset), cue.End + TimeSpan.FromSeconds(offset)));
        }
        return result;
    }
}

