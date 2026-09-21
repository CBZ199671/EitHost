using System.Text.Json;

namespace EitHost.Core.Reconstruction;

public sealed record WslPyEidorsBackendProfile(
    string ProfileName,
    string DisplayName,
    string Description,
    string PackageAttr,
    string WorkerLaunchCommand,
    string? DoctorCommand,
    bool RequiresGpu,
    bool RequiresAmgx)
{
    public bool RequiresCanonicalMeshIndex { get; init; }
}

public sealed record WslPyEidorsBackendManifestSnapshot(
    string? DefaultProfile,
    IReadOnlyList<WslPyEidorsBackendProfile> Profiles);

public static class WslPyEidorsBackendManifest
{
    public const string FileName = "pyeidors.backend.json";
    public const string CustomProfile = "custom";
    public const string LegacyCompatibilityProfile = "complex64";
    public const string LegacyFallbackWorkerLaunchCommand = "nix run .#eit-backend-worker-complex64 -- serve";
    public const string LegacyFallbackDoctorCommand = "nix run .#eit-backend-doctor-complex64 -- --profile complex64 --format json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static WslPyEidorsReconstructionOptions ApplyProfile(
        WslPyEidorsReconstructionOptions options,
        string? preferredProfile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profileName = string.IsNullOrWhiteSpace(preferredProfile) ? options.BackendProfile : preferredProfile;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new InvalidOperationException("请选择 PyEIDORS 后端路线。");
        }

        var snapshot = LoadSnapshotOrThrow(
            options.DistroName,
            options.BackendRepositoryPath,
            requireDefaultProfile: false);
        return ApplyProfile(options, snapshot, profileName);
    }

    public static WslPyEidorsReconstructionOptions ApplyProfile(
        WslPyEidorsReconstructionOptions options,
        WslPyEidorsBackendManifestSnapshot snapshot,
        string? preferredProfile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Profiles);
        var profileName = string.IsNullOrWhiteSpace(preferredProfile) ? options.BackendProfile : preferredProfile;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new InvalidOperationException("请选择 PyEIDORS 后端路线。");
        }

        var profile = snapshot.Profiles.FirstOrDefault(candidate => string.Equals(
            candidate.ProfileName,
            profileName.Trim(),
            StringComparison.Ordinal));
        if (profile is null)
        {
            throw PyEidorsReconstructionException.FromFrontendConfiguration(
                ResolveManifestPath(options.DistroName, options.BackendRepositoryPath),
                "BackendManifestProfileNotFound",
                $"PyEIDORS 后端清单中不存在路线 '{profileName}'，请重新选择后端目录或路线。");
        }

        return ApplyProfile(options, profile);
    }

    public static WslPyEidorsReconstructionOptions ResolveConfiguredOrDefault(
        WslPyEidorsReconstructionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedOptions = RemoveLegacyImplicitFallback(options);
        if (normalizedOptions != options)
        {
            _ = LoadProfilesOrThrow(
                options.DistroName,
                options.BackendRepositoryPath);
        }

        options = normalizedOptions;
        if (!string.IsNullOrWhiteSpace(options.BackendProfile))
        {
            if (string.Equals(options.BackendProfile, CustomProfile, StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(options.WorkerLaunchCommand) || options.UseNixDevelop))
            {
                return options;
            }

            return ApplyProfile(options, options.BackendProfile);
        }

        if (!string.IsNullOrWhiteSpace(options.WorkerLaunchCommand) || options.UseNixDevelop)
        {
            return options with { BackendProfile = CustomProfile };
        }

        return ApplyProfileIfManifestExists(options);
    }

    public static WslPyEidorsReconstructionOptions ResolveConfiguredOrDefault(
        WslPyEidorsReconstructionOptions options,
        WslPyEidorsBackendManifestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Profiles);
        options = RemoveLegacyImplicitFallback(options);
        if (!string.IsNullOrWhiteSpace(options.BackendProfile))
        {
            if (string.Equals(options.BackendProfile, CustomProfile, StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(options.WorkerLaunchCommand) || options.UseNixDevelop))
            {
                return options;
            }

            return ApplyProfile(options, snapshot, options.BackendProfile);
        }

        if (!string.IsNullOrWhiteSpace(options.WorkerLaunchCommand) || options.UseNixDevelop)
        {
            return options with { BackendProfile = CustomProfile };
        }

        return string.IsNullOrWhiteSpace(snapshot.DefaultProfile)
            ? options
            : ApplyProfile(options, snapshot, snapshot.DefaultProfile);
    }

    public static WslPyEidorsReconstructionOptions ApplyProfileIfManifestExists(
        WslPyEidorsReconstructionOptions options,
        string? preferredProfile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profileName = string.IsNullOrWhiteSpace(preferredProfile) ? options.BackendProfile : preferredProfile;
        return TryLoadProfile(options.DistroName, options.BackendRepositoryPath, profileName, out var profile)
            ? ApplyProfile(options, profile)
            : options;
    }

    public static IReadOnlyList<WslPyEidorsBackendProfile> LoadProfiles(
        string distroName,
        string backendRepositoryPath)
    {
        return TryLoadProfiles(distroName, backendRepositoryPath, out var profiles) && profiles.Count > 0
            ? profiles
            : [];
    }

    public static IReadOnlyList<WslPyEidorsBackendProfile> LoadProfilesOrThrow(
        string distroName,
        string backendRepositoryPath,
        bool requireDefaultProfile = true) =>
        LoadSnapshotOrThrow(distroName, backendRepositoryPath, requireDefaultProfile).Profiles;

    public static WslPyEidorsBackendManifestSnapshot LoadSnapshotOrThrow(
        string distroName,
        string backendRepositoryPath,
        bool requireDefaultProfile = true) =>
        LoadSnapshotOrThrow(
            distroName,
            backendRepositoryPath,
            requireDefaultProfile,
            static path => File.ReadAllText(path),
            static (distro, path) => WslTextFileReader.ReadAllText(distro, path),
            static (distro, path) => WslTextFileReader.FileExists(distro, path));

    internal static WslPyEidorsBackendManifestSnapshot LoadSnapshotOrThrow(
        string distroName,
        string backendRepositoryPath,
        bool requireDefaultProfile,
        Func<string, string> readHostText,
        Func<string, string, string> readWslText,
        Func<string, string, bool> probeWslFile)
    {
        ArgumentNullException.ThrowIfNull(readHostText);
        ArgumentNullException.ThrowIfNull(readWslText);
        ArgumentNullException.ThrowIfNull(probeWslFile);
        var manifestPath = FileName;
        try
        {
            if (string.IsNullOrWhiteSpace(distroName)
                || string.IsNullOrWhiteSpace(backendRepositoryPath))
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestPathInvalid",
                    "WSL 发行版和 PyEIDORS 后端安装根目录不能为空。");
            }

            manifestPath = Path.Combine(backendRepositoryPath.Trim(), FileName);
            manifestPath = ResolveManifestPath(distroName, backendRepositoryPath);
            var isWslManifest = TryResolveWslManifestLocation(
                distroName,
                backendRepositoryPath,
                out var resolvedDistro,
                out var linuxManifestPath);
            string manifestText;
            try
            {
                manifestText = ReadManifestText(
                    distroName,
                    backendRepositoryPath,
                    manifestPath,
                    readHostText,
                    readWslText);
            }
            catch (Exception ex) when (!isWslManifest
                && ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestNotFound",
                    "所选目录中不存在 pyeidors.backend.json；请选择 PyEIDORS 稳定软件根目录。",
                    ex);
            }
            catch (WslDistroNotReadyException ex) when (isWslManifest)
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestWslDistroNotReady",
                    $"WSL 发行版 '{resolvedDistro}' 在 {ex.Timeout.TotalSeconds:0.###} 秒内未就绪，"
                    + "无法读取 pyeidors.backend.json。请确认该发行版能够启动"
                    + $"（可先执行 wsl -d {resolvedDistro} -- true 预热）；"
                    + "若其 ext4.vhdx 位于机械硬盘，冷启动可能超出就绪预算，建议迁至固态硬盘后重试。"
                    + $" 原因：{ex.Message}",
                    ex);
            }
            catch (IOException ex) when (isWslManifest)
            {
                bool manifestExists;
                try
                {
                    manifestExists = probeWslFile(resolvedDistro, linuxManifestPath);
                }
                catch (Exception probeException) when (probeException is IOException
                    or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                    or NotSupportedException)
                {
                    var combined = new AggregateException(ex, probeException);
                    throw PyEidorsReconstructionException.FromFrontendConfiguration(
                        manifestPath,
                        "BackendManifestWslReadFailed",
                        $"无法在 WSL 发行版 '{resolvedDistro}' 中读取 '{linuxManifestPath}'，"
                        + $"且存在性检查失败。读取原因：{ex.Message} 检查原因：{probeException.Message}",
                        combined);
                }

                if (manifestExists)
                {
                    throw PyEidorsReconstructionException.FromFrontendConfiguration(
                        manifestPath,
                        "BackendManifestWslReadFailed",
                        $"无法在 WSL 发行版 '{resolvedDistro}' 中读取 '{linuxManifestPath}'。"
                        + "请检查 current 链接、文件读取权限、UTF-8 编码和 1 MiB 大小限制。"
                        + $" 原因：{ex.Message}",
                        ex);
                }

                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestNotFound",
                    $"所选目录中不存在 pyeidors.backend.json；请确认 WSL 发行版 '{resolvedDistro}'"
                    + $" 和清单路径 '{linuxManifestPath}'。",
                    ex);
            }

            using var document = JsonDocument.Parse(
                manifestText,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = JsonOptions.AllowTrailingCommas,
                    CommentHandling = JsonCommentHandling.Skip
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("profiles", out var profileElements)
                || profileElements.ValueKind != JsonValueKind.Object)
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestContractError",
                    "pyeidors.backend.json 缺少 profiles 对象。");
            }

            if (root.TryGetProperty("requiresCanonicalMeshIndex", out var canonicalRequirement)
                && canonicalRequirement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestContractError",
                    "pyeidors.backend.json 的 requiresCanonicalMeshIndex 必须是布尔值。");
            }

            var requiresCanonicalMeshIndex = canonicalRequirement.ValueKind == JsonValueKind.True;

            var profiles = new List<WslPyEidorsBackendProfile>();
            foreach (var property in profileElements.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object
                    || !HasValidOptionalBoolean(property.Value, "requiresGpu")
                    || !HasValidOptionalBoolean(property.Value, "requiresAmgx")
                    || !TryReadProfile(property.Name, property.Value, out var profile)
                    || (profile.RequiresAmgx && !profile.RequiresGpu))
                {
                    throw PyEidorsReconstructionException.FromFrontendConfiguration(
                        manifestPath,
                        "BackendManifestContractError",
                        $"pyeidors.backend.json 的 profile '{property.Name}' 字段无效；"
                        + "workerLaunchCommand 必须非空，GPU/AMGX 标志必须是布尔值，且 AMGX 必须同时要求 GPU。");
                }

                profiles.Add(profile with
                {
                    RequiresCanonicalMeshIndex = requiresCanonicalMeshIndex
                });
            }

            var defaultProfile = ReadString(root, "defaultProfile");
            if (profiles.Count == 0
                || (requireDefaultProfile && string.IsNullOrWhiteSpace(defaultProfile))
                || (!string.IsNullOrWhiteSpace(defaultProfile)
                    && !profiles.Any(profile => string.Equals(
                    profile.ProfileName,
                    defaultProfile,
                    StringComparison.Ordinal))))
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestContractError",
                    "pyeidors.backend.json 没有可用 profile，或 defaultProfile 未指向有效 profile。");
            }

            return new WslPyEidorsBackendManifestSnapshot(
                defaultProfile,
                Array.AsReadOnly(profiles.ToArray()));
        }
        catch (PyEidorsReconstructionException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw PyEidorsReconstructionException.FromFrontendConfiguration(
                manifestPath,
                "BackendManifestJsonError",
                ex.Message,
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw PyEidorsReconstructionException.FromFrontendConfiguration(
                manifestPath,
                ex.GetType().Name,
                ex.Message,
                ex);
        }
    }

    public static bool TryLoadProfiles(
        string distroName,
        string backendRepositoryPath,
        out IReadOnlyList<WslPyEidorsBackendProfile> profiles)
    {
        profiles = [];
        if (!TryReadManifestDocument(distroName, backendRepositoryPath, out var document))
        {
            return false;
        }

        try
        {
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var requiresCanonicalMeshIndex = ReadBoolean(
                    root,
                    "requiresCanonicalMeshIndex");
                if (!root.TryGetProperty("profiles", out var profileElements)
                    || profileElements.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var parsed = new List<WslPyEidorsBackendProfile>();
                foreach (var property in profileElements.EnumerateObject())
                {
                    if (TryReadProfile(property.Name, property.Value, out var profile))
                    {
                        parsed.Add(profile with
                        {
                            RequiresCanonicalMeshIndex = requiresCanonicalMeshIndex
                        });
                    }
                }

                profiles = parsed;
                return profiles.Count > 0;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            profiles = [];
            return false;
        }
    }

    public static bool TryLoadProfile(
        string distroName,
        string backendRepositoryPath,
        string? preferredProfile,
        out WslPyEidorsBackendProfile profile)
    {
        profile = null!;
        if (!TryReadManifestDocument(distroName, backendRepositoryPath, out var document))
        {
            return false;
        }

        try
        {
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var profileName = string.IsNullOrWhiteSpace(preferredProfile)
                    ? ReadString(root, "defaultProfile")
                    : preferredProfile.Trim();
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    return false;
                }

                if (!root.TryGetProperty("profiles", out var profiles)
                    || profiles.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!profiles.TryGetProperty(profileName, out var profileElement))
                {
                    return false;
                }

                if (!TryReadProfile(profileName, profileElement, out profile))
                {
                    return false;
                }

                profile = profile with
                {
                    RequiresCanonicalMeshIndex = ReadBoolean(root, "requiresCanonicalMeshIndex")
                };
                return true;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            profile = null!;
            return false;
        }
    }

    private static bool TryReadManifestDocument(
        string distroName,
        string backendRepositoryPath,
        out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(backendRepositoryPath))
        {
            return false;
        }

        try
        {
            var manifestPath = ResolveManifestPath(distroName, backendRepositoryPath);
            document = JsonDocument.Parse(ReadManifestText(
                distroName,
                backendRepositoryPath,
                manifestPath,
                static path => File.ReadAllText(path),
                static (distro, path) => WslTextFileReader.ReadAllText(distro, path)), new JsonDocumentOptions
                {
                    AllowTrailingCommas = JsonOptions.AllowTrailingCommas,
                    CommentHandling = JsonCommentHandling.Skip
                });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            document?.Dispose();
            document = null!;
            return false;
        }
    }

    private static bool TryReadProfile(
        string profileName,
        JsonElement profileElement,
        out WslPyEidorsBackendProfile profile)
    {
        profile = null!;
        if (profileElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var workerLaunchCommand = ReadString(profileElement, "workerLaunchCommand");
        var doctorCommand = ReadString(profileElement, "doctorCommand");
        if (string.IsNullOrWhiteSpace(workerLaunchCommand))
        {
            return false;
        }

        profile = new WslPyEidorsBackendProfile(
            profileName,
            ReadString(profileElement, "displayName") ?? profileName,
            ReadString(profileElement, "description") ?? string.Empty,
            ReadString(profileElement, "packageAttr") ?? string.Empty,
            workerLaunchCommand,
            doctorCommand,
            ReadBoolean(profileElement, "requiresGpu"),
            ReadBoolean(profileElement, "requiresAmgx"));
        return true;
    }

    private static string ResolveManifestPath(string distroName, string backendRepositoryPath)
    {
        var configuredPath = backendRepositoryPath.Trim();
        if (TryResolveWslManifestLocation(
                distroName,
                configuredPath,
                out var resolvedDistro,
                out var linuxManifestPath))
        {
            return WslPathMapper.ToWslUncPath(resolvedDistro, linuxManifestPath);
        }

        if (Path.IsPathFullyQualified(configuredPath))
        {
            return Path.Combine(configuredPath, FileName);
        }

        var relativeLinuxManifestPath = configuredPath.Replace('\\', '/').TrimEnd('/') + "/" + FileName;
        return WslPathMapper.ToWslUncPath(distroName, relativeLinuxManifestPath);
    }

    private static string ReadManifestText(
        string distroName,
        string backendRepositoryPath,
        string manifestPath,
        Func<string, string> readHostText,
        Func<string, string, string> readWslText)
    {
        if (!TryResolveWslManifestLocation(
                distroName,
                backendRepositoryPath,
                out var resolvedDistro,
                out var linuxManifestPath))
        {
            return readHostText(manifestPath);
        }

        return readWslText(resolvedDistro, linuxManifestPath);
    }

    private static bool TryResolveWslManifestLocation(
        string distroName,
        string backendRepositoryPath,
        out string resolvedDistro,
        out string linuxManifestPath)
    {
        var configuredPath = backendRepositoryPath.Trim();
        string linuxRepositoryPath;
        if (WslPathMapper.TryParseWslUncPath(
                configuredPath,
                out var selectedDistro,
                out var selectedLinuxPath))
        {
            resolvedDistro = selectedDistro;
            linuxRepositoryPath = selectedLinuxPath;
        }
        else if (configuredPath.StartsWith("/", StringComparison.Ordinal))
        {
            resolvedDistro = distroName.Trim();
            linuxRepositoryPath = configuredPath.Replace('\\', '/');
        }
        else
        {
            resolvedDistro = string.Empty;
            linuxManifestPath = string.Empty;
            return false;
        }

        linuxManifestPath = linuxRepositoryPath.TrimEnd('/') + "/" + FileName;
        return true;
    }

    private static WslPyEidorsReconstructionOptions ApplyProfile(
        WslPyEidorsReconstructionOptions options,
        WslPyEidorsBackendProfile profile)
    {
        return options with
        {
            BackendProfile = profile.ProfileName,
            BackendRequiresGpu = profile.RequiresGpu,
            BackendRequiresAmgx = profile.RequiresAmgx,
            BackendRequiresCanonicalMeshIndex = profile.RequiresCanonicalMeshIndex,
            UseNixDevelop = false,
            WorkerLaunchCommand = profile.WorkerLaunchCommand,
            DoctorCommand = profile.DoctorCommand
        };
    }

    private static WslPyEidorsReconstructionOptions RemoveLegacyImplicitFallback(
        WslPyEidorsReconstructionOptions options)
    {
        var profileOnlyDefault = string.IsNullOrWhiteSpace(options.WorkerLaunchCommand)
            && string.IsNullOrWhiteSpace(options.DoctorCommand);
        var synthesizedFallback = string.Equals(
                options.WorkerLaunchCommand,
                LegacyFallbackWorkerLaunchCommand,
                StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(options.DoctorCommand)
                || string.Equals(options.DoctorCommand, LegacyFallbackDoctorCommand, StringComparison.Ordinal));
        return string.Equals(options.BackendProfile, LegacyCompatibilityProfile, StringComparison.Ordinal)
            && (profileOnlyDefault || synthesizedFallback)
            ? options with
            {
                BackendProfile = string.Empty,
                BackendRequiresGpu = false,
                BackendRequiresAmgx = false,
                BackendRequiresCanonicalMeshIndex = false,
                WorkerLaunchCommand = null,
                DoctorCommand = null
            }
            : options;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static bool HasValidOptionalBoolean(JsonElement element, string propertyName)
    {
        return !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
}
