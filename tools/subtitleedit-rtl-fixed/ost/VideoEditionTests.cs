using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.MainHelpers;
using Nikse.SubtitleEdit.Logic.Config;
using Xunit;

namespace UITests.Features.Main;
public partial class OstTableTests
{
    private static float[] EditionSignal(int seconds, int seed)
    {
        var random = new Random(seed); return Enumerable.Range(0, seconds * VideoEditionSync.Rate).Select(_ => (float)(-5 + random.NextDouble() * 4)).ToArray();
    }
    [Fact]
    public void EditionFindsAddedOpeningAndGainChangeWithoutChangingCueText()
    {
        var source = EditionSignal(120, 713); var target = EditionSignal(5, 99).Concat(source.Select(x => x + .7f)).ToArray();
        var anchors = VideoEditionSync.Match(source, target, CancellationToken.None);
        Assert.True(anchors.Count >= 10); Assert.All(anchors, a => Assert.Equal(5, a.Target - a.Source, 2));
        var segments = VideoEditionSync.Segments(anchors); var cue = new VideoEditionSync.Cue(Guid.NewGuid(), "<i>שלום!</i>", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(22));
        var proposal = Assert.Single(VideoEditionSync.Propose(new[] { cue }, segments, 0));
        Assert.Equal(TimeSpan.FromSeconds(25), proposal.Start); Assert.Equal(cue.Text, proposal.Before.Text); Assert.Equal(TimeSpan.FromSeconds(20), cue.Start);
    }
    [Fact]
    public void EditionDoesNotBridgeDeletedSceneOrMapCueAcrossItsBoundary()
    {
        var source = EditionSignal(160, 71); var target = source.Take(60 * 25).Concat(source.Skip(80 * 25)).ToArray();
        var segments = VideoEditionSync.Segments(VideoEditionSync.Match(source, target, CancellationToken.None));
        Assert.Contains(segments, s => s.Start > 80 && Math.Abs(s.Offset + 20) < .01);
        Assert.DoesNotContain(segments, s => s.Start < 60 && s.End > 80);
        var cues = new[] { new VideoEditionSync.Cue(Guid.NewGuid(), "removed", TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(67)), new VideoEditionSync.Cue(Guid.NewGuid(), "crossing", TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(85)), new VideoEditionSync.Cue(Guid.NewGuid(), "safe", TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(102)) };
        var proposed = VideoEditionSync.Propose(cues, segments, 0);
        Assert.Equal("safe", Assert.Single(proposed).Before.Text); Assert.Equal(TimeSpan.FromSeconds(80), proposed[0].Start);
    }
    [Fact]
    public void EditionRejectsSilenceDifferentAudioAndRepeatedAmbiguousAudioAndSupportsCancellation()
    {
        var signal = EditionSignal(80, 61);
        Assert.Empty(VideoEditionSync.Match(new float[2000], new float[2000], CancellationToken.None));
        Assert.Empty(VideoEditionSync.Match(signal, EditionSignal(80, 62), CancellationToken.None));
        Assert.Empty(VideoEditionSync.Match(signal, signal.Concat(signal).ToArray(), CancellationToken.None));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => VideoEditionSync.Match(signal, signal, cancel.Token));
    }
    [Fact]
    public void EditionOnlyMapsEnclosedValidCuesAndHonorsExistingVideoOffset()
    {
        var segment = new[] { new VideoEditionSync.Segment(10, 20, 3, .99) };
        var cue = new VideoEditionSync.Cue(Guid.NewGuid(), "unchanged words", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11));
        Assert.Empty(VideoEditionSync.Propose(new[] { cue }, segment, 0));
        Assert.Equal(TimeSpan.FromSeconds(12), Assert.Single(VideoEditionSync.Propose(new[] { cue }, segment, 1000)).Start);
        Assert.Empty(VideoEditionSync.Propose(new[] { cue with { End = cue.Start } }, segment, 1000));
        Assert.Empty(VideoEditionSync.Propose(new[] { cue }, new[] { new VideoEditionSync.Segment(0, 20, -12, .99) }, 0));
        Assert.Empty(VideoEditionSync.Segments(new[] { new VideoEditionSync.Anchor(8, 8, .99), new VideoEditionSync.Anchor(32, 32, .99) }));
    }
    [AvaloniaFact]
    public void EditionApplyRequiresFreshDocumentAndUnlockedTimesAndPreservesTextIdsAndUndo()
    {
        var services = new ServiceCollection(); services.AddSubtitleEditServices(); Locator.Services = services.BuildServiceProvider();
        var window = new Window { Width = 1500, Height = 900 }; MainView.NextHostWindow = window; var main = new MainView(); window.Content = main;
        var oldOffset = Se.Settings.General.CurrentVideoOffsetInMs; Se.Settings.General.CurrentVideoOffsetInMs = 0;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); var vm = (MainViewModel)main.DataContext!; window.SuppressSaveChangesPromptOnClose(vm);
            vm.Subtitles.Clear(); var row = new SubtitleLineViewModel { Number = 1, Text = "<i>שלום!</i>", StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(12) }; vm.Subtitles.Add(row);
            var generation = (int)typeof(MainViewModel).GetField("_ostDocumentGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
            var hash = vm.GetFastHash(); var before = new VideoEditionSync.Cue(row.Id, row.Text, row.StartTime, row.EndTime);
            var proposals = new[] { new VideoEditionSync.Proposal(before, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(17)) };
            vm.LockTimeCodes = true; Assert.Throws<InvalidOperationException>(() => vm.ApplyVideoEdition(proposals, "", generation, hash, 0)); vm.LockTimeCodes = false;
            Assert.Throws<InvalidOperationException>(() => vm.ApplyVideoEdition(proposals, "different.srt", generation, hash, 0));
            Assert.Throws<InvalidOperationException>(() => vm.ApplyVideoEdition(proposals, "", generation + 1, hash, 0));
            row.Text = "edited"; Assert.Throws<InvalidOperationException>(() => vm.ApplyVideoEdition(proposals, "", generation, hash, 0)); row.Text = before.Text;
            Assert.Throws<InvalidOperationException>(() => vm.ApplyVideoEdition(proposals, "", generation, vm.GetFastHash(), 1000));
            vm.ApplyVideoEdition(proposals, "", generation, vm.GetFastHash(), 0);
            Assert.Equal(before.Id, row.Id); Assert.Equal(before.Text, row.Text); Assert.Equal(TimeSpan.FromSeconds(15), row.StartTime); Assert.Equal(TimeSpan.FromSeconds(17), row.EndTime);
            vm.UndoCommand.Execute(null); Assert.Equal(before.Start, vm.Subtitles[0].StartTime); Assert.Equal(before.End, vm.Subtitles[0].EndTime); Assert.Equal(before.Text, vm.Subtitles[0].Text);
        }
        finally { window.Close(); Se.Settings.General.CurrentVideoOffsetInMs = oldOffset; }
    }
}

