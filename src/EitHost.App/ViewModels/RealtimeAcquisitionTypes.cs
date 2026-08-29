using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels;

internal sealed record RealtimeDdsStartupResult(
    DdsSerialPortTransport Transport,
    DdsExecutionReceipt Execution,
    DdsFirmwareCapabilities Capabilities,
    string Status);

internal sealed record RealtimeRawPersistenceContext(
    PairingSummaryItem Pairing,
    Hdf5ExcitationMetadata Excitation,
    Usb2070AcquisitionMetadata Acquisition);
