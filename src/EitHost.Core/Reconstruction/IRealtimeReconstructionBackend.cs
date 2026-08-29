namespace EitHost.Core.Reconstruction;

public interface IRealtimeReconstructionBackend : IAsyncDisposable, IDisposable
{
    Task<RealtimeReconstructionResult> ReconstructAsync(
        RealtimeReconstructionRequest request,
        CancellationToken cancellationToken = default);
}
