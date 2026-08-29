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
    bool RequiresAmgx);

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

        if (!TryLoadProfile(options.DistroName, options.BackendRepositoryPath, profileName, out var profile))
        {
            throw new InvalidOperationException(
                $"PyEIDORS 后端清单中不存在路线 '{profileName}'，请重新选择后端目录或路线。");
        }

        return ApplyProfile(options, profile);
    }

    public static WslPyEidorsReconstructionOptions ResolveConfiguredOrDefault(
        WslPyEidorsReconstructionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = RemoveLegacyImplicitFallback(options);
        if (!string.IsNullOrWhiteSpace(options.BackendProfile))
        {
            return ApplyProfileIfManifestExists(options, options.BackendProfile);
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
                    parsed.Add(profile);
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

            return TryReadProfile(profileName, profileElement, out profile);
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
}
