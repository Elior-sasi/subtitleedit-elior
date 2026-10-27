using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nikse.SubtitleEdit.Features.Main.MainHelpers;

// Same-language literal transcript boundaries, never estimated word times or a global delay.
internal static class CcTiming
{
    internal sealed record Cue(Guid Id, string Text, TimeSpan Start, TimeSpan End);
    internal sealed record Proposal(Cue Before, TimeSpan Start, TimeSpan End, int First, int Last);

    internal static List<Proposal> Propose(IReadOnlyList<Cue> cues, IReadOnlyList<ScriptTranscriptReview.Cue> transcript,
        double windowMs, double videoOffsetMs, bool changeEnd, CancellationToken cancel = default)
    {
        ScriptTranscriptReview.Validate(transcript);
        if (cues.Count > 5000 || !double.IsFinite(windowMs) || windowMs < 1000 || windowMs > 120000 || !double.IsFinite(videoOffsetMs))
            throw new InvalidDataException("בחר עד 5000 כתוביות וחלון חיפוש של 1–120 שניות.");
        var spoken = transcript.Select(c => ScriptTranscriptReview.Words(c.Text)).ToArray();
        var result = new List<Proposal>();
        foreach (var cue in cues)
        {
            cancel.ThrowIfCancellationRequested();
            if (cue.Start < TimeSpan.Zero || cue.End <= cue.Start) continue;
            var words = ScriptTranscriptReview.Words(cue.Text);
            // Short utterances, speaker/sound descriptions and nonliteral CC remain manual.
            if (words.Length < 3 || words.Length > 160 || cue.Text.Contains('[') || cue.Text.Contains(']') || cue.Text.Contains('♪')) continue;
            var candidates = new List<Proposal>();
            for (var first = 0; first < transcript.Count; first++)
            {
                cancel.ThrowIfCancellationRequested();
                if (Math.Abs(transcript[first].Start - (cue.Start.TotalMilliseconds + videoOffsetMs)) > windowMs) continue;
                var combined = new List<string>();
                for (var last = first; last < Math.Min(first + 4, transcript.Count); last++)
                {
                    if (last > first && (transcript[last].Start < transcript[last - 1].End || transcript[last].Start - transcript[last - 1].End > 1500)) break;
                    combined.AddRange(spoken[last]);
                    if (combined.Count > words.Length || transcript[last].End - transcript[first].Start > 30000) break;
                    if (!words.SequenceEqual(combined)) continue;
                    var start = transcript[first].Start - videoOffsetMs;
                    var end = changeEnd ? transcript[last].End - videoOffsetMs : cue.End.TotalMilliseconds;
                    if (start < 0 || end <= start || end - start > 30000) continue;
                    candidates.Add(new Proposal(cue, TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end), first, last));
                }
            }
            // Repeated phrases and competing boundaries are deliberately not guessed.
            if (candidates.Count == 1) result.Add(candidates[0]);
        }
        var unique = result.Where(p => !result.Any(q => q.Before.Id != p.Before.Id && q.First <= p.Last && p.First <= q.Last)).ToList();
        return unique.Where(p => Math.Abs((p.Start - p.Before.Start).TotalMilliseconds) >= 10 || Math.Abs((p.End - p.Before.End).TotalMilliseconds) >= 10).ToList();
    }

    internal static void ValidateApply(IReadOnlyList<Cue> all, IReadOnlyList<Proposal> proposals)
    {
        if (all.Select(c => c.Id).Distinct().Count() != all.Count || proposals.Select(p => p.Before.Id).Distinct().Count() != proposals.Count)
            throw new InvalidDataException("בחירה כפולה או מסמך לא תקין.");
        var current = all.ToDictionary(c => c.Id);
        var changes = proposals.ToDictionary(p => p.Before.Id);
        foreach (var p in proposals)
        {
            if (!current.TryGetValue(p.Before.Id, out var c) || c != p.Before || p.Start < TimeSpan.Zero || p.End <= p.Start || p.End - p.Start > TimeSpan.FromSeconds(30))
                throw new InvalidDataException("הכתובית השתנתה או שהזמנים אינם תקינים. נתח מחדש.");
            foreach (var other in all)
            {
                if (other.Id == c.Id) continue;
                var start = changes.TryGetValue(other.Id, out var q) ? q.Start : other.Start;
                var end = q?.End ?? other.End;
                var overlapped = c.Start < other.End && other.Start < c.End;
                if (!overlapped && p.Start < end && start < p.End)
                    throw new InvalidDataException("הבחירה יוצרת חפיפה חדשה. בטל סימון ובדוק את גבולות הכתוביות.");
                if ((c.Start < other.Start && p.Start > start) || (c.Start > other.Start && p.Start < start))
                    throw new InvalidDataException("הבחירה משנה את סדר הכתוביות. בדוק את ההצעות.");
            }
        }
    }
}

