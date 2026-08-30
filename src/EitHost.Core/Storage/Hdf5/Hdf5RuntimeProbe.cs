using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public static class Hdf5RuntimeProbe
{
    private const string CleanupFailureKey = "HDF5 runtime probe cleanup failure";

    public static void Verify(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(fullDirectoryPath);
        var filePath = Path.Combine(
            fullDirectoryPath,
            $".eithost-hdf5-probe-{Guid.NewGuid():N}.h5");
        Exception? primaryException = null;
        try
        {
            Hdf5IncrementalStageAppender.CreateMigrated(
                filePath,
                new H5File
                {
                    ["runtime_probe"] = new double[] { 1.0, 2.0, 3.0 }
                });

            var file = new FileInfo(filePath);
            if (!file.Exists || file.Length <= 0)
            {
                throw new IOException("HDF5 runtime probe did not create a non-empty file.");
            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception cleanupException) when (primaryException is not null)
            {
                primaryException.Data[CleanupFailureKey] = cleanupException.ToString();
            }
        }
    }
}
