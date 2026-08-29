namespace EitHost.Core.Sync;

public interface IEitSetSyncController
{
    string Label { get; }

    Task StartAcquisitionAsync(CancellationToken cancellationToken = default);

    Task StartExcitationAsync(CancellationToken cancellationToken = default);

    Task StopAcquisitionAsync(CancellationToken cancellationToken = default);

    Task StopExcitationAsync(CancellationToken cancellationToken = default);
}
