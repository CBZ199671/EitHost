namespace EitHost.Core.Reconstruction;

public sealed record Pseudo3dKrigingRequest(
    double[,] NodeCoords,
    int[,] CellConnectivity,
    double[,] LayerValues,
    double[] SourceZ,
    double[] DisplayZ,
    double[] LayerQuality,
    string ParameterEntity,
    double AxialRangeFactor = 1.0,
    double SpatialRangeFactor = 2.5,
    int NeighborhoodSize = 8,
    string MetadataJson = "{}");

public sealed record Pseudo3dKrigingResult(
    double[,] DisplayLayerValues,
    double[,] RelativeVariance,
    string MetadataJson,
    TimeSpan BackendElapsed);

public interface IPseudo3dKrigingBackend
{
    Task<Pseudo3dKrigingResult> InterpolatePseudo3dAsync(
        Pseudo3dKrigingRequest request,
        CancellationToken cancellationToken = default);
}
