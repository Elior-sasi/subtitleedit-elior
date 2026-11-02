using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Options.Settings;
using Nikse.SubtitleEdit.Logic.Config;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
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
                    // Compare actual glyph rows, with caption rendering enabled separately.
                    // The marker used to start 13 pixels above the subtitle.
                    byte[]? emptyPixels = null;
                    int FirstInkRow(bool captureEmpty = false)
                    {
                        av.InvalidateVisual();
                        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var frame = waveformWindow.CaptureRenderedFrame()!;
                        var stride = frame.PixelSize.Width * 4;
                        var bytes = new byte[stride * frame.PixelSize.Height];
                        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        try { frame.CopyPixels(new PixelRect(frame.PixelSize), pin.AddrOfPinnedObject(), bytes.Length, stride); }
                        finally { pin.Free(); }
                        if (captureEmpty) { emptyPixels = bytes; return -1; }
                        for (var y = 0; y < Math.Min(60, frame.PixelSize.Height); y++)
                            for (var x = 0; x < frame.PixelSize.Width; x++)
                            {
                                var i = y * stride + x * 4;
                                if (Math.Abs(bytes[i] - emptyPixels![i]) > 30 || Math.Abs(bytes[i + 1] - emptyPixels[i + 1]) > 30 || Math.Abs(bytes[i + 2] - emptyPixels[i + 2]) > 30)
                                    return y;
                            }
                        throw new Exception("No waveform caption glyphs rendered.");
                    }
                    FirstInkRow(captureEmpty: true);
                    av.ShowParagraphText = true;
                    var captionTop = FirstInkRow();
                    av.ShowParagraphText = false;
                    vm.FixRightToLeftViaUnicodeControlCharactersCommand.Execute(null);
                    var badgeTop = FirstInkRow();
                    Assert.InRange(Math.Abs(captionTop - badgeTop), 0, 5);
                    vm.RemoveUnicodeControlCharactersCommand.Execute(null);
                    Assert.Equal(TimeSpan.FromSeconds(1), first.StartTime);
                    Assert.Equal(TimeSpan.FromSeconds(4), first.EndTime);
                }
                finally { waveformWindow.Close(); }
            }
        }
        finally { vm.AudioVisualizer = original; window.Close(); }
    }
    [Theory]
    [InlineData(0, 350, false, 0, true)]
    [InlineData(-120, 500, false, 0, true)]
    [InlineData(0, 350, true, 75, true)]
    [InlineData(0, 350, true, 340, false)]
    [InlineData(0, 30, false, 0, false)]
    [InlineData(330, 400, false, 0, false)]
    public void WaveformCaptionClampsToViewportAndNeverDrawsAPartialOrOverlappingBadge(double left, double right, bool bottom, double footer, bool visible)
    {
        var layout = WaveformRtlMarker.Layout(left, right, 350, 24, true, bottom, true, 160, footer);
        Assert.Equal(visible, layout.ShowMarker);
        Assert.True(layout.Left >= 0); Assert.True(layout.Right <= 346);
        if (!visible) return;
        Assert.InRange(layout.MarkerLeft, Math.Max(0, left) + 4, Math.Min(350, right) - 28);
        if (bottom) Assert.True(layout.MarkerLeft >= left + 3 + footer + 8);
        else Assert.True(layout.Right + 8 <= layout.MarkerLeft);
        Assert.Equal(layout.Right, WaveformRtlMarker.TextOrigin(layout.Left, layout.Right, 160, true) + 160);
        Assert.Equal(layout.Left, WaveformRtlMarker.TextOrigin(layout.Left, layout.Right, 160, false));
    }

    [AvaloniaFact]
    public void ActualWaveformSettingsItemKeepsBothPositionsAndPersistsOnlyItsPreference()
    {
        var (owner, vm) = ProEditor();
        var before = JsonSerializer.Serialize(Se.Settings); var old = Se.Settings.Waveform.CustomRtlMarkerAtBottom;
        Window? settingsWindow = null;
        try
        {
            Assert.False(JsonSerializer.Deserialize<SeWaveform>("{}")!.CustomRtlMarkerAtBottom);
            Se.Settings.Waveform.CustomRtlMarkerAtBottom = false;
            var saved = ""; var saves = 0;
            var control = WaveformRtlMarker.CreatePositionSetting(() => { saves++; saved = JsonSerializer.Serialize(Se.Settings); });
            var combo = Assert.Single(((StackPanel)control).Children.OfType<ComboBox>());
            Assert.Equal(0, combo.SelectedIndex); Assert.Equal("מיקום תגית RTL", AutomationProperties.GetName(combo));
            combo.SelectedIndex = 1; Assert.True(Se.Settings.Waveform.CustomRtlMarkerAtBottom); Assert.Equal(1, saves);
            Assert.True(JsonSerializer.Deserialize<Se>(saved)!.Waveform.CustomRtlMarkerAtBottom);
            var expected = JsonNode.Parse(before)!; var actual = JsonNode.Parse(saved)!;
            expected["Waveform"]!["CustomRtlMarkerAtBottom"] = true; Assert.True(JsonNode.DeepEquals(expected, actual));
            combo.SelectedIndex = 0; Assert.Equal(2, saves); Assert.False(Se.Settings.Waveform.CustomRtlMarkerAtBottom);
            var failure = (StackPanel)WaveformRtlMarker.CreatePositionSetting(() => throw new IOException("synthetic save failure"));
            var failingCombo = Assert.Single(failure.Children.OfType<ComboBox>()); failingCombo.SelectedIndex = 1;
            Assert.False(Se.Settings.Waveform.CustomRtlMarkerAtBottom); Assert.Equal(0, failingCombo.SelectedIndex);
            Assert.Contains("שמירת ההעדפה נכשלה", Assert.Single(failure.Children.OfType<TextBlock>()).Text);
            var settingsVm = Locator.Services.GetRequiredService<SettingsViewModel>(); settingsVm.Initialize(vm);
            var page = new SettingsPage(settingsVm); settingsWindow = new Window { Width = 1000, Height = 700, Content = page }; settingsWindow.Show(); Dispatcher.UIThread.RunJobs();
            var search = (TextBox)typeof(SettingsPage).GetField("_searchBox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            search.Text = "מיקום תגית"; Dispatcher.UIThread.RunJobs(); settingsWindow.UpdateLayout();
            Assert.Single(settingsWindow.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "CustomWaveformRtlMarkerPosition");
            Assert.Contains(settingsWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "מיקום תגית RTL");
            SaveWaveformFixture(settingsWindow, "Waveform-RTL-Settings");
        }
        finally { Se.Settings.Waveform.CustomRtlMarkerAtBottom = old; settingsWindow?.Close(); owner.Close(); }
    }

    private static void SaveWaveformFixture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is not { Length: > 0 } workspace) return;
        var dir = Path.Combine(workspace, "waveform-rtl-smoke"); Directory.CreateDirectory(dir);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        using var file = File.Create(Path.Combine(dir, name + ".png")); frame!.Save(file, PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void HebrewWaveformCaptionsAndMixedLatinNumbersStayRightAlignedInBothPositionsAndRenderers()
    {
        var (owner, vm) = ProEditor(); var originalWaveform = vm.AudioVisualizer;
        var settings = JsonSerializer.Serialize(Se.Settings.Waveform);
        try
        {
            Se.Settings.Waveform.WaveformTextFontSize = 18; Se.Settings.Waveform.WaveformUnwrapText = false;
            Se.Settings.Waveform.WaveformShowNumberAndDuration = true; Se.Settings.Waveform.WaveformShowCps = true;
            const string original = "\u202Bילדים, קדימה, בואו!\nנפגשים עם Naruto ב־12:30.\u202C";
            foreach (var skia in new[] { false, true }) foreach (var bottom in new[] { false, true })
            {
                Se.Settings.Waveform.CustomRtlMarkerAtBottom = bottom;
                vm.Subtitles.Clear(); var row = ProRow(1, 0, 4, original); vm.Subtitles.Add(row); var hash = vm.GetFastHash();
                AudioVisualizer av = skia ? new SkiaAudioVisualizer() : new AudioVisualizer(); vm.AudioVisualizer = av;
                av.WavePeaks = new WavePeakData2(126, Enumerable.Repeat(new WavePeak2(70, -70), 2520).ToArray());
                av.ShowOriginalText = false; av.ShowParagraphText = false;
                var window = new Window { Width = 700, Height = 200, Content = av };
                try
                {
                    window.Show(); window.UpdateLayout(); av.SetPosition(0, vm.Subtitles.ToList(), 0, -1, new());
                    byte[] Capture()
                    {
                        av.InvalidateVisual(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var frame = window.CaptureRenderedFrame()!; var pixels = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
                        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                        try { frame.CopyPixels(new PixelRect(frame.PixelSize), pin.AddrOfPinnedObject(), pixels.Length, frame.PixelSize.Width * 4); }
                        finally { pin.Free(); } return pixels;
                    }
                    foreach (var width in new[] { 700, 350 })
                    {
                        window.Width = width; window.Height = width == 350 ? 150 : 200; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                        av.ShowParagraphText = false; var empty = Capture(); av.ShowParagraphText = true; var caption = Capture();
                        var pixelWidth = (int)window.ClientSize.Width; int min = pixelWidth, max = -1;
                        for (var y = 0; y < 90; y++) for (var x = 0; x < pixelWidth; x++)
                        {
                            var i = (y * pixelWidth + x) * 4;
                            if (Math.Abs(caption[i] - empty[i]) > 30 || Math.Abs(caption[i + 1] - empty[i + 1]) > 30 || Math.Abs(caption[i + 2] - empty[i + 2]) > 30) { min = Math.Min(min, x); max = Math.Max(max, x); }
                        }
                        Assert.True(max > Math.Min(width, 504) - 110, $"RTL caption did not reach the visible right edge: {skia}/{bottom}/{width}: {min}..{max}");
                        Assert.True(min >= 4 && max < Math.Min(width, 504) - 3);
                        Assert.True(av.GetPreparedParagraphText(original).RightToLeft);
                        Assert.Equal(FlowDirection.RightToLeft, av.GetCachedParagraphText(av.GetPreparedParagraphText(original).Unwrapped, true).FlowDirection);
                        var runs = SkiaBidiRuns.Split("נפגשים עם Naruto ב־12:30.", true);
                        Assert.Contains(runs, r => !r.RightToLeft && r.Text.Contains("Naruto")); Assert.Contains(runs, r => !r.RightToLeft && r.Text.Contains("12:30"));
                        SaveWaveformFixture(window, $"Waveform-RTL-{(skia ? "Skia" : "Classic")}-{(bottom ? "Bottom" : "Beside")}-{width}");
                        Assert.Equal(original, row.Text); Assert.Equal(hash, vm.GetFastHash());
                    }
                    Se.Settings.Waveform.WaveformUnwrapText = true; av.ShowParagraphText = true;
                    SaveWaveformFixture(window, $"Waveform-RTL-{(skia ? "Skia" : "Classic")}-{(bottom ? "Bottom" : "Beside")}-Unwrapped");
                    Se.Settings.Waveform.WaveformUnwrapText = false;
                }
                finally { window.Close(); }
            }
        }
        finally { Se.Settings.Waveform = JsonSerializer.Deserialize<SeWaveform>(settings)!; vm.AudioVisualizer = originalWaveform; owner.Close(); }
    }
}
