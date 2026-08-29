using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Storage.Catalog;

public sealed class ExperimentBackendExchangeArchiver
{
    public const string ArtifactKind = "backend_exchange";

    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;

    public ExperimentBackendExchangeArchiver(DataRootLayout layout, ExperimentCatalog catalog)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Archive(
        Guid experimentRunId,
        string runRelativeDirectory,
        int blockNumber,
        string sourceResultPath,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRelativeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceResultPath);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);

        var sourcePath = Path.GetFullPath(sourceResultPath);
        var sourceRelativePath = layout.ToRelativeArtifactPath(sourcePath);
        var exchangePrefix = Path.GetRelativePath(layout.RootPath, layout.BackendExchangeDirectory);
        if (!IsWithinRelativeDirectory(sourceRelativePath, exchangePrefix))
        {
            throw new ArgumentException(
                "Backend exchange result must be inside the DataRoot exchange staging directory.",
                nameof(sourceResultPath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Retained backend exchange result does not exist.", sourcePath);
        }

        var destinationPath = layout.GetBackendExchangeDiagnosticPath(runRelativeDirectory, blockNumber);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Backend diagnostic destination has no directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            AtomicFileCommitter.CopyWithRetry(sourcePath, temporaryPath, overwrite: false);
            AtomicFileCommitter.MoveWithRetry(temporaryPath, destinationPath, overwrite: true);
            catalog.RegisterDerivedArtifact(new DerivedArtifactCatalogRecord(
                experimentRunId,
                blockNumber,
                ArtifactKind,
                layout.ToRelativeArtifactPath(destinationPath),
                "/",
                createdAt));
            AtomicFileCommitter.DeleteBestEffort(sourcePath);
            return destinationPath;
        }
        catch (Exception ex) when (!AtomicFileCommitter.IsFatal(ex))
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
            throw new IOException(
                $"Backend diagnostic import failed; recoverable staging file: {sourcePath}",
                ex);
        }
    }

    private static bool IsWithinRelativeDirectory(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

}
