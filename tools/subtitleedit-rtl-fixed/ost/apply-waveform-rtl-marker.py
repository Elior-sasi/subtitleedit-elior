"""Add a display-only RTL-control indicator to both native waveform renderers."""
from pathlib import Path
import shutil

source = Path(__file__).resolve().parent
target = Path('src/ui/Controls/AudioVisualizerControl')
shutil.copyfile(source / 'WaveformRtlMarker.cs', target / 'WaveformRtlMarker.cs')
shutil.copyfile(source / 'WaveformRtlMarkerTests.cs', Path('tests/UI/Features/Main/WaveformRtlMarkerTests.cs'))

def patch(path, changes):
    path = Path(path)
    text = path.read_text(encoding='utf-8-sig')
    for before, after in changes:
        if text.count(before) != 1:
            raise RuntimeError(f'RTL marker anchor changed in {path}: {before[:100]!r}')
        text = text.replace(before, after)
    path.write_text(text, encoding='utf-8-sig')

patch('src/ui/Logic/Config/SeWaveform.cs', [
    ('    public bool ShowToolbar { get; set; }', '    public bool CustomRtlMarkerAtBottom { get; set; }\n    public bool ShowToolbar { get; set; }'),
])
patch('src/ui/Features/Options/Settings/SettingsPage.cs', [
    ('            MakeCheckboxSetting(Se.Language.Options.Settings.WaveformUseSkiaRenderer, nameof(_vm.WaveformUseSkiaRenderer)),',
     '            MakeCheckboxSetting(Se.Language.Options.Settings.WaveformUseSkiaRenderer, nameof(_vm.WaveformUseSkiaRenderer)),\n            new SettingsItem("מיקום תגית RTL", () => Nikse.SubtitleEdit.Controls.AudioVisualizerControl.WaveformRtlMarker.CreatePositionSetting()),'),
])

patch(target / 'AudioVisualizer.cs', [
    ('        var textBounds = new Rect(currentRegionLeft + 1, contentTop, currentRegionWidth - 3, contentHeight);', '''        var prepared = GetPreparedParagraphText(text);
        var marker = WaveformRtlMarker.HasRtlControls(text) ? GetCachedParagraphText("RTL") : null;
        double captionWidth = 0;
        if (ShowParagraphText)
        {
            if (Se.Settings.Waveform.WaveformUnwrapText) captionWidth = GetCachedParagraphText(prepared.Unwrapped, prepared.RightToLeft).Width;
            else foreach (var line in prepared.Lines) captionWidth = Math.Max(captionWidth, GetCachedParagraphText(line, prepared.RightToLeft).Width);
        }
        var bottom = Se.Settings.Waveform.CustomRtlMarkerAtBottom;
        var layout = WaveformRtlMarker.Layout(currentRegionLeft, currentRegionRight, renderCtx.Width, marker?.Width ?? 0,
            marker != null, bottom, prepared.RightToLeft, captionWidth, bottom ? CustomRtlFooterWidth(paragraph, currentRegionWidth, ref renderCtx) : 0);
        var markerY = bottom && marker != null ? renderCtx.Height - 14 - marker.Height : contentTop + 14;
        var showMarker = layout.ShowMarker && marker != null && markerY >= contentTop && markerY + marker.Height <= renderCtx.Height;
        if (showMarker)
            context.DrawText(marker!, new Point(layout.MarkerLeft, markerY));
        var clipHeight = showMarker && bottom ? Math.Min(contentHeight, markerY - contentTop - 4) : contentHeight;
        var textBounds = new Rect(layout.Left, contentTop, Math.Max(0, layout.Right - layout.Left), Math.Max(0, clipHeight));'''),
    ('                DrawParagraphText(context, text, currentRegionLeft + 3, contentTop + 14);',
     '                DrawParagraphText(context, text, layout.Left, contentTop + 14, layout.Right);'),
    ('    private void DrawParagraphText(DrawingContext context, string text, double x, double y)',
     '    private void DrawParagraphText(DrawingContext context, string text, double x, double y, double? rightEdge = null)'),
    ('        var prepared = GetPreparedParagraphText(text);\n        if (Se.Settings.Waveform.WaveformUnwrapText)',
     '        var prepared = GetPreparedParagraphText(text);\n        double Origin(double width) => rightEdge.HasValue ? WaveformRtlMarker.TextOrigin(x, rightEdge.Value, width, prepared.RightToLeft) : x;\n        double InkOrigin(FormattedText value) => rightEdge.HasValue ? WaveformRtlMarker.InkOrigin(x, rightEdge.Value, value.Width, prepared.RightToLeft, value.OverhangLeading, value.Width - value.OverhangTrailing) : x;\n        if (Se.Settings.Waveform.WaveformUnwrapText)'),
    ('new Rect(x, y, unwrapped.Width, unwrapped.Height)', 'new Rect(Origin(unwrapped.Width), y, unwrapped.Width, unwrapped.Height)'),
    ('context.DrawText(unwrapped, new Point(x, y));', 'context.DrawText(unwrapped, new Point(InkOrigin(unwrapped), y));'),
    ('blur = context.PushEffect(TextBlurEffect, new Rect(x, y, width, height));', 'blur = context.PushEffect(TextBlurEffect, new Rect(Origin(width), y, width, height));'),
    ('context.DrawText(formattedText, new Point(x, y));', 'context.DrawText(formattedText, new Point(InkOrigin(formattedText), y));'),
    ('    private void DrawParagraphFooter(DrawingContext context, SubtitleLineViewModel paragraph,', '''    private double CustomRtlFooterWidth(SubtitleLineViewModel paragraph, double width, ref RenderContext renderCtx)
    {
        var n = renderCtx.ZoomFactor * renderCtx.SampleRate;
        if (n <= 15) return 0;
        if (Se.Settings.Waveform.WaveformShowNumberAndDuration)
        {
            var number = GetCachedParagraphText($"#{paragraph.Number}");
            if (n <= 51) return number.Width;
            var duration = GetCachedParagraphText(GetCachedNumberAndDurationLabel(paragraph));
            return duration.Width >= width - 4 ? number.Width : duration.Width;
        }
        return n > 99 && Se.Settings.Waveform.WaveformShowCps && paragraph.Duration.TotalMilliseconds > 0
            ? GetCachedParagraphText(GetCachedCpsLabel(ShowOriginalTextInWaveform ? paragraph.OriginalCharactersPerSecond : paragraph.CharactersPerSecond)).Width : 0;
    }

    private void DrawParagraphFooter(DrawingContext context, SubtitleLineViewModel paragraph,'''),
])

patch(target / 'SkiaAudioVisualizer.cs', [
    ('        f.UnwrapText = settings.Waveform.WaveformUnwrapText;', '        f.UnwrapText = settings.Waveform.WaveformUnwrapText;\n        f.CustomRtlMarkerAtBottom = settings.Waveform.CustomRtlMarkerAtBottom;'),
    ('    public bool UnwrapText;', '    public bool UnwrapText;\n    public bool CustomRtlMarkerAtBottom;'),
    ('                IsSelected = _selectedInView.Contains(p),', '''                IsSelected = _selectedInView.Contains(p),
                HasRtlControls = WaveformRtlMarker.HasRtlControls(showOriginal ? p.OriginalText : p.Text),'''),
    ('    public bool IsSelected;\n    public IReadOnlyList<string> Lines;', '    public bool IsSelected;\n    public bool HasRtlControls;\n    public IReadOnlyList<string> Lines;'),
])

patch(target / 'SkiaWaveformRenderer.cs', [
    ('''            canvas.Save();
            canvas.ClipRect(SKRect.Create(left + 1, 0, width - 3, f.WorkingTextHeight));
            DrawParagraphText(canvas, f, p, left + 3, TimeLineTextOffset, 255);''', '''            var marker = p.HasRtlControls ? _textCache.Get("RTL", f.FontSize) : null;
            float captionWidth = 0;
            if (f.UnwrapText) captionWidth = _textCache.Get(p.Unwrapped, f.FontSize, p.RightToLeft).Width;
            else foreach (var line in p.Lines) captionWidth = Math.Max(captionWidth, _textCache.Get(line, f.FontSize, p.RightToLeft).Width);
            var layout = WaveformRtlMarker.Layout(left, right, f.Width, marker?.Width ?? 0, marker != null,
                f.CustomRtlMarkerAtBottom, p.RightToLeft, captionWidth, f.CustomRtlMarkerAtBottom ? CustomRtlFooterWidth(f, p, width) : 0);
            var markerY = f.CustomRtlMarkerAtBottom && marker != null ? f.Height - TimeLineTextOffset - marker.Height : TimeLineTextOffset;
            var showMarker = layout.ShowMarker && marker != null && markerY >= 0 && markerY + marker.Height <= f.Height;
            if (showMarker) DrawText(canvas, marker!, (float)layout.MarkerLeft, markerY, f.TextColor);
            var clipHeight = showMarker && f.CustomRtlMarkerAtBottom ? Math.Min(f.WorkingTextHeight, markerY - 4) : f.WorkingTextHeight;
            canvas.Save();
            canvas.ClipRect(SKRect.Create((float)layout.Left, 0, (float)Math.Max(0, layout.Right - layout.Left), Math.Max(0, clipHeight)));
            DrawParagraphText(canvas, f, p, (float)layout.Left, TimeLineTextOffset, 255, (float)layout.Right);'''),
    ('private void DrawParagraphText(SKCanvas canvas, SkiaWaveformFrame f, SkiaParagraph p, float x, float y, byte alpha)',
     'private void DrawParagraphText(SKCanvas canvas, SkiaWaveformFrame f, SkiaParagraph p, float x, float y, byte alpha, float? rightEdge = null)'),
    ('DrawParagraphTextLines(canvas, f, p, x, y, alpha);', 'DrawParagraphTextLines(canvas, f, p, x, y, alpha, rightEdge);'),
    ('private void DrawParagraphTextLines(SKCanvas canvas, SkiaWaveformFrame f, SkiaParagraph p, float x, float y, byte alpha)',
     'private void DrawParagraphTextLines(SKCanvas canvas, SkiaWaveformFrame f, SkiaParagraph p, float x, float y, byte alpha, float? rightEdge = null)'),
    ('        var color = f.TextColor.WithAlpha((byte)(f.TextColor.Alpha * alpha / 255));',
     '        var color = f.TextColor.WithAlpha((byte)(f.TextColor.Alpha * alpha / 255));\n        float Origin(SkiaTextCache.ShapedText value) => rightEdge.HasValue ? (float)WaveformRtlMarker.InkOrigin(x, rightEdge.Value, value.Width, p.RightToLeft, value.Blob?.Bounds.Left ?? 0, value.Blob?.Bounds.Right ?? value.Width) : x;'),
    ('            DrawText(canvas, _textCache.Get(p.Unwrapped, f.FontSize, p.RightToLeft), x, y, color);',
     '            var unwrapped = _textCache.Get(p.Unwrapped, f.FontSize, p.RightToLeft);\n            DrawText(canvas, unwrapped, Origin(unwrapped), y, color);'),
    ('            DrawText(canvas, text, x, y, color);', '            DrawText(canvas, text, Origin(text), y, color);'),
    ('    private void DrawParagraphFooter(SKCanvas canvas, SkiaWaveformFrame f, SkiaParagraph p, float left, float width)', '''    private float CustomRtlFooterWidth(SkiaWaveformFrame f, SkiaParagraph p, float width)
    {
        if (p.NumberAndDurationLabel != null)
        {
            var duration = _textCache.Get(p.NumberAndDurationLabel, f.FontSize);
            return duration.Width >= width - 4 && p.NumberLabel != null ? _textCache.Get(p.NumberLabel, f.FontSize).Width : duration.Width;
        }
        if (p.NumberLabel != null) return _textCache.Get(p.NumberLabel, f.FontSize).Width;
        return p.CpsLabel != null ? _textCache.Get(p.CpsLabel, f.FontSize).Width : 0;
    }

    private void DrawParagraphFooter(SKCanvas canvas, SkiaWaveformFrame f, SkiaParagraph p, float left, float width)'''),
])

# Row changes invalidate the renderer even while playback is paused.
path = Path('src/ui/Features/Main/MainViewModel.cs')
text = path.read_text(encoding='utf-8-sig')
for name in ('FixRightToLeftViaUnicodeControlCharacters', 'RemoveUnicodeControlCharacters'):
    start = text.index(f'    private void {name}()')
    end = text.index('    [RelayCommand]', start)
    method = text[start:end]
    anchor = '        ShowStatus('
    if method.count(anchor) != 1:
        raise RuntimeError(f'RTL command repaint anchor changed: {name}')
    method = method.replace(anchor, '        _updateAudioVisualizer = true;\n        AudioVisualizer?.InvalidateVisual();\n\n' + anchor)
    text = text[:start] + method + text[end:]
path.write_text(text, encoding='utf-8-sig')
