using System.IO;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimeBackendController : IRealtimeReconstructionBackend
{
    private readonly SemaphoreSlim backendGate = new(1, 1);
    private readonly object lifetimeGate = new();
    private readonly bool ownsBackend;
    private IRealtimeReconstructionBackend backend;
    private int admittedOperationCount;
    private bool disposed;
    private bool resourcesDisposed;

    internal RealtimeBackendController(
        string exchangeDirectory,
        IRealtimeReconstructionBackend? suppliedBackend = null)
    {
        string? configLoadError = null;
        try
        {
            Options = WslPyEidorsReconstructionOptionsLoader.Load(exchangeDirectory: exchangeDirectory);
        }
        catch (Exception ex)
        {
            Options = new WslPyEidorsReconstructionOptions(ExchangeDirectory: exchangeDirectory);
            configLoadError = ex.Message;
        }

        Options = WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(Options);
        ProfileOptions = CreateProfileOptions(Options);
        if (suppliedBackend is null)
        {
            backend = new WslPyEidorsReconstructionBackend(Options);
            ownsBackend = true;
        }
        else
        {
            backend = suppliedBackend;
        }

        Status = configLoadError is null
            ? CreateStatus()
            : $"PyEIDORS 后端配置读取失败，当前保持未配置：{configLoadError}";
    }

    internal event Action? StateChanged;

    internal WslPyEidorsReconstructionOptions Options { get; private set; }

    internal IReadOnlyList<SelectionOption> ProfileOptions { get; private set; }

    internal bool OwnsBackend => ownsBackend;

    internal string Status { get; private set; }

    internal string ProfileLabel => GetProfileLabel(Options.BackendProfile);

    internal string NixProfile => string.IsNullOrWhiteSpace(Options.NixDevelopProfile)
        ? "nix develop 默认"
        : Options.NixDevelopProfile;

    internal string DisplayPath => CreateDisplayPath(Options);

    internal string ConfigPath => WslPyEidorsReconstructionOptionsLoader.GetUserConfigPath();

    internal string InitialDirectory => CreateInitialDirectory(Options);

    internal async Task<string?> SelectProfileAsync(
        string? profileName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = profileName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(Options.BackendProfile, normalized, StringComparison.Ordinal))
        {
            return null;
        }

        var updated = WslPyEidorsBackendManifest.ApplyProfile(Options, normalized);
        await ApplyOptionsAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        var configPath = WslPyEidorsReconstructionOptionsLoader.SaveUserConfig(updated);
        SetStatus($"PyEIDORS 后端路线已保存：{configPath}");
        return configPath;
    }

    internal async Task<string> SelectRepositoryPathAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var updated = CreateOptionsFromSelectedPath(Options, selectedPath);
        await ApplyOptionsAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        var configPath = WslPyEidorsReconstructionOptionsLoader.SaveUserConfig(updated);
        SetStatus($"PyEIDORS 后端路径已保存：{configPath}");
        return configPath;
    }

    internal static WslPyEidorsReconstructionOptions CreateOptionsFromSelectedPath(
        WslPyEidorsReconstructionOptions current,
        string selectedPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var distroName = current.DistroName;
        string backendRepositoryPath;
        if (WslPathMapper.TryParseWslUncPath(selectedPath, out var selectedDistro, out var selectedLinuxPath))
        {
            distroName = selectedDistro;
            backendRepositoryPath = selectedLinuxPath;
        }
        else
        {
            backendRepositoryPath = WslPathMapper.ToWslPath(selectedPath);
        }

        return WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(current with
        {
            DistroName = distroName,
            BackendRepositoryPath = backendRepositoryPath,
            BackendProfile = string.Empty,
            UseNixDevelop = false,
            NixDevelopProfile = string.Empty,
            WorkerLaunchCommand = null,
            DoctorCommand = null
        });
    }

    internal void SetStatus(string status)
    {
        Status = status;
        StateChanged?.Invoke();
    }

    internal void BeginManifestProbe(
        Action<Action> postToUi,
        Func<bool> isRealtimeActive,
        Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentNullException.ThrowIfNull(isRealtimeActive);
        ArgumentNullException.ThrowIfNull(diagnostic);
        var baseline = Options;
        _ = Task.Run(() =>
        {
            try
            {
                var refreshed = WslPyEidorsBackendManifest.ApplyProfileIfManifestExists(baseline);
                var profileOptions = CreateProfileOptions(refreshed);
                postToUi(() => _ = ApplyManifestProbeResultAsync(
                    baseline,
                    refreshed,
                    profileOptions,
                    isRealtimeActive(),
                    diagnostic));
            }
            catch (Exception ex)
            {
                diagnostic($"PyEIDORS backend manifest probe failed: {ex.Message}");
            }
        });
    }

    public async Task<RealtimeReconstructionResult> ReconstructAsync(
        RealtimeReconstructionRequest request,
        CancellationToken cancellationToken = default)
    {
        AdmitOperation();
        var entered = false;
        try
        {
            await backendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await backend.ReconstructAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                backendGate.Release();
            }

            CompleteOperation();
        }
    }

    public void Dispose()
    {
        var disposeNow = false;
        lock (lifetimeGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (admittedOperationCount == 0)
            {
                resourcesDisposed = true;
                disposeNow = true;
            }
        }

        if (disposeNow)
        {
            DisposeResources(suppressErrors: false);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal async Task ApplyOptionsAsync(
        WslPyEidorsReconstructionOptions options,
        IReadOnlyList<SelectionOption>? profileOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        AdmitOperation();
        try
        {
            await Task.Run(
                async () =>
                {
                    await backendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (ownsBackend)
                        {
                            var replacement = new WslPyEidorsReconstructionBackend(options);
                            var previous = backend;
                            backend = replacement;
                            previous.Dispose();
                        }

                        Options = options;
                        ProfileOptions = profileOptions ?? CreateProfileOptions(options);
                        Status = CreateStatus();
                        StateChanged?.Invoke();
                    }
                    finally
                    {
                        backendGate.Release();
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation();
        }
    }

    private async Task ApplyManifestProbeResultAsync(
        WslPyEidorsReconstructionOptions baseline,
        WslPyEidorsReconstructionOptions refreshed,
        IReadOnlyList<SelectionOption> profileOptions,
        bool realtimeActive,
        Action<string> diagnostic)
    {
        try
        {
            if (!ReferenceEquals(Options, baseline))
            {
                return;
            }

            if (refreshed == baseline)
            {
                ProfileOptions = profileOptions;
                StateChanged?.Invoke();
                return;
            }

            if (!realtimeActive)
            {
                await ApplyOptionsAsync(refreshed, profileOptions).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            diagnostic($"PyEIDORS backend manifest apply failed: {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    private void AdmitOperation()
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            admittedOperationCount++;
        }
    }

    private void CompleteOperation()
    {
        var disposeNow = false;
        lock (lifetimeGate)
        {
            admittedOperationCount--;
            if (disposed && admittedOperationCount == 0 && !resourcesDisposed)
            {
                resourcesDisposed = true;
                disposeNow = true;
            }
        }

        if (disposeNow)
        {
            DisposeResources(suppressErrors: true);
        }
    }

    private void DisposeResources(bool suppressErrors)
    {
        try
        {
            backend.Dispose();
        }
        catch when (suppressErrors)
        {
            // Deferred cleanup cannot report through a caller after Dispose has returned.
        }
        finally
        {
            backendGate.Dispose();
        }
    }

    private string CreateStatus() =>
        $"PyEIDORS 后端：{Options.DistroName}:{Options.BackendRepositoryPath} · {ProfileLabel}";

    private static IReadOnlyList<SelectionOption> CreateProfileOptions(WslPyEidorsReconstructionOptions options) =>
        CreateProfileOptions(
            options,
            WslPyEidorsBackendManifest.LoadProfiles(options.DistroName, options.BackendRepositoryPath));

    private static IReadOnlyList<SelectionOption> CreateProfileOptions(
        WslPyEidorsReconstructionOptions options,
        IReadOnlyList<WslPyEidorsBackendProfile> profiles)
    {
        var optionsList = profiles
            .Select(profile => new SelectionOption(CreateProfileLabel(profile), profile.ProfileName))
            .ToList();
        if (string.IsNullOrWhiteSpace(options.BackendProfile))
        {
            optionsList.Insert(0, new SelectionOption("请选择后端路线", string.Empty));
        }
        else if (!optionsList.Any(option => string.Equals(option.Value, options.BackendProfile, StringComparison.Ordinal)))
        {
            optionsList.Add(new SelectionOption(options.BackendProfile, options.BackendProfile));
        }

        return optionsList;
    }

    private string GetProfileLabel(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return "未选择";
        }

        return ProfileOptions.FirstOrDefault(
            option => string.Equals(option.Value, profileName, StringComparison.Ordinal))?.Label ?? profileName;
    }

    private static string CreateProfileLabel(WslPyEidorsBackendProfile profile)
    {
        var label = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.ProfileName : profile.DisplayName;
        if (profile.RequiresAmgx)
        {
            return $"{label} · 需 AMGX";
        }

        return profile.RequiresGpu ? $"{label} · 需 NVIDIA" : label;
    }

    private static string CreateDisplayPath(WslPyEidorsReconstructionOptions options) =>
        options.BackendRepositoryPath.StartsWith("/", StringComparison.Ordinal)
            ? WslPathMapper.ToWslUncPath(options.DistroName, options.BackendRepositoryPath)
            : options.BackendRepositoryPath;

    private static string CreateInitialDirectory(WslPyEidorsReconstructionOptions options)
    {
        var displayPath = CreateDisplayPath(options);
        if (Directory.Exists(displayPath))
        {
            return displayPath;
        }

        var wslHomePath = $@"\\wsl.localhost\{options.DistroName}\home";
        if (Directory.Exists(wslHomePath))
        {
            return wslHomePath;
        }

        var wslRoot = $@"\\wsl.localhost\{options.DistroName}";
        return Directory.Exists(wslRoot) ? wslRoot : @"\\wsl.localhost";
    }
}
