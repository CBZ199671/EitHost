namespace EitHost.Core.Storage.Catalog;

public sealed class DataRootLayout
{
    private const string CatalogFileName = "eit_catalog.sqlite";
    public const int DerivedBlocksPerShard = 3000;

    private DataRootLayout(
        string rootPath,
        string catalogPath,
        string currentFrameStorePath,
        string legacyApplicationDataDirectory)
    {
        RootPath = rootPath;
        CatalogPath = catalogPath;
        CurrentFrameStorePath = currentFrameStorePath;
        LegacyApplicationDataDirectory = legacyApplicationDataDirectory;
    }

    public string RootPath { get; }

    public string CatalogPath { get; }

    public string CurrentFrameStorePath { get; }

    public string LegacyApplicationDataDirectory { get; }

    public string BackendExchangeDirectory => ResolveArtifactPath(
        Path.Combine(".exchange", "pyeidors"));

    public static DataRootLayout Create(
        string? dataRootPath = null,
        string? catalogPath = null,
        DateTimeOffset? now = null,
        string? applicationBasePath = null,
        string? localApplicationDataPath = null)
    {
        var appBase = Path.GetFullPath(
            string.IsNullOrWhiteSpace(applicationBasePath)
                ? AppContext.BaseDirectory
                : applicationBasePath);
        var root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(dataRootPath)
                ? Path.Combine(appBase, "Data")
                : dataRootPath);
        var resolvedCatalog = Path.GetFullPath(
            string.IsNullOrWhiteSpace(catalogPath)
                ? Path.Combine(root, CatalogFileName)
                : catalogPath);
        EnsureContained(root, resolvedCatalog, nameof(catalogPath));

        var timestamp = now ?? DateTimeOffset.Now;
        var frameStore = Path.Combine(root, $"eit_frames_{timestamp:yyyyMMdd}.sqlite");
        var localAppData = string.IsNullOrWhiteSpace(localApplicationDataPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationDataPath;
        var legacyDirectory = Path.GetFullPath(Path.Combine(localAppData, "EitHost"));
        return new DataRootLayout(root, resolvedCatalog, frameStore, legacyDirectory);
    }

    public string GetRunRelativeDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        return Path.Combine("runs", startedAt.ToString("yyyyMMdd"), experimentRunId.ToString("N"));
    }

    public string GetRunDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        return ResolveArtifactPath(GetRunRelativeDirectory(experimentRunId, startedAt));
    }

    public string GetArchiveRelativeDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        return Path.Combine(
            "archives",
            startedAt.ToString("yyyyMMdd"),
            experimentRunId.ToString("N"));
    }

    public string GetArchiveDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        return ResolveArtifactPath(GetArchiveRelativeDirectory(experimentRunId, startedAt));
    }

    public string CreateTrashStagingDirectoryPath(Guid experimentRunId)
    {
        return ResolveArtifactPath(Path.Combine(
            ".trash",
            $"{experimentRunId:N}_{Guid.NewGuid():N}"));
    }

    public string EnsureRunDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        var path = GetRunDirectory(experimentRunId, startedAt);
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsureRawDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        var path = Path.Combine(EnsureRunDirectory(experimentRunId, startedAt), "raw");
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsureDerivedDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        var path = Path.Combine(EnsureRunDirectory(experimentRunId, startedAt), "derived");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetDerivedBlockPath(
        Guid experimentRunId,
        DateTimeOffset startedAt,
        int blockNumber)
    {
        return GetDerivedBlockPath(
            GetRunRelativeDirectory(experimentRunId, startedAt),
            blockNumber);
    }

    public string GetDerivedBlockPath(string runRelativeDirectory, int blockNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRelativeDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockNumber);
        var shard = (blockNumber - 1) / DerivedBlocksPerShard;
        return Path.Combine(
            ResolveArtifactPath(runRelativeDirectory),
            "derived",
            $"derived_shard_{shard:D6}.h5");
    }

    public static string GetDerivedBlockRoot(int blockNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockNumber);
        return $"/blocks/{blockNumber:D8}";
    }

    public static string GetDerivedDatasetPath(int blockNumber, string relativeDatasetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeDatasetPath);
        var normalized = relativeDatasetPath.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = $"/{normalized}";
        }

        return $"{GetDerivedBlockRoot(blockNumber)}{normalized}";
    }

    public static bool IsCanonicalDerivedShardPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("derived_shard_", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".h5", StringComparison.OrdinalIgnoreCase);
    }

    public string GetBackendExchangeDiagnosticPath(string runRelativeDirectory, int blockNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRelativeDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        var shard = blockNumber / 1000;
        return Path.Combine(
            ResolveArtifactPath(runRelativeDirectory),
            "diagnostics",
            "backend_exchange",
            shard.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            $"result_{blockNumber:D8}.h5");
    }

    public string GetRealtimeDiagnosticPath(string runRelativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRelativeDirectory);
        return Path.Combine(
            ResolveArtifactPath(runRelativeDirectory),
            "diagnostics",
            "realtime.log");
    }

    public string EnsureExportsDirectory(Guid experimentRunId, DateTimeOffset startedAt)
    {
        var path = Path.Combine(EnsureRunDirectory(experimentRunId, startedAt), "exports");
        Directory.CreateDirectory(path);
        return path;
    }

    public string ToRelativeArtifactPath(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        var fullPath = Path.GetFullPath(artifactPath);
        EnsureContained(RootPath, fullPath, nameof(artifactPath));
        return Path.GetRelativePath(RootPath, fullPath);
    }

    public string ResolveArtifactPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Artifact path must be relative to DataRoot.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        EnsureContained(RootPath, fullPath, nameof(relativePath));
        return fullPath;
    }

    public IReadOnlyList<string> EnumerateFrameStorePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExistingFrameStores(paths, RootPath);
        if (!string.Equals(RootPath, LegacyApplicationDataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AddExistingFrameStores(paths, LegacyApplicationDataDirectory);
        }

        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string LegacyCatalogPath => Path.Combine(LegacyApplicationDataDirectory, CatalogFileName);

    private static void AddExistingFrameStores(HashSet<string> paths, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "eit_frames*.sqlite", SearchOption.TopDirectoryOnly))
        {
            paths.Add(Path.GetFullPath(path));
        }
    }

    private static void EnsureContained(string rootPath, string candidatePath, string parameterName)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(candidatePath));
        if (Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path must stay inside DataRoot.", parameterName);
        }
    }
}
