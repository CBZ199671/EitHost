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

        var profiles = LoadProfilesOrThrow(
            options.DistroName,
            options.BackendRepositoryPath,
            requireDefaultProfile: false);
        var profile = profiles.FirstOrDefault(candidate => string.Equals(
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
        bool requireDefaultProfile = true)
    {
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
            if (!File.Exists(manifestPath))
            {
                throw PyEidorsReconstructionException.FromFrontendConfiguration(
                    manifestPath,
                    "BackendManifestNotFound",
                    "所选目录中不存在 pyeidors.backend.json；请选择 ~/apps/PyEIDORS 稳定软件根目录。");
            }

            using var document = JsonDocument.Parse(
                File.ReadAllText(manifestPath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = JsonOptions.AllowTrailingCommas,
                    CommentHandling = JsonCommentHandling.Skip
                });
            var root = document.RootElement;
            if (!root.TryGetProperty("profiles", out var profileElements)
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

            return profiles;
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

        using (document)
        {
            var requiresCanonicalMeshIndex = ReadBoolean(
                document.RootElement,
                "requiresCanonicalMeshIndex");
            if (!document.RootElement.TryGetProperty("profiles", out var profileElements)
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

        using (document)
        {
            var root = document.RootElement;
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
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
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
        if (Path.IsPathFullyQualified(configuredPath) && Directory.Exists(configuredPath))
        {
            return Path.Combine(configuredPath, FileName);
        }

        var linuxPath = configuredPath.Replace('\\', '/');
        if (Path.IsPathFullyQualified(linuxPath))
        {
            linuxPath = WslPathMapper.ToWslPath(linuxPath);
        }

        var uncPath = WslPathMapper.ToWslUncPath(distroName, linuxPath);
        return Path.Combine(uncPath, FileName);
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
