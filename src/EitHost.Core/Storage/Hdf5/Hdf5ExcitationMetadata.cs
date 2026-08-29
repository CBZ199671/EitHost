using EitHost.Core.Hardware.Dds;

namespace EitHost.Core.Storage.Hdf5;

public sealed record Hdf5ExcitationMetadata(
    DdsDacSettings Dac,
    DdsExcitationSettings Excitation,
    byte PgaGain,
    DdsExecutionReceipt? Execution = null,
    DdsScanStatus? ScanStatus = null);
