using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Controls.AudioVisualizerControl;

internal static class WaveformRtlMarker
{
    // Only a display indicator for actual Unicode controls, never inferred from Hebrew letters.
    internal static bool HasRtlControls(string? text)
    {
        if (text == null) return false;
        foreach (var character in text)
            if (character is '\u200F' or '\u202B' or '\u202E' or '\u2067' or '\u061C') return true;
        return false;
    }

    internal readonly record struct CaptionLayout(double Left, double Right, double MarkerLeft, bool ShowMarker);
    internal static CaptionLayout Layout(double cueLeft, double cueRight, double viewportWidth, double markerWidth,
        bool hasMarker, bool bottom, bool rtl, double captionWidth, double footerWidth)
    {
        if (!double.IsFinite(cueLeft) || !double.IsFinite(cueRight) || !double.IsFinite(viewportWidth)) return default;
        var left = Math.Max(0, cueLeft) + 4; var right = Math.Min(viewportWidth, cueRight) - 4;
        if (right <= left) return default;
        var required = markerWidth + (bottom || captionWidth <= 0 ? 0 : 24);
        var markerLeft = bottom ? Math.Max(left, cueLeft + 3 + footerWidth + (footerWidth > 0 ? 8 : 0))
            : rtl || captionWidth <= 0 ? right - markerWidth : Math.Min(right - markerWidth, left + captionWidth + 8);
        var show = hasMarker && markerWidth > 0 && right - left >= required && markerLeft >= left && markerLeft + markerWidth <= right;
        return new CaptionLayout(left, show && !bottom ? markerLeft - 8 : right, markerLeft, show);
    }
    internal static double TextOrigin(double left, double right, double width, bool rtl) => rtl ? right - width : left;

    // A native waveform settings item. Only this additive preference is saved on selection.
    internal static Control CreatePositionSetting(Action? save = null)
    {
        var panel = new StackPanel { Spacing = 6 };
        var combo = new ComboBox { Name = "CustomWaveformRtlMarkerPosition", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock { Text = text, FlowDirection = FlowDirection.RightToLeft }),
            ItemsSource = new[] { "צמוד לטקסט", "בתחתית ליד מספר הכתובית" }, SelectedIndex = Se.Settings.Waveform.CustomRtlMarkerAtBottom ? 1 : 0 };
        AutomationProperties.SetName(combo, "מיקום תגית RTL");
        var status = new TextBlock { Name = "CustomWaveformRtlMarkerStatus", Text = "הבחירה נשמרת מיד.", TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.RightToLeft };
        var restoring = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (restoring) return;
            var previous = Se.Settings.Waveform.CustomRtlMarkerAtBottom;
            try
            {
                Se.Settings.Waveform.CustomRtlMarkerAtBottom = combo.SelectedIndex == 1; (save ?? (() => Se.SaveSettings()))();
                status.Text = "מיקום תגית RTL נשמר.";
                if (TopLevel.GetTopLevel(combo) is Window { Owner: { } owner })
                    foreach (var waveform in owner.GetVisualDescendants().OfType<AudioVisualizer>()) waveform.InvalidateVisual();
            }
            catch (Exception e)
            {
                Se.Settings.Waveform.CustomRtlMarkerAtBottom = previous;
                restoring = true; try { combo.SelectedIndex = previous ? 1 : 0; } finally { restoring = false; }
                status.Text = "שמירת ההעדפה נכשלה: " + e.Message;
            }
            AutomationProperties.SetName(status, status.Text);
        };
        panel.AttachedToVisualTree += (_, _) =>
        {
            // Only this label gets RTL text; native settings geometry stays LTR.
            var row = panel.GetVisualAncestors().OfType<Grid>().FirstOrDefault(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "מיקום תגית RTL"));
            if (row != null) foreach (var label in row.Children.OfType<TextBlock>().Where(t => t.Text == "מיקום תגית RTL")) label.FlowDirection = FlowDirection.RightToLeft;
        };
        panel.Children.Add(combo); panel.Children.Add(status); return panel;
    }
}
