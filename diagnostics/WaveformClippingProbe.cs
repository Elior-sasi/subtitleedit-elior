using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Controls.AudioVisualizerControl;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using Xunit;

namespace UITests.Features.Main;

public partial class OstTableTests
{
    [AvaloniaFact]
    public void WaveformReportedClippingWindowsProbe()
    {
        // Exact cue 14 supplied by the user: each line has its own RLE/PDF,
        // including the ASCII space before the first line's PDF. Never normalize it.
        const string exactReportedCue = "\u202Bתחזיקו חזק, \u202C\n\u202Bאני באה במהירות הבזק!\u202C";
        var originalSettings = JsonSerializer.Serialize(Se.Settings.Waveform);
        var probe = new WaveformGlyphProbe();
        var window = new Window { Width = 900, Height = 280, Content = probe };
        var classicMethod = typeof(AudioVisualizer).GetMethod("DrawParagraphText", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var skiaMethod = typeof(SkiaWaveformRenderer).GetMethod("DrawParagraphTextLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var renderer = new SkiaWaveformRenderer();
        var cache = (SkiaTextCache)typeof(SkiaWaveformRenderer).GetField("_textCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
        var errors = new List<string>();
        var cases = 0;
        byte[] Capture(Action<DrawingContext> paint)
        {
            probe.Paint = paint; probe.InvalidateVisual(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame()!;
            var pixels = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect(frame.PixelSize), pin.AddrOfPinnedObject(), pixels.Length, frame.PixelSize.Width * 4); }
            finally { pin.Free(); }
            return pixels;
        }
        bool RightEdgeEqual(byte[] a, byte[] b, int width, int height, double right, double scale)
        {
            for (var y = 0; y < height; y++)
                for (var x = Math.Max(0, (int)((right - 60) * scale)); x < Math.Min(width, (int)((right + 15) * scale)); x++)
                    for (var c = 0; c < 4; c++)
                        if (a[(y * width + x) * 4 + c] != b[(y * width + x) * 4 + c]) return false;
            return true;
        }
        try
        {
            window.Show(); window.UpdateLayout();
            foreach (var font in new[] { "Arial", "Segoe UI", "Tahoma", "David" })
            foreach (var size in new[] { 14, 15, 16, 18, 26 })
            foreach (var bold in new[] { false, true })
            foreach (var unwrap in new[] { false, true })
            foreach (var raw in new[] { "תחזיקו חזק,\nאני באה במהירות הבזק!", "\u200Fתחזיקו חזק,\n\u200Fאני באה במהירות הבזק!", "\u202Bתחזיקו חזק,\nאני באה במהירות הבזק!\u202C", exactReportedCue })
            {
                Se.Settings.Waveform.WaveformTextFontSize = size;
                Se.Settings.Waveform.WaveformUnwrapText = unwrap;
                var av = new AudioVisualizer();
                typeof(AudioVisualizer).GetField("_typeface", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(av, new Typeface(font, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal));
                var prepared = av.GetPreparedParagraphText(raw);
                cache.SetFont(font, bold);
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
                foreach (var right in new[] { 378d, 378.25, 378.75 })
                {
                    cases++;
                    void Paint(DrawingContext context, bool clipped)
                    {
                        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
                        using var clip = clipped ? context.PushClip(new Rect(10, 0, right - 10, 130)) : (IDisposable?)null;
                        classicMethod.Invoke(av, new object?[] { context, raw, 10d, 14d, right });
                    }
                    var full = Capture(c => Paint(c, false)); var clipped = Capture(c => Paint(c, true));
                    if (!RightEdgeEqual(full, clipped, 900, 280, right, scale))
                    {
                        errors.Add($"Classic {font}/{size}/{bold}/{unwrap}/{scale}/{right}/{raw}");
                        if (errors.Count <= 8) SaveWaveformFixture(window, $"Reported-Clipping-Classic-{errors.Count}");
                    }
                    var frame = new SkiaWaveformFrame { FontSize = size, UnwrapText = unwrap, Height = 130, TextColor = SkiaSharp.SKColors.White };
                    var paragraph = new SkiaParagraph { Lines = prepared.Lines, Unwrapped = prepared.Unwrapped, RightToLeft = prepared.RightToLeft };
                    byte[] Skia(bool clip)
                    {
                        using var bitmap = new SkiaSharp.SKBitmap(900, 280);
                        using var canvas = new SkiaSharp.SKCanvas(bitmap); canvas.Clear(SkiaSharp.SKColors.Black); canvas.Scale((float)scale);
                        if (clip) canvas.ClipRect(new SkiaSharp.SKRect(10, 0, (float)right, 130));
                        skiaMethod.Invoke(renderer, new object?[] { canvas, frame, paragraph, 10f, 14f, (byte)255, (float)right });
                        canvas.Flush(); return bitmap.Bytes;
                    }
                    if (!RightEdgeEqual(Skia(false), Skia(true), 900, 280, right, scale)) errors.Add($"Skia {font}/{size}/{bold}/{unwrap}/{scale}/{right}/{raw}");
                }
            }
            Console.WriteLine($"WINDOWS_EDGE_CASES={cases}; LOSSES={errors.Count}");
            foreach (var error in errors.Take(20)) Console.WriteLine(error);
        }
        finally { Se.Settings.Waveform = JsonSerializer.Deserialize<SeWaveform>(originalSettings)!; window.Close(); }

        var (owner, vm) = ProEditor(); vm.StopBackgroundWork(); var previous = vm.AudioVisualizer;
        var saved = JsonSerializer.Serialize(Se.Settings.Waveform);
        try
        {
            Se.Settings.Waveform.WaveformTextFontSize = 15;
            Se.Settings.Waveform.WaveformUnwrapText = false;
            Se.Settings.Waveform.CustomRtlMarkerAtBottom = true;
            foreach (var skia in new[] { false, true })
            foreach (var bold in new[] { false, true })
            {
                Se.Settings.Waveform.WaveformTextFontBold = bold;
                vm.Subtitles.Clear(); var row = ProRow(14, 45.112, 47.107, exactReportedCue); vm.Subtitles.Add(row);
                AudioVisualizer av = skia ? new SkiaAudioVisualizer() : new AudioVisualizer(); vm.AudioVisualizer = av;
                av.ZoomFactor = 1.5; av.ShowParagraphText = true; av.ShowOriginalText = false;
                av.WavePeaks = new WavePeakData2(126, Enumerable.Repeat(new WavePeak2(70, -70), 12600).ToArray());
                var screenshot = new Window { Width = 398, Height = 128, Content = av };
                try { screenshot.Show(); screenshot.UpdateLayout(); av.SetPosition(45.072, vm.Subtitles.ToList(), 0, -1, new()); SaveWaveformFixture(screenshot, $"Reported-ExactCue14-398x128-{(skia ? "Skia" : "Classic")}-{bold}"); }
                finally { screenshot.Close(); }
            }
        }
        finally { Se.Settings.Waveform = JsonSerializer.Deserialize<SeWaveform>(saved)!; vm.AudioVisualizer = previous; owner.Close(); }
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(20)));
    }
}
