using System.Globalization;
using System.Windows.Media;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels;

// Owned by the single pseudo composition consumer. BitmapSource.Create copies the
// scratch pixels; published frozen images never share the next frame's buffer.
internal sealed class Pseudo3dRenderSession
{
    private object? scaleKey;
    private (double Center, double Range) scale;
    private int[] pixels = [];
    internal (double Center, double Range) ColorScale => scale;

    internal ImageSource Render(LayeredPseudo3dVolume volume, object key, int edge = 512)
    {
        if (!Equals(scaleKey, key))
        {
            scale = Pseudo3dVisualizationRenderer.FindColorScale(volume.DisplayLayerTriangleConductivity);
            scaleKey = key;
        }
        edge = Math.Clamp(edge, 192, 1024);
        if (pixels.Length != edge * edge) pixels = new int[checked(edge * edge)];
        return Pseudo3dVisualizationRenderer.Render(volume, edge, scale, pixels);
    }

    internal string ScaleLabel(LayeredPseudo3dVolume volume) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{scale.Center - scale.Range:G4} ← {scale.Center:G4} → {scale.Center + scale.Range:G4}") +
        $" · {ReconstructionScale.ToDisplayLabel(volume.ReconstructionScaleStatus)} · 会话锁定色标";
}
