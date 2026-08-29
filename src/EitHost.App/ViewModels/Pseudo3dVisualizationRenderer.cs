using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels;

internal static class Pseudo3dVisualizationRenderer
{
    private const int BackgroundColor = unchecked((int)0xFFF8FAFC);
    private const int OutlineColor = unchecked((int)0xB8475569);
    private const int ConnectorColor = unchecked((int)0x90475569);

    internal static ImageSource Render(LayeredPseudo3dVolume volume, int pixelSize = 512)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var edge = Math.Clamp(pixelSize, 192, 1024);
        var pixels = new int[checked(edge * edge)];
        Array.Fill(pixels, BackgroundColor);

        var nodes = volume.SourceNodeCoords2d;
        var triangles = volume.SourceTriangleConnectivity;
        var values = volume.DisplayLayerTriangleConductivity;
        ValidatePayload(nodes, triangles, values, volume.DisplayLayerCount);

        var bounds = FindBounds(nodes);
        var colorScale = FindColorScale(values);
        DrawLayerConnectors(pixels, edge, bounds);
        for (var layer = 0; layer < volume.DisplayLayerCount; layer++)
        {
            var zFraction = volume.DisplayLayerCount == 1
                ? 0.0
                : (double)layer / (volume.DisplayLayerCount - 1);
            for (var triangle = 0; triangle < triangles.GetLength(0); triangle++)
            {
                var p0 = Project(nodes, triangles[triangle, 0], zFraction, bounds, edge);
                var p1 = Project(nodes, triangles[triangle, 1], zFraction, bounds, edge);
                var p2 = Project(nodes, triangles[triangle, 2], zFraction, bounds, edge);
                var color = ColorFor(values[layer, triangle], colorScale.Center, colorScale.Range);
                FillTriangle(pixels, edge, p0, p1, p2, color, alpha: 174);
            }

            DrawLayerOutline(
                pixels,
                edge,
                bounds,
                zFraction,
                layer == 0 || layer == volume.DisplayLayerCount - 1 ? (byte)210 : (byte)120);
        }

        var bitmap = BitmapSource.Create(
            edge,
            edge,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            checked(edge * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static void ValidatePayload(
        double[,] nodes,
        int[,] triangles,
        double[,] values,
        int displayLayerCount)
    {
        if (nodes.GetLength(0) == 0 || nodes.GetLength(1) != 2)
        {
            throw new InvalidDataException("Pseudo-3D renderer requires two-dimensional source nodes.");
        }

        if (triangles.GetLength(0) == 0 || triangles.GetLength(1) != 3)
        {
            throw new InvalidDataException("Pseudo-3D renderer requires triangular source cells.");
        }

        if (values.GetLength(0) != displayLayerCount || values.GetLength(1) != triangles.GetLength(0))
        {
            throw new InvalidDataException("Pseudo-3D renderer layer conductivity shape does not match its mesh.");
        }
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) FindBounds(double[,] nodes)
    {
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
        for (var node = 0; node < nodes.GetLength(0); node++)
        {
            minX = Math.Min(minX, nodes[node, 0]);
            maxX = Math.Max(maxX, nodes[node, 0]);
            minY = Math.Min(minY, nodes[node, 1]);
            maxY = Math.Max(maxY, nodes[node, 1]);
        }

        if (Math.Abs(maxX - minX) < 1.0e-12)
        {
            minX -= 0.5;
            maxX += 0.5;
        }

        if (Math.Abs(maxY - minY) < 1.0e-12)
        {
            minY -= 0.5;
            maxY += 0.5;
        }

        return (minX, maxX, minY, maxY);
    }

    private static (double Center, double Range) FindColorScale(double[,] values)
    {
        var finite = new List<double>(values.Length);
        foreach (var value in values)
        {
            if (double.IsFinite(value))
            {
                finite.Add(value);
            }
        }

        finite.Sort();
        if (finite.Count == 0)
        {
            return (0.0, 1.0);
        }

        var middle = finite.Count / 2;
        var center = finite.Count % 2 == 0
            ? 0.5 * (finite[middle - 1] + finite[middle])
            : finite[middle];
        var range = finite.Max(value => Math.Abs(value - center));
        return (center, Math.Max(range, 1.0e-12));
    }

    private static (double X, double Y) Project(
        double[,] nodes,
        int nodeIndex,
        double zFraction,
        (double MinX, double MaxX, double MinY, double MaxY) bounds,
        int edge)
    {
        var x = (nodes[nodeIndex, 0] - bounds.MinX) / (bounds.MaxX - bounds.MinX);
        var y = (nodes[nodeIndex, 1] - bounds.MinY) / (bounds.MaxY - bounds.MinY);
        var drawable = edge * 0.82;
        var padding = edge * 0.09;
        return (
            padding + (x * drawable * 0.62) + (zFraction * drawable * 0.22),
            padding + ((1.0 - y) * drawable * 0.38) + ((1.0 - zFraction) * drawable * 0.42));
    }

    private static void DrawLayerConnectors(
        int[] pixels,
        int edge,
        (double MinX, double MaxX, double MinY, double MaxY) bounds)
    {
        var nodes = new double[,]
        {
            { bounds.MinX, (bounds.MinY + bounds.MaxY) * 0.5 },
            { bounds.MaxX, (bounds.MinY + bounds.MaxY) * 0.5 },
            { (bounds.MinX + bounds.MaxX) * 0.5, bounds.MinY },
            { (bounds.MinX + bounds.MaxX) * 0.5, bounds.MaxY }
        };
        var nodeBounds = (bounds.MinX, bounds.MaxX, bounds.MinY, bounds.MaxY);
        for (var node = 0; node < nodes.GetLength(0); node++)
        {
            DrawLine(
                pixels,
                edge,
                Project(nodes, node, 0.0, nodeBounds, edge),
                Project(nodes, node, 1.0, nodeBounds, edge),
                ConnectorColor,
                150);
        }
    }

    private static void DrawLayerOutline(
        int[] pixels,
        int edge,
        (double MinX, double MaxX, double MinY, double MaxY) bounds,
        double zFraction,
        byte alpha)
    {
        const int samples = 96;
        var outline = new double[samples, 2];
        var centerX = 0.5 * (bounds.MinX + bounds.MaxX);
        var centerY = 0.5 * (bounds.MinY + bounds.MaxY);
        var radiusX = 0.5 * (bounds.MaxX - bounds.MinX);
        var radiusY = 0.5 * (bounds.MaxY - bounds.MinY);
        for (var index = 0; index < samples; index++)
        {
            var angle = 2.0 * Math.PI * index / samples;
            outline[index, 0] = centerX + (Math.Cos(angle) * radiusX);
            outline[index, 1] = centerY + (Math.Sin(angle) * radiusY);
        }

        for (var index = 0; index < samples; index++)
        {
            DrawLine(
                pixels,
                edge,
                Project(outline, index, zFraction, bounds, edge),
                Project(outline, (index + 1) % samples, zFraction, bounds, edge),
                OutlineColor,
                alpha);
        }
    }

    private static void FillTriangle(
        int[] pixels,
        int edge,
        (double X, double Y) p0,
        (double X, double Y) p1,
        (double X, double Y) p2,
        int color,
        byte alpha)
    {
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        var maxX = Math.Min(edge - 1, (int)Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(edge - 1, (int)Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
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
                    BlendPixel(pixels, checked((y * edge) + x), color, alpha);
                }
            }
        }
    }

    private static void DrawLine(
        int[] pixels,
        int edge,
        (double X, double Y) start,
        (double X, double Y) end,
        int color,
        byte alpha)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y))));
        for (var step = 0; step <= steps; step++)
        {
            var fraction = (double)step / steps;
            var x = (int)Math.Round(start.X + ((end.X - start.X) * fraction));
            var y = (int)Math.Round(start.Y + ((end.Y - start.Y) * fraction));
            if (x >= 0 && x < edge && y >= 0 && y < edge)
            {
                BlendPixel(pixels, checked((y * edge) + x), color, alpha);
            }
        }
    }

    private static void BlendPixel(int[] pixels, int index, int foreground, byte alpha)
    {
        var background = pixels[index];
        var inverse = 255 - alpha;
        var red = ((((foreground >> 16) & 0xFF) * alpha) + (((background >> 16) & 0xFF) * inverse)) / 255;
        var green = ((((foreground >> 8) & 0xFF) * alpha) + (((background >> 8) & 0xFF) * inverse)) / 255;
        var blue = (((foreground & 0xFF) * alpha) + ((background & 0xFF) * inverse)) / 255;
        pixels[index] = unchecked((int)(0xFF000000u | (uint)(red << 16) | (uint)(green << 8) | (uint)blue));
    }

    private static int ColorFor(double value, double center, double range)
    {
        var t = Math.Clamp((value - center) / range, -1.0, 1.0);
        var magnitude = Math.Abs(t);
        var white = (R: 248, G: 250, B: 252);
        var cold = (R: 37, G: 99, B: 235);
        var hot = (R: 220, G: 38, B: 38);
        var target = t >= 0.0 ? hot : cold;
        var red = (byte)Math.Round(white.R + ((target.R - white.R) * magnitude));
        var green = (byte)Math.Round(white.G + ((target.G - white.G) * magnitude));
        var blue = (byte)Math.Round(white.B + ((target.B - white.B) * magnitude));
        return unchecked((int)(0xFF000000u | (uint)(red << 16) | (uint)(green << 8) | blue));
    }

    private static double Edge(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c) =>
        ((c.X - a.X) * (b.Y - a.Y)) - ((c.Y - a.Y) * (b.X - a.X));
}
