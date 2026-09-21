namespace EitHost.Core.Reconstruction;

/// <summary>
/// Raised when a WSL distribution could not be brought up within the readiness budget,
/// so no bounded file probe or read was ever attempted against it.
/// </summary>
internal sealed class WslDistroNotReadyException : IOException
{
    internal WslDistroNotReadyException(string distroName, TimeSpan timeout, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        DistroName = distroName;
        Timeout = timeout;
    }

    internal string DistroName { get; }

    internal TimeSpan Timeout { get; }
}
