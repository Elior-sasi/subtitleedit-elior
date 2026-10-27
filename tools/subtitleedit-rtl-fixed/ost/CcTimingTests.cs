using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    private static CcTiming.Cue CcCue(string text, double start, double end) => new(Guid.NewGuid(), text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    [AvaloniaFact]
    public async Task CcTimingRealDialogOpensWithStartOnlyAndCancelDoesNotEditDocument()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var owner = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = owner; var main = new MainView(); owner.Content = main;
        try
        {
            owner.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; owner.SuppressSaveChangesPromptOnClose(vm);
            vm.LockTimeCodes = false; vm.Subtitles.Clear();
            var row = new SubtitleLineViewModel { Text = "<i>we should leave now</i>", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(4) }; vm.Subtitles.Add(row);
            var hash = vm.GetFastHash();
            var task = (Task)typeof(MainViewModel).GetMethod("OpenCcTiming", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null)!;
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows.Where(w => w.Title == "תיקון תזמוני CC לפי הדיבור — ללא שינוי טקסט"));
            Assert.All(dialog.GetVisualDescendants().OfType<CheckBox>(), c => Assert.False(c.IsChecked));
            Assert.Equal(hash, vm.GetFastHash()); dialog.Close(); await task;
            Assert.Same(row, Assert.Single(vm.Subtitles)); Assert.Equal(hash, vm.GetFastHash());
        }
        finally { owner.Close(); }
    }

    [Fact]
    public void CcTimingFindsDifferentDelaysKeepsMarkupAndDefaultsToStartOnly()
    {
        var a = CcCue("<i>נוכל להתמודד\nעם כל מה שיבוא מולנו!</i>", 10, 16);
        var b = CcCue("we can leave right now", 30, 36);
        var transcript = new[] { new ScriptTranscriptReview.Cue(12000, 15000, "נוכל להתמודד עם כל מה שיבוא מולנו"), new ScriptTranscriptReview.Cue(30500, 35000, b.Text) };
        var proposals = CcTiming.Propose(new[] { a, b }, transcript, 30000, 0, false);
        Assert.Equal(2, proposals.Count); Assert.Equal(TimeSpan.FromSeconds(12), proposals[0].Start); Assert.Equal(a.End, proposals[0].End);
        Assert.Equal(a.Text, proposals[0].Before.Text); Assert.Equal(TimeSpan.FromSeconds(30.5), proposals[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), a.Start);
        var ends = CcTiming.Propose(new[] { a }, transcript, 30000, 1000, true);
        Assert.Equal(TimeSpan.FromSeconds(11), Assert.Single(ends).Start); Assert.Equal(TimeSpan.FromSeconds(14), ends[0].End);
    }

    [Fact]
    public void CcTimingRejectsRepeatedSharedNonliteralShortAndPartialTranscriptMatches()
    {
        var cue = CcCue("we should leave now", 10, 20);
        var repeated = new[] { new ScriptTranscriptReview.Cue(11000, 12000, cue.Text), new ScriptTranscriptReview.Cue(15000, 16000, cue.Text) };
        Assert.Empty(CcTiming.Propose(new[] { cue }, repeated, 30000, 0, true));
        Assert.Empty(CcTiming.Propose(new[] { cue, cue with { Id = Guid.NewGuid() } }, repeated.Take(1).ToArray(), 30000, 0, true));
        Assert.Empty(CcTiming.Propose(new[] { cue }, new[] { repeated[0] with { Text = "yes " + cue.Text } }, 30000, 0, true));
        Assert.Empty(CcTiming.Propose(new[] { CcCue("[we should leave now]", 10, 20) }, repeated.Take(1).ToArray(), 30000, 0, true));
        Assert.Empty(CcTiming.Propose(new[] { CcCue("come here", 10, 20) }, new[] { new ScriptTranscriptReview.Cue(11000, 12000, "come here") }, 30000, 0, true));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => CcTiming.Propose(new[] { cue }, repeated, 30000, 0, true, cancel.Token));
    }

    [Fact]
    public void CcTimingRejectsPartialSelectionNewNonadjacentOverlapAndStaleRows()
    {
        var a = CcCue("first subtitle text", 10, 11); var b = CcCue("second subtitle text", 20, 21); var c = CcCue("third subtitle text", 30, 40);
        var p = new CcTiming.Proposal(a, TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(32), 0, 0);
        Assert.Throws<InvalidDataException>(() => CcTiming.ValidateApply(new[] { a, b, c }, new[] { p }));
        Assert.Throws<InvalidDataException>(() => CcTiming.ValidateApply(new[] { a with { Text = "changed" } }, new[] { p }));
        var negative = p with { Start = TimeSpan.FromSeconds(-1) };
        Assert.Throws<InvalidDataException>(() => CcTiming.ValidateApply(new[] { a }, new[] { negative }));
    }

    [Fact]
    public void CcTimingCombinesContiguousWholeTranscriptCuesWithoutInventingWordTimes()
    {
        var a = CcCue("we should leave now", 10, 20);
        var transcript = new[] { new ScriptTranscriptReview.Cue(12000, 13000, "we should"), new ScriptTranscriptReview.Cue(13500, 15000, "leave now") };
        var p = Assert.Single(CcTiming.Propose(new[] { a }, transcript, 30000, 0, true));
        Assert.Equal(TimeSpan.FromSeconds(12), p.Start); Assert.Equal(TimeSpan.FromSeconds(15), p.End);
        Assert.Empty(CcTiming.Propose(new[] { a }, new[] { transcript[0], transcript[1] with { Start = 16000, End = 17000 } }, 30000, 0, true));
    }

    [AvaloniaFact]
    public void CcTimingNativeApplyPreservesIdsTextAndUndoAndRejectsLocksMediaAndGeneration()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        var oldOffset = Se.Settings.General.CurrentVideoOffsetInMs; Se.Settings.General.CurrentVideoOffsetInMs = 0;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            vm.Subtitles.Clear(); var row = new SubtitleLineViewModel { Number = 1, Text = "<i>we should leave now</i>", StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(20) }; vm.Subtitles.Add(row);
            var generation = (int)typeof(MainViewModel).GetField("_ostDocumentGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
            var before = new CcTiming.Cue(row.Id, row.Text, row.StartTime, row.EndTime);
            var proposals = new[] { new CcTiming.Proposal(before, TimeSpan.FromSeconds(12), before.End, 0, 0) };
            var hash = vm.GetFastHash();
            vm.LockTimeCodes = true; Assert.Throws<InvalidOperationException>(() => vm.ApplyCcTiming(proposals, "", generation, hash, "", 0)); vm.LockTimeCodes = false;
            Assert.Throws<InvalidOperationException>(() => vm.ApplyCcTiming(proposals, "", generation + 1, hash, "", 0));
            Assert.Throws<InvalidOperationException>(() => vm.ApplyCcTiming(proposals, "", generation, hash, "changed.mp4", 0));
            Assert.Throws<InvalidOperationException>(() => vm.ApplyCcTiming(proposals, "", generation, hash, "", 1000));
            vm.ApplyCcTiming(proposals, "", generation, hash, "", 0);
            Assert.Equal(before.Id, row.Id); Assert.Equal(before.Text, row.Text); Assert.Equal(TimeSpan.FromSeconds(12), row.StartTime); Assert.Equal(before.End, row.EndTime);
            vm.UndoCommand.Execute(null); Assert.Equal(before.Start, vm.Subtitles[0].StartTime); Assert.Equal(before.Text, vm.Subtitles[0].Text);
        }
        finally { window.Close(); Se.Settings.General.CurrentVideoOffsetInMs = oldOffset; }
    }
}

