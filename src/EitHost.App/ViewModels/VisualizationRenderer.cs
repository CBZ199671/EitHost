using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels;

internal static class VisualizationRenderer
{
    private const int RealtimeImageElectrodeCount = 16;
    private const double RealtimeImageElectrodeTangentialPixels = 18.0;
    private const double RealtimeImageElectrodeRadialPixels = 9.0;
    private const int RealtimeImageElectrodeLabelScale = 2;
    private const string RealtimeSignalViewModeDemod = "demod";
    private const string RealtimeSignalViewModeReference = "reference";
    private const string RealtimeSignalViewModeTarget = "target";
    private const string RealtimeDemodDisplayModeRectangular = "rectangular";
    private const string RealtimeDemodDisplayModePolar = "polar";
    private static readonly string[] ElectrodeDigit0 = ["111", "101", "101", "101", "111"];
    private static readonly string[] ElectrodeDigit1 = ["010", "110", "010", "010", "111"];
    private static readonly string[] ElectrodeDigit2 = ["111", "001", "111", "100", "111"];
    private static readonly string[] ElectrodeDigit3 = ["111", "001", "111", "001", "111"];
    private static readonly string[] ElectrodeDigit4 = ["101", "101", "111", "001", "001"];
    private static readonly string[] ElectrodeDigit5 = ["111", "100", "111", "001", "111"];
    private static readonly string[] ElectrodeDigit6 = ["111", "100", "111", "101", "111"];
    private static readonly string[] ElectrodeDigit7 = ["111", "001", "010", "100", "100"];
    private static readonly string[] ElectrodeDigit8 = ["111", "101", "111", "101", "111"];
    private static readonly string[] ElectrodeDigit9 = ["111", "101", "111", "001", "111"];

    internal static ImageSource RenderReconstructionImage(
        RealtimeReconstructionResult result,
        string imagePolarity,
        double imageGain,
        IReadOnlyList<ElectrodeContactState>? electrodeStates = null,
        int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
    {
        return new RealtimeImageRasterCache().Render(
            result,
            imagePolarity,
            imageGain,
            electrodeStates,
            imagePixelSize);
    }

    internal static ImageSource RenderPreReferenceContactDiagnosticImage(
        IReadOnlyList<ElectrodeContactState> electrodeStates,
        int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
    {
        var edge = VisualizationGeometry.ClampImagePixelSize(imagePixelSize);
        var pixels = new int[checked(edge * edge)];
        Array.Fill(pixels, unchecked((int)0xFFF8FAFC));
        DrawCircle(pixels, edge, edge, unchecked((int)0xFF334155));
        DrawPeripheralElectrodes(pixels, edge, edge, electrodeStates);
        return CreateBitmap(pixels, edge, edge);
    }

    internal static ImageSource RenderReconstructionImageCached(
        RealtimeReconstructionResult result,
        string imagePolarity,
        double imageGain,
        IReadOnlyList<ElectrodeContactState>? electrodeStates,
        RealtimeImageRasterCache rasterCache,
        int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
    {
        return rasterCache.Render(result, imagePolarity, imageGain, electrodeStates, imagePixelSize);
    }

    private static BitmapSource CreateBitmap(int[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static (double X, double Y) Transform(
        double x,
        double y,
        double minX,
        double minY,
        double spanX,
        double spanY,
        int width,
        int height)
    {
        var padding = VisualizationGeometry.PaddingFor(Math.Min(width, height));
        var drawableWidth = width - 2.0 * padding;
        var drawableHeight = height - 2.0 * padding;
        var px = padding + ((x - minX) / spanX) * drawableWidth;
        var py = height - padding - ((y - minY) / spanY) * drawableHeight;
        return (px, py);
    }

    private static void FillTriangle(
        int[] cells,
        float[] barycentricWeight0,
        float[] barycentricWeight1,
        int width,
        int height,
        (double X, double Y) p0,
        (double X, double Y) p1,
        (double X, double Y) p2,
        int cell)
    {
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        var area = Edge(p0, p1, p2);
        if (Math.Abs(area) <= 1.0e-9)
        {
            return;
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var point = (X: x + 0.5, Y: y + 0.5);
                var w0 = Edge(p1, p2, point);
                var w1 = Edge(p2, p0, point);
                var w2 = Edge(p0, p1, point);
                if (area > 0 ? w0 >= 0 && w1 >= 0 && w2 >= 0 : w0 <= 0 && w1 <= 0 && w2 <= 0)
                {
                    var pixelIndex = checked(y * width + x);
                    cells[pixelIndex] = cell;
                    barycentricWeight0[pixelIndex] = (float)(w0 / area);
                    barycentricWeight1[pixelIndex] = (float)(w1 / area);
                }
            }
        }
    }

    private static double Edge((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        return (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);
    }

    private static int ColorFor(double value, double center, double range, bool invert, double gain)
    {
        var delta = value - center;
        if (invert)
        {
            delta = -delta;
        }

        var t = Math.Clamp((delta / range) * gain, -1.0, 1.0);
        var magnitude = Math.Abs(t);
        var white = (R: 248, G: 250, B: 252);
        var cold = (R: 37, G: 99, B: 235);
        var hot = (R: 220, G: 38, B: 38);
        var target = t >= 0 ? hot : cold;
        var r = (byte)Math.Round(white.R + (target.R - white.R) * magnitude);
        var g = (byte)Math.Round(white.G + (target.G - white.G) * magnitude);
        var b = (byte)Math.Round(white.B + (target.B - white.B) * magnitude);
        return unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b));
    }

    internal static string NormalizeRealtimeImagePolarity(string? polarity)
    {
        return string.Equals(polarity?.Trim(), "inverted", StringComparison.Ordinal)
            ? "inverted"
            : "normal";
    }

    internal static string NormalizeRealtimeSignalViewMode(string? viewMode)
    {
        return viewMode?.Trim().ToLowerInvariant() switch
        {
            RealtimeSignalViewModeReference => RealtimeSignalViewModeReference,
            RealtimeSignalViewModeTarget => RealtimeSignalViewModeTarget,
            _ => RealtimeSignalViewModeDemod
        };
    }

    internal static string NormalizeRealtimeDemodDisplayMode(string? displayMode)
    {
        return string.Equals(
            displayMode?.Trim(),
            RealtimeDemodDisplayModePolar,
            StringComparison.OrdinalIgnoreCase)
            ? RealtimeDemodDisplayModePolar
            : RealtimeDemodDisplayModeRectangular;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return 0.0;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 0
            ? (finite[middle - 1] + finite[middle]) / 2.0
            : finite[middle];
    }

    private static void DrawCircle(int[] pixels, int width, int height, int color)
    {
        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;
        var radius = (Math.Min(width, height) / 2.0) - VisualizationGeometry.PaddingFor(Math.Min(width, height));
        var inner = radius - 1.5;
        var outer = radius + 1.5;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2));
                if (distance >= inner && distance <= outer)
                {
                    pixels[checked(y * width + x)] = color;
                }
            }
        }
    }

    private static void DrawPeripheralElectrodes(
        int[] pixels,
        int width,
        int height,
        IReadOnlyList<ElectrodeContactState>? electrodeStates = null)
    {
        for (var electrode = 0; electrode < RealtimeImageElectrodeCount; electrode++)
        {
            var electrodeNumber = electrode + 1;
            var geometry = GetElectrodeOverlayGeometry(Math.Min(width, height), electrodeNumber);
            DrawTangentialElectrodePad(
                pixels,
                width,
                height,
                (geometry.ElectrodeCenter.X, geometry.ElectrodeCenter.Y),
                Math.Cos(geometry.AngleRadians),
                Math.Sin(geometry.AngleRadians),
                RealtimeImageElectrodeTangentialPixels,
                RealtimeImageElectrodeRadialPixels,
                unchecked((int)0xFF0F172A));
            DrawTangentialElectrodePad(
                pixels,
                width,
                height,
                (geometry.ElectrodeCenter.X, geometry.ElectrodeCenter.Y),
                Math.Cos(geometry.AngleRadians),
                Math.Sin(geometry.AngleRadians),
                RealtimeImageElectrodeTangentialPixels - 5.0,
                RealtimeImageElectrodeRadialPixels - 3.0,
                InnerElectrodeColor(electrodeStates, electrode));
            DrawElectrodeLabel(
                pixels,
                width,
                height,
                electrodeNumber,
                geometry.LabelBounds,
                geometry.LabelScale,
                geometry.LabelSpacing,
                unchecked((int)0xFF0F172A));
        }
    }

    internal static ElectrodeOverlayGeometry GetElectrodeOverlayGeometry(
        int imagePixelSize,
        int electrodeNumber)
    {
        var edge = VisualizationGeometry.ClampImagePixelSize(imagePixelSize);
        var angle = GetElectrodeDisplayAngleRadians(electrodeNumber);
        var radialX = Math.Cos(angle);
        var radialY = Math.Sin(angle);
        var cx = (edge - 1) / 2.0;
        var cy = (edge - 1) / 2.0;
        var meshRadius = (edge / 2.0) - VisualizationGeometry.PaddingFor(edge);

        // The pad starts at the mesh boundary. The previous 0.95×radial offset left an
        // unnecessary gap outside the mesh and forced edge labels back into the pad at small
        // raster sizes.
        var electrodeRadius = meshRadius + (RealtimeImageElectrodeRadialPixels / 2.0);
        var electrodeCenter = new Point(
            cx + (radialX * electrodeRadius),
            cy + (radialY * electrodeRadius));
        var tangentialX = -radialY;
        var tangentialY = radialX;
        var halfTangential = RealtimeImageElectrodeTangentialPixels / 2.0;
        var halfRadial = RealtimeImageElectrodeRadialPixels / 2.0;
        var electrodeHalfWidth =
            (Math.Abs(radialX) * halfRadial) + (Math.Abs(tangentialX) * halfTangential);
        var electrodeHalfHeight =
            (Math.Abs(radialY) * halfRadial) + (Math.Abs(tangentialY) * halfTangential);
        var electrodeBounds = new Rect(
            electrodeCenter.X - electrodeHalfWidth,
            electrodeCenter.Y - electrodeHalfHeight,
            electrodeHalfWidth * 2.0,
            electrodeHalfHeight * 2.0);

        var labelScale = edge < VisualizationGeometry.DefaultImagePixelSize
            ? 1
            : RealtimeImageElectrodeLabelScale;
        var labelSpacing = Math.Max(1, labelScale);
        var labelRadius = meshRadius + RealtimeImageElectrodeRadialPixels + 12.0;
        var targetLabelCenter = new Point(
            cx + (radialX * labelRadius),
            cy + (radialY * labelRadius));
        var text = electrodeNumber.ToString(CultureInfo.InvariantCulture);
        var glyphWidth = ElectrodeDigit0[0].Length * labelScale;
        var glyphHeight = ElectrodeDigit0.Length * labelScale;
        var textWidth = (text.Length * glyphWidth) + ((text.Length - 1) * labelSpacing);
        var left = Math.Clamp(
            Math.Round(targetLabelCenter.X - (textWidth / 2.0)),
            2.0,
            Math.Max(2.0, edge - textWidth - 2.0));
        var top = Math.Clamp(
            Math.Round(targetLabelCenter.Y - (glyphHeight / 2.0)),
            2.0,
            Math.Max(2.0, edge - glyphHeight - 2.0));

        return new ElectrodeOverlayGeometry(
            angle,
            electrodeCenter,
            electrodeBounds,
            new Rect(left, top, textWidth, glyphHeight),
            labelScale,
            labelSpacing);
    }

    internal static double GetElectrodeDisplayAngleRadians(int electrodeNumber)
    {
        if (electrodeNumber < 1 || electrodeNumber > RealtimeImageElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(electrodeNumber));
        }

        return (-Math.PI / 2.0)
            - ((electrodeNumber - 1) * 2.0 * Math.PI / RealtimeImageElectrodeCount);
    }

    private static int InnerElectrodeColor(IReadOnlyList<ElectrodeContactState>? states, int electrode)
    {
        if (states is null || electrode < 0 || electrode >= states.Count)
        {
            return unchecked((int)0xFFE0F2FE);
        }

        return states[electrode] switch
        {
            ElectrodeContactState.Green => unchecked((int)0xFFDCFCE7),
            ElectrodeContactState.Yellow => unchecked((int)0xFFFDE68A),
            ElectrodeContactState.Red => unchecked((int)0xFFF87171),
            ElectrodeContactState.DarkRed => unchecked((int)0xFF991B1B),
            ElectrodeContactState.SystemLevel => unchecked((int)0xFFCBD5E1),
            _ => unchecked((int)0xFFE0F2FE)
        };
    }

    private static void DrawElectrodeLabel(
        int[] pixels,
        int width,
        int height,
        int electrodeNumber,
        Rect labelBounds,
        int labelScale,
        int labelSpacing,
        int color)
    {
        var text = electrodeNumber.ToString(CultureInfo.InvariantCulture);
        var glyphWidth = ElectrodeDigit0[0].Length * labelScale;
        var left = (int)labelBounds.Left;
        var top = (int)labelBounds.Top;

        var cursorX = left;
        foreach (var digit in text)
        {
            DrawElectrodeDigit(
                pixels,
                width,
                height,
                ElectrodeDigitGlyph(digit),
                cursorX,
                top,
                labelScale,
                color);
            cursorX += glyphWidth + labelSpacing;
        }
    }

    private static string[] ElectrodeDigitGlyph(char digit)
    {
        return digit switch
        {
            '0' => ElectrodeDigit0,
            '1' => ElectrodeDigit1,
            '2' => ElectrodeDigit2,
            '3' => ElectrodeDigit3,
            '4' => ElectrodeDigit4,
            '5' => ElectrodeDigit5,
            '6' => ElectrodeDigit6,
            '7' => ElectrodeDigit7,
            '8' => ElectrodeDigit8,
            '9' => ElectrodeDigit9,
            _ => ElectrodeDigit0
        };
    }

    private static void DrawElectrodeDigit(
        int[] pixels,
        int width,
        int height,
        IReadOnlyList<string> glyph,
        int left,
        int top,
        int labelScale,
        int color)
    {
        for (var row = 0; row < glyph.Count; row++)
        {
            var line = glyph[row];
            for (var column = 0; column < line.Length; column++)
            {
                if (line[column] != '1')
                {
                    continue;
                }

                FillPixelBlock(
                    pixels,
                    width,
                    height,
                    left + (column * labelScale),
                    top + (row * labelScale),
                    labelScale,
                    color);
            }
        }
    }

    internal sealed record ElectrodeOverlayGeometry(
        double AngleRadians,
        Point ElectrodeCenter,
        Rect ElectrodeBounds,
        Rect LabelBounds,
        int LabelScale,
        int LabelSpacing);

    private static void FillPixelBlock(
        int[] pixels,
        int width,
        int height,
        int left,
        int top,
        int size,
        int color)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + size); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(width, left + size); x++)
            {
                pixels[checked(y * width + x)] = color;
            }
        }
    }

    private static void DrawTangentialElectrodePad(
        int[] pixels,
        int width,
        int height,
        (double X, double Y) center,
        double radialX,
        double radialY,
        double tangentialSize,
        double radialSize,
        int color)
    {
        var halfTangential = tangentialSize / 2.0;
        var halfRadial = radialSize / 2.0;
        var searchRadius = (int)Math.Ceiling(Math.Sqrt((halfTangential * halfTangential) + (halfRadial * halfRadial))) + 1;
        var minX = Math.Max(0, (int)Math.Floor(center.X - searchRadius));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(center.X + searchRadius));
        var minY = Math.Max(0, (int)Math.Floor(center.Y - searchRadius));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(center.Y + searchRadius));
        var tangentX = -radialY;
        var tangentY = radialX;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = (x + 0.5) - center.X;
                var dy = (y + 0.5) - center.Y;
                var radialDistance = (dx * radialX) + (dy * radialY);
                var tangentialDistance = (dx * tangentX) + (dy * tangentY);
                if (Math.Abs(radialDistance) <= halfRadial && Math.Abs(tangentialDistance) <= halfTangential)
                {
                    pixels[checked(y * width + x)] = color;
                }
            }
        }
    }

    internal sealed class RealtimeImageRasterCache
    {
        private const int BackgroundColor = unchecked((int)0xFFF8FAFC);
        private int[]? cellByPixel;
        private float[]? barycentricWeight0ByPixel;
        private float[]? barycentricWeight1ByPixel;
        private int[]? node0ByCell;
        private int[]? node1ByCell;
        private int[]? node2ByCell;
        private double[]? cellAreaWeights;
        private double[]? nodeValueBuffer;
        private double[]? nodeWeightBuffer;
        private int rasterNodeCount;
        private int[]? frameBuffer;
        private ulong meshSignature;
        private int rasterPixelSize;
        private readonly object renderGate = new();
        private int colorScaleResetPending;
        private readonly RealtimeImageColorScaleTracker colorScale = new();

        public int RasterBuildCount { get; private set; }

        public int Parallelism { get; } = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

        public ImageSource Render(
            RealtimeReconstructionResult result,
            string imagePolarity,
            double imageGain,
            IReadOnlyList<ElectrodeContactState>? electrodeStates,
            int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
        {
            return RenderWithPresentation(
                result,
                imagePolarity,
                imageGain,
                electrodeStates,
                imagePixelSize).Image;
        }

        public RealtimeRenderedImage RenderWithPresentation(
            RealtimeReconstructionResult result,
            string imagePolarity,
            double imageGain,
            IReadOnlyList<ElectrodeContactState>? electrodeStates,
            int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
        {
            lock (renderGate)
            {
                return RenderCore(result, imagePolarity, imageGain, electrodeStates, imagePixelSize);
            }
        }

        public ImageSource RenderWithPersistedPresentation(
            RealtimeReconstructionResult result,
            string imagePolarity,
            double imageGain,
            double scaleCenter,
            double scaleRange,
            IReadOnlyList<ElectrodeContactState>? electrodeStates,
            int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
        {
            if (!double.IsFinite(scaleCenter) || !double.IsFinite(scaleRange) || scaleRange <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(scaleRange), "Persisted image color scale is invalid.");
            }

            lock (renderGate)
            {
                return RenderCore(
                    result,
                    imagePolarity,
                    imageGain,
                    electrodeStates,
                    imagePixelSize,
                    scaleCenter,
                    scaleRange).Image;
            }
        }

        private RealtimeRenderedImage RenderCore(
            RealtimeReconstructionResult result,
            string imagePolarity,
            double imageGain,
            IReadOnlyList<ElectrodeContactState>? electrodeStates,
            int imagePixelSize,
            double? persistedScaleCenter = null,
            double? persistedScaleRange = null)
        {
            ApplyPendingColorScaleReset();
            var meshIndexMetadata = result.GetMeshIndexMetadata();
            meshIndexMetadata.ValidateForResult(
                result.NodeCoords,
                result.CellConnectivity,
                result.Conductivity.Length,
                requireCanonical: false);
            var edge = VisualizationGeometry.ClampImagePixelSize(imagePixelSize);
            EnsureRaster(result.NodeCoords, result.CellConnectivity, edge);
            var pixels = RentFrameBuffer(edge);
            Array.Fill(pixels, BackgroundColor);
            RealtimeImageColorScaleSnapshot? appliedScale = null;
            if (cellByPixel is not null && result.Conductivity.Length > 0)
            {
                var scale = persistedScaleCenter is { } center && persistedScaleRange is { } range
                    ? new RealtimeImageColorScaleSnapshot(center, range, range, true)
                    : colorScale.Update(result.Conductivity);
                appliedScale = scale;
                var appliedCenter = scale.Center;
                var appliedRange = scale.Range;

                var invert = NormalizeRealtimeImagePolarity(imagePolarity) == "inverted";
                var gain = Math.Clamp(imageGain, 0.1, 5.0);
                var nodeValues = string.Equals(
                    meshIndexMetadata.ParameterEntity,
                    ReconstructionParameterEntity.Node,
                    StringComparison.Ordinal)
                    ? result.Conductivity
                    : ProjectCellValuesToNodes(result.Conductivity);
                var rasterCells = cellByPixel;
                var weight0ByPixel = barycentricWeight0ByPixel;
                var weight1ByPixel = barycentricWeight1ByPixel;
                var cellNode0 = node0ByCell;
                var cellNode1 = node1ByCell;
                var cellNode2 = node2ByCell;

                if (weight0ByPixel is not null
                    && weight1ByPixel is not null
                    && cellNode0 is not null
                    && cellNode1 is not null
                    && cellNode2 is not null)
                {
                    Parallel.For(
                        0,
                        edge,
                        new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
                        y =>
                        {
                            var rowOffset = y * edge;
                            for (var x = 0; x < edge; x++)
                            {
                                var pixelIndex = rowOffset + x;
                                var cell = rasterCells[pixelIndex];
                                if ((uint)cell >= (uint)cellNode0.Length)
                                {
                                    continue;
                                }

                                var node0 = cellNode0[cell];
                                var node1 = cellNode1[cell];
                                var node2 = cellNode2[cell];
                                if ((uint)node0 >= (uint)nodeValues.Length
                                    || (uint)node1 >= (uint)nodeValues.Length
                                    || (uint)node2 >= (uint)nodeValues.Length)
                                {
                                    continue;
                                }

                                var value0 = nodeValues[node0];
                                var value1 = nodeValues[node1];
                                var value2 = nodeValues[node2];
                                if (!double.IsFinite(value0)
                                    || !double.IsFinite(value1)
                                    || !double.IsFinite(value2))
                                {
                                    continue;
                                }

                                var weight0 = weight0ByPixel[pixelIndex];
                                var weight1 = weight1ByPixel[pixelIndex];
                                var weight2 = 1.0 - weight0 - weight1;
                                var value = (value0 * weight0) + (value1 * weight1) + (value2 * weight2);
                                pixels[pixelIndex] = ColorFor(value, appliedCenter, appliedRange, invert, gain);
                            }
                        });
                }
            }

            DrawCircle(pixels, edge, edge, unchecked((int)0xFF334155));
            DrawPeripheralElectrodes(
                pixels,
                edge,
                edge,
                electrodeStates);
            return new RealtimeRenderedImage(CreateBitmap(pixels, edge, edge), appliedScale);
        }

        public ImageSource RenderNeutral(
            IReadOnlyList<ElectrodeContactState>? electrodeStates,
            int imagePixelSize = VisualizationGeometry.DefaultImagePixelSize)
        {
            lock (renderGate)
            {
                return RenderNeutralCore(electrodeStates, imagePixelSize);
            }
        }

        private ImageSource RenderNeutralCore(
            IReadOnlyList<ElectrodeContactState>? electrodeStates,
            int imagePixelSize)
        {
            ApplyPendingColorScaleReset();
            var edge = VisualizationGeometry.ClampImagePixelSize(imagePixelSize);
            var pixels = RentFrameBuffer(edge);
            Array.Fill(pixels, BackgroundColor);
            DrawCircle(pixels, edge, edge, unchecked((int)0xFF334155));
            DrawPeripheralElectrodes(pixels, edge, edge, electrodeStates);
            return CreateBitmap(pixels, edge, edge);
        }

        /// <summary>
        /// Reuses one frame buffer across renders. At the maximum edge a fresh buffer would be
        /// several megabytes per frame, which the realtime path produces continuously.
        /// <see cref="CreateBitmap"/> copies into the bitmap, so reuse is safe.
        /// </summary>
        private int[] RentFrameBuffer(int edge)
        {
            var required = checked(edge * edge);
            if (frameBuffer is null || frameBuffer.Length != required)
            {
                frameBuffer = new int[required];
            }

            return frameBuffer;
        }

        public void ResetColorScale()
        {
            Interlocked.Exchange(ref colorScaleResetPending, 1);
        }

        private void ApplyPendingColorScaleReset()
        {
            if (Interlocked.Exchange(ref colorScaleResetPending, 0) != 0)
            {
                colorScale.Reset();
            }
        }

        private void EnsureRaster(double[,] nodeCoords, int[,] cellConnectivity, int edge)
        {
            var signature = ComputeMeshSignature(nodeCoords, cellConnectivity);
            // The pixel-to-cell map is resolution dependent, so a resize invalidates it just as a
            // mesh change does.
            if (cellByPixel is not null && signature == meshSignature && rasterPixelSize == edge)
            {
                return;
            }

            var raster = new int[checked(edge * edge)];
            Array.Fill(raster, -1);
            var barycentricWeight0 = new float[raster.Length];
            var barycentricWeight1 = new float[raster.Length];
            var nodeCount = nodeCoords.GetLength(0);
            var coordColumns = nodeCoords.GetLength(1);
            var cellCount = cellConnectivity.GetLength(0);
            var node0 = new int[cellCount];
            var node1 = new int[cellCount];
            var node2 = new int[cellCount];
            var areaWeights = new double[cellCount];
            Array.Fill(node0, -1);
            Array.Fill(node1, -1);
            Array.Fill(node2, -1);
            if (nodeCount > 0 && coordColumns > 0 && cellConnectivity.GetLength(1) >= 3)
            {
                var xs = new double[nodeCount];
                var ys = new double[nodeCount];
                for (var index = 0; index < nodeCount; index++)
                {
                    xs[index] = nodeCoords[index, 0];
                    ys[index] = nodeCoords[index, Math.Min(1, coordColumns - 1)];
                }

                var minX = xs.Min();
                var maxX = xs.Max();
                var minY = ys.Min();
                var maxY = ys.Max();
                var spanX = Math.Max(maxX - minX, 1.0e-12);
                var spanY = Math.Max(maxY - minY, 1.0e-12);
                for (var cell = 0; cell < cellCount; cell++)
                {
                    var a = cellConnectivity[cell, 0];
                    var b = cellConnectivity[cell, 1];
                    var c = cellConnectivity[cell, 2];
                    if (a < 0 || b < 0 || c < 0 || a >= nodeCount || b >= nodeCount || c >= nodeCount)
                    {
                        continue;
                    }

                    var modelArea = Math.Abs(Edge(
                        (xs[a], ys[a]),
                        (xs[b], ys[b]),
                        (xs[c], ys[c])));
                    if (modelArea <= 1.0e-18)
                    {
                        continue;
                    }

                    node0[cell] = a;
                    node1[cell] = b;
                    node2[cell] = c;
                    areaWeights[cell] = modelArea;
                    FillTriangle(
                        raster,
                        barycentricWeight0,
                        barycentricWeight1,
                        edge,
                        edge,
                        Transform(xs[a], ys[a], minX, minY, spanX, spanY, edge, edge),
                        Transform(xs[b], ys[b], minX, minY, spanX, spanY, edge, edge),
                        Transform(xs[c], ys[c], minX, minY, spanX, spanY, edge, edge),
                        cell);
                }
            }

            cellByPixel = raster;
            barycentricWeight0ByPixel = barycentricWeight0;
            barycentricWeight1ByPixel = barycentricWeight1;
            node0ByCell = node0;
            node1ByCell = node1;
            node2ByCell = node2;
            cellAreaWeights = areaWeights;
            rasterNodeCount = nodeCount;
            nodeValueBuffer = null;
            nodeWeightBuffer = null;
            meshSignature = signature;
            rasterPixelSize = edge;
            colorScale.Reset();
            RasterBuildCount++;
        }

        /// <summary>
        /// Converts the solver's piecewise-constant element values into a continuous display field.
        /// This is presentation-only: the persisted conductivity and every downstream calculation
        /// continue to use the original element values.
        /// </summary>
        private double[] ProjectCellValuesToNodes(double[] conductivity)
        {
            if (rasterNodeCount <= 0
                || node0ByCell is null
                || node1ByCell is null
                || node2ByCell is null
                || cellAreaWeights is null)
            {
                return [];
            }

            if (nodeValueBuffer is null || nodeValueBuffer.Length != rasterNodeCount)
            {
                nodeValueBuffer = new double[rasterNodeCount];
                nodeWeightBuffer = new double[rasterNodeCount];
            }

            var values = nodeValueBuffer;
            var weights = nodeWeightBuffer!;
            Array.Clear(values);
            Array.Clear(weights);

            var projectedCellCount = Math.Min(conductivity.Length, node0ByCell.Length);
            for (var cell = 0; cell < projectedCellCount; cell++)
            {
                var value = conductivity[cell];
                var areaWeight = cellAreaWeights[cell];
                var node0 = node0ByCell[cell];
                var node1 = node1ByCell[cell];
                var node2 = node2ByCell[cell];
                if (!double.IsFinite(value)
                    || !double.IsFinite(areaWeight)
                    || areaWeight <= 0.0
                    || node0 < 0
                    || node1 < 0
                    || node2 < 0)
                {
                    continue;
                }

                values[node0] += value * areaWeight;
                values[node1] += value * areaWeight;
                values[node2] += value * areaWeight;
                weights[node0] += areaWeight;
                weights[node1] += areaWeight;
                weights[node2] += areaWeight;
            }

            for (var node = 0; node < values.Length; node++)
            {
                values[node] = weights[node] > 0.0
                    ? values[node] / weights[node]
                    : double.NaN;
            }

            return values;
        }

        private static ulong ComputeMeshSignature(double[,] nodes, int[,] cells)
        {
            const ulong offset = 1469598103934665603UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            hash = (hash ^ (uint)nodes.GetLength(0)) * prime;
            hash = (hash ^ (uint)nodes.GetLength(1)) * prime;
            for (var row = 0; row < nodes.GetLength(0); row++)
            {
                for (var column = 0; column < nodes.GetLength(1); column++)
                {
                    hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(nodes[row, column])) * prime;
                }
            }

            hash = (hash ^ (uint)cells.GetLength(0)) * prime;
            hash = (hash ^ (uint)cells.GetLength(1)) * prime;
            for (var row = 0; row < cells.GetLength(0); row++)
            {
                for (var column = 0; column < cells.GetLength(1); column++)
                {
                    hash = (hash ^ (uint)cells[row, column]) * prime;
                }
            }

            return hash;
        }
    }

    internal sealed record RealtimeRenderedImage(
        ImageSource Image,
        RealtimeImageColorScaleSnapshot? ColorScale);

}
