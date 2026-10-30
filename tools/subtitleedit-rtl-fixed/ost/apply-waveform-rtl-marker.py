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

patch(target / 'AudioVisualizer.cs', [
    ('        var textBounds = new Rect(currentRegionLeft + 1, contentTop, currentRegionWidth - 3, contentHeight);', '''        var marker = WaveformRtlMarker.HasRtlControls(text) ? GetCachedParagraphText("RTL") : null;
        var markerWidth = marker == null ? 0 : marker.Width + 6;
        // Clip the label inside this cue, and reserve its space so text cannot cover it.
        if (marker != null)
        {
            using (context.PushClip(new Rect(currentRegionLeft + 1, contentTop, currentRegionWidth - 3, contentHeight)))
                context.DrawText(marker, new Point(Math.Max(currentRegionLeft + 3, currentRegionRight - marker.Width - 3), contentTop + 1));
        }
        var textBounds = new Rect(currentRegionLeft + 1, contentTop, Math.Max(0, currentRegionWidth - 3 - markerWidth), contentHeight);'''),
])

patch(target / 'SkiaAudioVisualizer.cs', [
    ('                IsSelected = _selectedInView.Contains(p),', '''                IsSelected = _selectedInView.Contains(p),
                HasRtlControls = WaveformRtlMarker.HasRtlControls(showOriginal ? p.OriginalText : p.Text),'''),
    ('    public bool IsSelected;\n    public IReadOnlyList<string> Lines;', '    public bool IsSelected;\n    public bool HasRtlControls;\n    public IReadOnlyList<string> Lines;'),
])

patch(target / 'SkiaWaveformRenderer.cs', [
    ('''            canvas.Save();
            canvas.ClipRect(SKRect.Create(left + 1, 0, width - 3, f.WorkingTextHeight));
            DrawParagraphText(canvas, f, p, left + 3, TimeLineTextOffset, 255);''', '''            var marker = p.HasRtlControls ? _textCache.Get("RTL", f.FontSize) : null;
            var markerWidth = marker == null ? 0 : marker.Width + 6;
            if (marker != null)
            {
                canvas.Save();
                canvas.ClipRect(SKRect.Create(left + 1, 0, width - 3, f.WorkingTextHeight));
                DrawText(canvas, marker, Math.Max(left + 3, right - marker.Width - 3), 1, f.TextColor);
                canvas.Restore();
            }
            canvas.Save();
            canvas.ClipRect(SKRect.Create(left + 1, 0, Math.Max(0, width - 3 - markerWidth), f.WorkingTextHeight));
            DrawParagraphText(canvas, f, p, left + 3, TimeLineTextOffset, 255);'''),
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
