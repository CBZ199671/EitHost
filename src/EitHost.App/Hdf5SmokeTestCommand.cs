using System.IO;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App;

internal static class Hdf5SmokeTestCommand
{
    internal const string Option = "--hdf5-smoke-test";
    internal const string FailureFileName = "hdf5-smoke-test.failure.txt";

    internal static bool TryRun(IReadOnlyList<string> arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        exitCode = 0;
        if (arguments.Count == 0 ||
            !string.Equals(arguments[0], Option, StringComparison.Ordinal))
        {
            return false;
        }

        if (arguments.Count != 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            exitCode = 64;
            return true;
        }

        var probeDirectory = Path.GetFullPath(arguments[1]);
        var failurePath = Path.Combine(probeDirectory, FailureFileName);
        try
        {
            Directory.CreateDirectory(probeDirectory);
            File.Delete(failurePath);
            Hdf5RuntimeProbe.Verify(probeDirectory);
            exitCode = 0;
        }
        catch (Exception exception)
        {
            exitCode = 2;
            try
            {
                Directory.CreateDirectory(probeDirectory);
                File.WriteAllText(failurePath, exception.ToString());
            }
            catch (Exception reportException)
            {
                exception.Data["HDF5 smoke-test failure report"] = reportException.ToString();
            }
        }

        return true;
    }
}
