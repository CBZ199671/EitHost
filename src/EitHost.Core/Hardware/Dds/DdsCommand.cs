namespace EitHost.Core.Hardware.Dds;

public enum DdsCommand : byte
{
    SetDac = 0x01,
    StopDac = 0x02,
    StartExcitation = 0x05,
    StopExcitation = 0x06,
    SetPga = 0x07,
    GetCapabilities = 0x08,
    GetScanStatus = 0x09
}
