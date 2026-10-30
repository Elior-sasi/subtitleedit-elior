using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Controls.AudioVisualizerControl;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic.Media;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    [Theory]
    [InlineData("שלום", false)]
    [InlineData("RTL", false)]
    [InlineData("\u202Cשלום", false)]
    [InlineData("\u202Bשלום\u202C", true)]
    [InlineData("<i>\u202Bשלום\u202C</i>", true)]
    [InlineData("hello\n\u200Fשלום", true)]
    [InlineData("\u202Ehello", true)]
    [InlineData("\u2067שלום\u2069", true)]
    public void WaveformRtlBadgeDetectsControlsRatherThanLanguage(string text, bool expected)
    {
        Assert.Equal(expected, WaveformRtlMarker.HasRtlControls(text));
    }

    [AvaloniaFact]
    public void NativeRtlFixDrawsPersistentWaveformBadgeAndRemoveClearsItWithBothRenderers()
    {
        var (window, vm) = ProEditor();
        var original = vm.AudioVisualizer;
        try
        {
            foreach (var skia in new[] { false, true })
            {
                vm.SubtitleGrid.SelectionMode = SelectionMode.Multiple;
                vm.SubtitleGrid.SelectedItem = null;
                vm.Subtitles.Clear();
                const string raw = "<i>שלום!</i>";
                var first = ProRow(1, 1, 4, raw);
                var second = ProRow(2, 5, 6, "שורה אחרת");
                vm.Subtitles.Add(first); vm.Subtitles.Add(second);
                AudioVisualizer av = skia ? new SkiaAudioVisualizer() : new AudioVisualizer();
                av.WavePeaks = new WavePeakData2(126, Enumerable.Repeat(new WavePeak2(200, -200), 1260).ToArray());
                av.ShowParagraphText = false; // Differences must be the badge, not glyph shaping.
                av.ShowOriginalText = false;
                vm.AudioVisualizer = av;
                var waveformWindow = new Window { Width = 800, Height = 200, Content = av };
                try
                {
                    waveformWindow.Show(); waveformWindow.UpdateLayout();
                    av.SetPosition(0, vm.Subtitles.ToList(), 0, -1, new());
                    byte[] Capture()
                    {
                        // Selection shading is deliberately absent: RTL is persistent metadata.
                        av.AllSelectedParagraphs = new(); av.SelectedParagraph = null;
                        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var bitmap = waveformWindow.CaptureRenderedFrame()!;
                        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default); return stream.ToArray();
                    }
                    ProSelect(vm, first);
                    var plain = Capture();
                    vm.FixRightToLeftViaUnicodeControlCharactersCommand.Execute(null);
                    Assert.True(WaveformRtlMarker.HasRtlControls(first.Text));
                    Assert.Equal(raw, Utilities.RemoveUnicodeControlChars(first.Text));
                    Assert.DoesNotContain("RTL", first.Text); // The badge must never enter saved subtitle text.
                    Assert.Equal("שורה אחרת", second.Text);
                    var marked = Capture();
                    Assert.NotEqual(plain, marked);
                    ProSelect(vm, second);
                    Assert.Equal(marked, Capture());
                    ProSelect(vm, first);
                    vm.FixRightToLeftViaUnicodeControlCharactersCommand.Execute(null);
                    Assert.Equal(marked, Capture()); // Reapplying does not duplicate the badge.
                    vm.RemoveUnicodeControlCharactersCommand.Execute(null);
                    Assert.Equal(raw, first.Text);
                    Assert.False(WaveformRtlMarker.HasRtlControls(first.Text));
                    Assert.Equal(plain, Capture());
                    Assert.Equal(TimeSpan.FromSeconds(1), first.StartTime);
                    Assert.Equal(TimeSpan.FromSeconds(4), first.EndTime);
                }
                finally { waveformWindow.Close(); }
            }
        }
        finally { vm.AudioVisualizer = original; window.Close(); }
    }
}
