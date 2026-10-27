using System.Linq;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class SubtitleLineViewModel
{
    // Reuse the native markup stripping, shaping and configured font. Unlike the
    // optional list column, an explicit scan works without changing its setting.
    internal int MeasureProPixelWidth() => GetStrippedLines().Select(CalculatePixelWidth).DefaultIfEmpty(0).Max();
}

