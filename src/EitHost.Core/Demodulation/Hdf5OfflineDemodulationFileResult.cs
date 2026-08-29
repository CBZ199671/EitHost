namespace EitHost.Core.Demodulation;

public sealed record Hdf5OfflineDemodulationFileResult(
    Guid SourceRunId,
    string InputHdf5Path,
    string OutputHdf5Path,
    OfflineDemodulationResult Demodulation);
