namespace EitHost.Core.Acquisition;

public sealed record BufferedAcquisitionAutoFlushResult(
    string Hdf5Path,
    int RowCount,
    long ValueCount)
{
    public Task Completion { get; init; } = Task.CompletedTask;
}
