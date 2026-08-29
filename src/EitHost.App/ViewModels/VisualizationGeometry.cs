namespace EitHost.App.ViewModels;

/// <summary>
/// Single owner of the visualization surface geometry.
///
/// The surfaces follow their container, so the sizes below are defaults and bounds rather than
/// fixed dimensions. They stay load-bearing beyond layout: the renderer rasterises into a square
/// of the live size, ROI overlays are positioned against the same square, and ROI hit-testing
/// converts pointer coordinates back through it. Every consumer resolves the size through this
/// type so the image and its overlays cannot disagree.
/// </summary>
internal static class VisualizationGeometry
{
    /// <summary>Edge used before the container has reported a size.</summary>
    internal const int DefaultImagePixelSize = 360;

    /// <summary>
    /// Smallest square worth rendering. Layout reports transient near-zero sizes during measure,
    /// and the mesh stops being readable well before that.
    /// </summary>
    internal const int MinimumImagePixelSize = 240;

    /// <summary>
    /// Largest square the renderer rasterises. Beyond this the image control scales the bitmap,
    /// which keeps per-frame cost bounded on a large display without capping the layout size.
    /// </summary>
    internal const int MaximumImagePixelSize = 1024;

    /// <summary>
    /// Margin reserved around the mesh, as a fraction of the image edge. Kept proportional so a
    /// larger surface shows a larger mesh rather than a thicker border.
    /// </summary>
    internal const double ImagePaddingFraction = 30.0 / DefaultImagePixelSize;

    /// <summary>Plot width used before the container has reported a size.</summary>
    internal const double DefaultPlotCanvasWidth = 520.0;

    /// <summary>Narrowest plot that still carries readable axis labels.</summary>
    internal const double MinimumPlotCanvasWidth = 320.0;

    /// <summary>Widest plot worth building geometry for; wider containers letterbox instead.</summary>
    internal const double MaximumPlotCanvasWidth = 1600.0;

    /// <summary>Plot height used before the container has reported a size.</summary>
    internal const double DefaultPlotCanvasHeight = 220.0;

    internal const double MinimumPlotCanvasHeight = 140.0;

    internal const double MaximumPlotCanvasHeight = 520.0;

    /// <summary>
    /// Widest fixed-ROI heatmap in frame columns. One frame renders as one column, so the cap
    /// tracks the widest plot the geometry builder will produce.
    /// </summary>
    internal const int HeatmapMaximumWidth = (int)MaximumPlotCanvasWidth;

    /// <summary>
    /// Rounds a measured container edge to the square the renderer will actually produce.
    /// Non-finite and non-positive measurements fall back to the default rather than collapsing
    /// the surface during a layout pass.
    /// </summary>
    internal static int ClampImagePixelSize(double requested)
    {
        if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0.0)
        {
            return DefaultImagePixelSize;
        }

        var rounded = (int)Math.Round(requested, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, MinimumImagePixelSize, MaximumImagePixelSize);
    }

    internal static double ClampPlotWidth(double requested) =>
        ClampPlotExtent(requested, DefaultPlotCanvasWidth, MinimumPlotCanvasWidth, MaximumPlotCanvasWidth);

    internal static double ClampPlotHeight(double requested) =>
        ClampPlotExtent(requested, DefaultPlotCanvasHeight, MinimumPlotCanvasHeight, MaximumPlotCanvasHeight);

    /// <summary>Absolute mesh padding for a given image edge.</summary>
    internal static double PaddingFor(double imagePixelSize) => imagePixelSize * ImagePaddingFraction;

    private static double ClampPlotExtent(double requested, double fallback, double minimum, double maximum)
    {
        if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0.0)
        {
            return fallback;
        }

        return Math.Clamp(Math.Round(requested, MidpointRounding.AwayFromZero), minimum, maximum);
    }
}
