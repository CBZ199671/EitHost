namespace EitHost.Core.Hardware.Dds;

public enum DdsResponseStatus : byte
{
    Ok = 0x00,
    BadLength = 0x01,
    BadChecksum = 0x02,
    Unsupported = 0x03,
    InvalidArgument = 0x04,
    TimerRange = 0x05,
    Busy = 0x06,
    InternalError = 0x07
}

