using System.Diagnostics;

namespace EitHost.Core.Storage.Hdf5;

internal static class Hdf5ChildProcess
{
    // Opening HDF5 and clearing HANDLE_FLAG_INHERIT must finish before spawning
    // a redirected child. Hold the gate only across Start, never child I/O/wait.
    internal static Process? Start(ProcessStartInfo startInfo)
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        return Process.Start(startInfo);
    }

    internal static bool Start(Process process)
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        return process.Start();
    }
}
