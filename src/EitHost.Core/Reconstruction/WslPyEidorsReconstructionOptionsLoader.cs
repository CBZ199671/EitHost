using System.Text.Json;

namespace EitHost.Core.Reconstruction;

public static class WslPyEidorsReconstructionOptionsLoader
{
    public const string ConfigFileName = "eithost.reconstruction.json";
    public const string ConfigPathEnvironmentVariable = "EITHOST_RECONSTRUCTION_CONFIG";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions SaveJsonOptions = new(JsonOptions)
    {
        WriteIndented = true
    };

    public static WslPyEidorsReconstructionOptions Load(
        string? exchangeDirectory = null,
        string? baseDirectory = null,
        string? localAppDataDirectory = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var options = new WslPyEidorsReconstructionOptions(ExchangeDirectory: exchangeDirectory);
        foreach (var configPath in EnumerateConfigPaths(baseDirectory, localAppDataDirectory, getEnvironmentVariable))
        {
            if (!File.Exists(configPath))
            {
                continue;
            }

            options = ApplyConfig(options, ReadConfig(configPath));
        }

        return ApplyEnvironment(options, getEnvironmentVariable);
    }

    public static string GetUserConfigPath(string? localAppDataDirectory = null)
    {
        var localAppDataRoot = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataDirectory;
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            localAppDataRoot = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppDataRoot, "EitHost", ConfigFileName);
    }

    public static string SaveUserConfig(
        WslPyEidorsReconstructionOptions options,
        string? localAppDataDirectory = null,
        bool persistExchangeDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = GetUserConfigPath(localAppDataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var config = new WslPyEidorsReconstructionOptionsConfig
        {
            DistroName = options.DistroName,
            BackendRepositoryPath = options.BackendRepositoryPath,
            ExchangeDirectory = persistExchangeDirectory ? options.ExchangeDirectory : null,
            BackendProfile = options.BackendProfile,
            UseNixDevelop = options.UseNixDevelop,
            NixDevelopProfile = options.NixDevelopProfile,
            WorkerExecutable = options.WorkerExecutable,
            WorkerArguments = options.WorkerArguments,
            WorkerLaunchCommand = options.WorkerLaunchCommand,
            DoctorCommand = options.DoctorCommand
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config, SaveJsonOptions));
        return path;
    }

    private static IEnumerable<string> EnumerateConfigPaths(
        string? baseDirectory,
        string? localAppDataDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applicationDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        foreach (var path in CandidatePaths(applicationDirectory, localAppDataDirectory, getEnvironmentVariable))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    private static IEnumerable<string> CandidatePaths(
        string applicationDirectory,
        string? localAppDataDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        yield return Path.Combine(applicationDirectory, ConfigFileName);

        yield return GetUserConfigPath(localAppDataDirectory);

        var explicitConfig = getEnvironmentVariable(ConfigPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitConfig))
        {
            yield return explicitConfig;
        }
    }

    private static WslPyEidorsReconstructionOptionsConfig ReadConfig(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WslPyEidorsReconstructionOptionsConfig>(json, JsonOptions)
                ?? new WslPyEidorsReconstructionOptionsConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid PyEIDORS backend configuration JSON: {path}", ex);
        }
    }

    private static WslPyEidorsReconstructionOptions ApplyConfig(
        WslPyEidorsReconstructionOptions options,
        WslPyEidorsReconstructionOptionsConfig config)
    {
        return options with
        {
            DistroName = CoalesceRequired(config.DistroName, options.DistroName),
            BackendRepositoryPath = CoalesceRequired(config.BackendRepositoryPath, options.BackendRepositoryPath),
            ExchangeDirectory = CoalesceOptional(config.ExchangeDirectory, options.ExchangeDirectory),
            BackendProfile = CoalesceRequired(config.BackendProfile, options.BackendProfile),
            UseNixDevelop = config.UseNixDevelop ?? options.UseNixDevelop,
            NixDevelopProfile = CoalesceRequired(config.NixDevelopProfile, options.NixDevelopProfile),
            WorkerExecutable = CoalesceRequired(config.WorkerExecutable, options.WorkerExecutable),
            WorkerArguments = CoalesceRequired(config.WorkerArguments, options.WorkerArguments),
            WorkerLaunchCommand = CoalesceOptional(config.WorkerLaunchCommand, options.WorkerLaunchCommand),
            DoctorCommand = CoalesceOptional(config.DoctorCommand, options.DoctorCommand)
        };
    }

    private static WslPyEidorsReconstructionOptions ApplyEnvironment(
        WslPyEidorsReconstructionOptions options,
        Func<string, string?> getEnvironmentVariable)
    {
        var configured = options;
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_DISTRO", static (current, value) => current with { DistroName = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_WSL_DISTRO", static (current, value) => current with { DistroName = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_REPO_PATH", static (current, value) => current with { BackendRepositoryPath = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_BACKEND_REPOSITORY_PATH", static (current, value) => current with { BackendRepositoryPath = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_BACKEND_PATH", static (current, value) => current with { BackendRepositoryPath = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_BACKEND_PROFILE", static (current, value) => current with { BackendProfile = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_EXCHANGE_DIR", static (current, value) => current with { ExchangeDirectory = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_NIX_PROFILE", static (current, value) => current with { NixDevelopProfile = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_WORKER_EXECUTABLE", static (current, value) => current with { WorkerExecutable = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_WORKER_ARGUMENTS", static (current, value) => current with { WorkerArguments = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_WORKER_LAUNCH_COMMAND", static (current, value) => current with { WorkerLaunchCommand = value });
        configured = ApplyString(configured, getEnvironmentVariable, "EITHOST_PYEIDORS_DOCTOR_COMMAND", static (current, value) => current with { DoctorCommand = value });
        var useNixDevelop = getEnvironmentVariable("EITHOST_PYEIDORS_USE_NIX_DEVELOP");
        if (!string.IsNullOrWhiteSpace(useNixDevelop))
        {
            configured = configured with { UseNixDevelop = ParseBoolean(useNixDevelop, "EITHOST_PYEIDORS_USE_NIX_DEVELOP") };
        }

        return configured;
    }

    private static WslPyEidorsReconstructionOptions ApplyString(
        WslPyEidorsReconstructionOptions options,
        Func<string, string?> getEnvironmentVariable,
        string name,
        Func<WslPyEidorsReconstructionOptions, string, WslPyEidorsReconstructionOptions> apply)
    {
        var value = getEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? options : apply(options, value.Trim());
    }

    private static bool ParseBoolean(string value, string name)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException($"{name} must be true/false, 1/0, yes/no, or on/off.")
        };
    }

    private static string CoalesceRequired(string? candidate, string fallback)
    {
        return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
    }

    private static string? CoalesceOptional(string? candidate, string? fallback)
    {
        return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
    }

    private sealed class WslPyEidorsReconstructionOptionsConfig
    {
        public string? DistroName { get; init; }

        public string? BackendRepositoryPath { get; init; }

        public string? ExchangeDirectory { get; init; }

        public string? BackendProfile { get; init; }

        public bool? UseNixDevelop { get; init; }

        public string? NixDevelopProfile { get; init; }

        public string? WorkerExecutable { get; init; }

        public string? WorkerArguments { get; init; }

        public string? WorkerLaunchCommand { get; init; }

        public string? DoctorCommand { get; init; }
    }
}
