using System.Threading;

namespace EitHost.Core.Storage.Hdf5;

/// <summary>
/// Serializes complete native HDF5 call sequences across raw readers/writers and
/// derived-stage writers. Handles may remain open between calls, but no two
/// threads may execute HDF.PInvoke concurrently.
/// </summary>
internal static class Hdf5NativeCallGate
{
    private static readonly object Sync = new();

    internal static bool IsEnteredByCurrentThread => Monitor.IsEntered(Sync);

    internal static IDisposable Enter()
    {
        Monitor.Enter(Sync);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Monitor.Exit(Sync);
            }
        }
    }
}
