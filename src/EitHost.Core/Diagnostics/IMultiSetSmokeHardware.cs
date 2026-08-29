using EitHost.Core.Hardware.Dds;
using EitHost.Core.Sync;

namespace EitHost.Core.Diagnostics;

public interface IMultiSetSmokeHardware
{
    Task<HardwareSmokeReport> CaptureHardwareReportAsync(CancellationToken cancellationToken = default);

    IMultiSetSmokeSetController CreateController(
        MultiSetSmokeSetPlan plan,
        MultiSetSmokeOptions options);
}

public interface IMultiSetSmokeSetController : IEitSetSyncController, IDisposable
{
    MultiSetSmokeSetPlan Plan { get; }

    SingleSetAdCapture ReadCapture(CancellationToken cancellationToken = default);

    SingleSetSmokeDdsCommand? StartExcitationCommand { get; }

    DdsExecutionReceipt? ExecutionReceipt { get; }

    SingleSetSmokeDdsCommand? StopExcitationCommand { get; }
}
