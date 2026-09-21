using System.IO;
using EitHost.Core.Storage.Hdf5;
using EitHost.Core.Storage.Catalog;
using PureHDF;

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
            VerifyDeepOfflineShard(probeDirectory);
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

    private static void VerifyDeepOfflineShard(string probeDirectory)
    {
        // The test runner opts into long paths; the shipped EXE does not. Exercise
        // native atomic creation at the actual offline path depth in this process.
        const string name = "offline_shard_000000.h5";
        var directory = Path.Combine(probeDirectory,
            new string('d', Math.Max(1, 225 - probeDirectory.Length - name.Length - 35)) +
            "-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, name);
        try
        {
            var runId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var writer = new DerivedArtifactHdf5Writer();
            foreach (var block in new[] { 1, 2 })
                writer.WriteReconstruction(path, new DerivedReconstructionData(
                    runId, block, block * 100, (block + 1) * 100, now, now,
                    [(double)block], null, null, null));
            using var file = Hdf5FileAccess.OpenReadWithRetry(path);
            foreach (var block in new[] { 1, 2 })
                if (!file.Dataset(DataRootLayout.GetDerivedDatasetPath(block, "/reconstruction/conductivity"))
                    .Read<double[]>().SequenceEqual([(double)block]))
                    throw new InvalidDataException("Deep offline HDF5 create/append verification failed.");
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
