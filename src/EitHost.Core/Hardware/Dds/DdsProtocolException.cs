namespace EitHost.Core.Hardware.Dds;

public sealed class DdsProtocolException : IOException
{
    public DdsProtocolException(string message)
        : base(message)
    {
    }
}

