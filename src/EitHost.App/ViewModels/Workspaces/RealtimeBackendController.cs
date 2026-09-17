using System.IO;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimeBackendController : IRealtimeReconstructionBackend, IPseudo3dKrigingBackend
{
    private readonly SemaphoreSlim backendGate = new(1, 1);
    private readonly SemaphoreSlim pseudo3dBackendGate = new(1, 1);
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly object lifetimeGate = new();
    private readonly bool ownsBackend;
    private readonly Func<WslPyEidorsReconstructionOptions, string, WslPyEidorsReconstructionOptions> applyBackendProfile;
    private readonly Func<string, string, bool, IReadOnlyList<WslPyEidorsBackendProfile>> loadBackendProfiles;
    private readonly Func<string, string, bool, WslPyEidorsBackendManifestSnapshot> loadBackendSnapshot;
    private readonly Func<WslPyEidorsReconstructionOptions, WslPyEidorsReconstructionOptions> resolveConfiguredOptions;
    private readonly Func<WslPyEidorsReconstructionOptions, string> saveUserConfig;
    private readonly bool initialManifestValidationDeferred;
    private readonly bool usesLegacyManifestDelegates;
    private IRealtimeReconstructionBackend backend;
    private IPseudo3dKrigingBackend? pseudo3dBackend;
    private int admittedOperationCount;
    private bool disposed;
    private bool resourcesDisposed;

    internal RealtimeBackendController(
        string exchangeDirectory,
        IRealtimeReconstructionBackend? suppliedBackend = null,
        WslPyEidorsReconstructionOptions? initialOptions = null,
        Func<WslPyEidorsReconstructionOptions, string>? saveUserConfig = null,
        Func<WslPyEidorsReconstructionOptions, string, WslPyEidorsReconstructionOptions>? applyBackendProfile = null,
        Func<string, string, bool, IReadOnlyList<WslPyEidorsBackendProfile>>? loadBackendProfiles = null,
        Func<WslPyEidorsReconstructionOptions, WslPyEidorsReconstructionOptions>? resolveConfiguredOptions = null,
        Func<string, string, bool, WslPyEidorsBackendManifestSnapshot>? loadBackendSnapshot = null,
        IPseudo3dKrigingBackend? suppliedPseudo3dBackend = null)
    {
        usesLegacyManifestDelegates = loadBackendSnapshot is null
            && (applyBackendProfile is not null
                || loadBackendProfiles is not null
                || resolveConfiguredOptions is not null);
        this.saveUserConfig = saveUserConfig ?? (static updated =>
            WslPyEidorsReconstructionOptionsLoader.SaveUserConfig(updated));
        this.applyBackendProfile = applyBackendProfile ?? (static (options, profileName) =>
            WslPyEidorsBackendManifest.ApplyProfile(options, profileName));
        this.loadBackendProfiles = loadBackendProfiles ?? (static (distroName, repositoryPath, requireDefaultProfile) =>
            WslPyEidorsBackendManifest.LoadProfilesOrThrow(
                distroName,
                repositoryPath,
                requireDefaultProfile));
        this.loadBackendSnapshot = loadBackendSnapshot ?? (static (distroName, repositoryPath, requireDefaultProfile) =>
            WslPyEidorsBackendManifest.LoadSnapshotOrThrow(
                distroName,
                repositoryPath,
                requireDefaultProfile));
        this.resolveConfiguredOptions = resolveConfiguredOptions ?? (static options =>
            WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(options));
        string? configLoadError = null;
        string? backendValidationError = null;
        if (initialOptions is not null)
        {
            Options = initialOptions;
        }
        else
        {
            try
            {
                Options = WslPyEidorsReconstructionOptionsLoader.Load(exchangeDirectory: exchangeDirectory);
            }
            catch (Exception ex)
            {
                Options = new WslPyEidorsReconstructionOptions(
                    DistroName: string.Empty,
                    ExchangeDirectory: exchangeDirectory);
                configLoadError = ex.Message;
            }
        }

        var initialRepositoryUsesWsl = IsWslBackedRepositoryPath(Options.BackendRepositoryPath);
        initialManifestValidationDeferred = initialRepositoryUsesWsl
            && !HasValidCustomDirectConfiguration(Options);
        if (initialManifestValidationDeferred)
        {
            Options = ClearDeferredBackendLaunchState(Options);
        }
        else
        {
            try
            {
                Options = this.resolveConfiguredOptions(Options);
            }
            catch (PyEidorsReconstructionException ex)
            {
                Options = ClearUnverifiedBackendConfiguration(Options);
                backendValidationError = ex.Message;
            }
        }

        ProfileOptions = initialRepositoryUsesWsl
            ? CreateProfileOptions(Options, [])
            : CreateProfileOptions(Options);
        if (suppliedBackend is null)
        {
            if (initialManifestValidationDeferred)
            {
                backend = new DeferredManifestBackend();
            }
            else
            {
                backend = new WslPyEidorsReconstructionBackend(Options);
                pseudo3dBackend = new WslPyEidorsReconstructionBackend(Options);
            }

            ownsBackend = true;
        }
        else
        {
            backend = suppliedBackend;
            pseudo3dBackend = suppliedPseudo3dBackend ?? suppliedBackend as IPseudo3dKrigingBackend;
        }

        Status = initialManifestValidationDeferred
            ? "PyEIDORS 后端正在后台验证，当前不可用；验证完成前不会使用已缓存的启动命令。"
            : backendValidationError is not null
            ? "PyEIDORS 后端配置读取失败，当前不可用；"
                + $"请重新选择 PyEIDORS 后端路径：{backendValidationError}"
            : configLoadError is null
                ? CreateStatus()
                : $"PyEIDORS 后端配置读取失败，当前保持未配置：{configLoadError}";
    }

    internal event Action? StateChanged;

    internal WslPyEidorsReconstructionOptions Options { get; private set; }

    internal IReadOnlyList<SelectionOption> ProfileOptions { get; private set; }

    internal bool OwnsBackend => ownsBackend;

    internal bool UsesSharedPseudo3dWorker => ReferenceEquals(backend, pseudo3dBackend);

    internal bool UsesDedicatedPseudo3dWorker => pseudo3dBackend is not null
        && !UsesSharedPseudo3dWorker;

    internal string Status { get; private set; }

    internal string ProfileLabel => GetProfileLabel(Options.BackendProfile);

    internal string NixProfile => string.IsNullOrWhiteSpace(Options.NixDevelopProfile)
        ? "nix develop 默认"
        : Options.NixDevelopProfile;

    internal string DisplayPath => CreateDisplayPath(Options);

    internal string ConfigPath => WslPyEidorsReconstructionOptionsLoader.GetUserConfigPath();

    internal string InitialDirectory => CreateInitialDirectory(Options);

    internal string GetValidatedStartProfile()
    {
        ThrowIfDisposed();
        var options = Options;
        if (string.IsNullOrWhiteSpace(options.BackendRepositoryPath))
        {
            throw new InvalidOperationException(
                "尚未配置 PyEIDORS 后端目录。请在后端设置中选择本机 PyEIDORS 安装根目录和后端路线，再启动测量。");
        }

        if (backend is DeferredManifestBackend)
        {
            throw new InvalidOperationException(
                $"{Status} 请在后端设置中确认验证成功后，再启动测量。");
        }

        if (string.IsNullOrWhiteSpace(options.BackendProfile))
        {
            throw new InvalidOperationException(
                $"PyEIDORS 后端路线尚未就绪。请在后端设置中选择本机后端目录和有效路线，再启动测量。{Status}");
        }

        if (string.IsNullOrWhiteSpace(options.DistroName))
        {
            throw new InvalidOperationException(
                "PyEIDORS 后端缺少 WSL 发行版。请重新选择本机 WSL 中的 PyEIDORS 安装根目录，再启动测量。");
        }

        if (string.IsNullOrWhiteSpace(options.WorkerLaunchCommand)
            && !options.UseNixDevelop
            && string.IsNullOrWhiteSpace(options.WorkerExecutable))
        {
            throw new InvalidOperationException(
                $"PyEIDORS 后端启动配置尚未就绪。请重新选择后端路径并完成验证，再启动测量。{Status}");
        }

        return options.BackendProfile;
    }

    internal async Task<string?> SelectProfileAsync(
        string? profileName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = profileName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        AdmitOperation();
        var entered = false;
        try
        {
            await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (string.Equals(Options.BackendProfile, normalized, StringComparison.Ordinal))
            {
                return null;
            }

            var selection = await Task.Run(() =>
                ResolveProfileSelection(Options, normalized), cancellationToken).ConfigureAwait(false);
            var configPath = await ApplyOptionsCoreAsync(
                selection.Updated,
                CreateProfileOptions(selection.Updated, selection.Profiles),
                persist: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            SetStatus($"PyEIDORS 后端路线已保存：{configPath}");
            return configPath;
        }
        finally
        {
            if (entered)
            {
                configurationGate.Release();
            }

            CompleteOperation();
        }
    }

    internal async Task<string> SelectRepositoryPathAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        AdmitOperation();
        var entered = false;
        try
        {
            await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var selection = await Task.Run(() =>
                ResolveRepositorySelection(Options, selectedPath), cancellationToken).ConfigureAwait(false);
            var configPath = await ApplyOptionsCoreAsync(
                selection.Updated,
                CreateProfileOptions(selection.Updated, selection.Profiles),
                persist: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            SetStatus($"PyEIDORS 后端路径已保存：{configPath}");
            return configPath!;
        }
        finally
        {
            if (entered)
            {
                configurationGate.Release();
            }

            CompleteOperation();
        }
    }

    internal static WslPyEidorsReconstructionOptions CreateOptionsFromSelectedPath(
        WslPyEidorsReconstructionOptions current,
        string selectedPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        return WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(
            CreateUnresolvedOptionsFromSelectedPath(current, selectedPath));
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
                WslPyEidorsReconstructionOptions refreshed;
                IReadOnlyList<SelectionOption> profileOptions;
                if (!usesLegacyManifestDelegates
                    && !string.IsNullOrWhiteSpace(baseline.BackendRepositoryPath))
                {
                    var snapshot = loadBackendSnapshot(
                        baseline.DistroName,
                        baseline.BackendRepositoryPath,
                        false);
                    refreshed = WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(baseline, snapshot);
                    profileOptions = CreateProfileOptions(refreshed, snapshot.Profiles);
                }
                else if (initialManifestValidationDeferred)
                {
                    var profiles = loadBackendProfiles(
                        baseline.DistroName,
                        baseline.BackendRepositoryPath,
                        false);
                    refreshed = resolveConfiguredOptions(baseline);
                    profileOptions = CreateProfileOptions(refreshed, profiles);
                }
                else
                {
                    refreshed = WslPyEidorsBackendManifest.ApplyProfileIfManifestExists(baseline);
                    profileOptions = CreateProfileOptions(refreshed);
                }

                postToUi(() => _ = ApplyManifestProbeResultAsync(
                    baseline,
                    refreshed,
                    profileOptions,
                    isRealtimeActive,
                    diagnostic));
            }
            catch (Exception ex)
            {
                if (initialManifestValidationDeferred)
                {
                    postToUi(() => _ = ApplyManifestProbeFailureAsync(baseline, ex, diagnostic));
                }
                else
                {
                    diagnostic($"PyEIDORS backend manifest probe failed: {ex.Message}");
                }
            }
        });
    }

    private (WslPyEidorsReconstructionOptions Updated, IReadOnlyList<WslPyEidorsBackendProfile> Profiles)
        ResolveProfileSelection(WslPyEidorsReconstructionOptions current, string profileName)
    {
        if (usesLegacyManifestDelegates)
        {
            var legacyUpdated = applyBackendProfile(current, profileName);
            return (
                legacyUpdated,
                loadBackendProfiles(
                    legacyUpdated.DistroName,
                    legacyUpdated.BackendRepositoryPath,
                    false));
        }

        var snapshot = loadBackendSnapshot(
            current.DistroName,
            current.BackendRepositoryPath,
            false);
        return (
            WslPyEidorsBackendManifest.ApplyProfile(current, snapshot, profileName),
            snapshot.Profiles);
    }

    private (WslPyEidorsReconstructionOptions Updated, IReadOnlyList<WslPyEidorsBackendProfile> Profiles)
        ResolveRepositorySelection(WslPyEidorsReconstructionOptions current, string selectedPath)
    {
        var unresolved = CreateUnresolvedOptionsFromSelectedPath(current, selectedPath);
        if (usesLegacyManifestDelegates)
        {
            var legacyUpdated = resolveConfiguredOptions(unresolved);
            return (
                legacyUpdated,
                loadBackendProfiles(
                    legacyUpdated.DistroName,
                    legacyUpdated.BackendRepositoryPath,
                    false));
        }

        var snapshot = loadBackendSnapshot(
            unresolved.DistroName,
            unresolved.BackendRepositoryPath,
            false);
        return (
            WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(unresolved, snapshot),
            snapshot.Profiles);
    }

    private static WslPyEidorsReconstructionOptions CreateUnresolvedOptionsFromSelectedPath(
        WslPyEidorsReconstructionOptions current,
        string selectedPath)
    {
        var distroName = current.DistroName;
        string backendRepositoryPath;
        if (WslPathMapper.TryParseWslUncPath(
                selectedPath,
                out var selectedDistro,
                out var selectedLinuxPath))
        {
            distroName = selectedDistro;
            backendRepositoryPath = selectedLinuxPath;
        }
        else
        {
            backendRepositoryPath = WslPathMapper.ToWslPath(selectedPath);
        }

        return CreateOptionsWithoutDerivedLaunchState(
            current with
            {
                DistroName = distroName,
                BackendRepositoryPath = backendRepositoryPath
            },
            string.Empty);
    }

    public async Task<RealtimeReconstructionResult> ReconstructAsync(
        RealtimeReconstructionRequest request,
        CancellationToken cancellationToken = default)
    {
        AdmitOperation();
        var entered = false;
        try
        {
            if (ownsBackend)
            {
                ThrowIfBackendConfigurationUnavailable();
            }

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

    public async Task<Pseudo3dKrigingResult> InterpolatePseudo3dAsync(
        Pseudo3dKrigingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AdmitOperation();
        var entered = false;
        try
        {
            if (ownsBackend)
            {
                ThrowIfBackendConfigurationUnavailable();
            }

            await pseudo3dBackendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var selectedBackend = pseudo3dBackend
                ?? throw new NotSupportedException(
                    "The supplied realtime backend does not provide pseudo-3D Kriging.");
            return await selectedBackend.InterpolatePseudo3dAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                pseudo3dBackendGate.Release();
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
        var entered = false;
        try
        {
            await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await ApplyOptionsCoreAsync(
                options,
                profileOptions,
                persist: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                configurationGate.Release();
            }

            CompleteOperation();
        }
    }

    private async Task<string?> ApplyOptionsCoreAsync(
        WslPyEidorsReconstructionOptions options,
        IReadOnlyList<SelectionOption>? profileOptions = null,
        bool persist = false,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
            async () =>
            {
                var nextProfileOptions = profileOptions ?? CreateProfileOptions(options);
                await backendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var pseudo3dGateEntered = false;
                IRealtimeReconstructionBackend? replacement = null;
                IPseudo3dKrigingBackend? pseudo3dReplacement = null;
                try
                {
                    await pseudo3dBackendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    pseudo3dGateEntered = true;
                    if (ownsBackend)
                    {
                        replacement = new WslPyEidorsReconstructionBackend(options);
                        pseudo3dReplacement = new WslPyEidorsReconstructionBackend(options);
                    }

                    var configPath = persist ? saveUserConfig(options) : null;
                    IRealtimeReconstructionBackend? previous = null;
                    IPseudo3dKrigingBackend? previousPseudo3d = null;
                    if (replacement is not null)
                    {
                        previous = backend;
                        previousPseudo3d = pseudo3dBackend;
                        backend = replacement;
                        pseudo3dBackend = pseudo3dReplacement;
                        replacement = null;
                        pseudo3dReplacement = null;
                    }

                    Options = options;
                    ProfileOptions = nextProfileOptions;
                    Status = CreateStatus();
                    if (previous is not null)
                    {
                        try
                        {
                            previous.Dispose();
                            if (!ReferenceEquals(previousPseudo3d, previous)
                                && previousPseudo3d is IDisposable disposablePseudo3d)
                            {
                                disposablePseudo3d.Dispose();
                            }
                        }
                        catch
                        {
                            // The new backend and persisted options are already committed.
                        }
                    }

                    StateChanged?.Invoke();
                    return configPath;
                }
                finally
                {
                    try
                    {
                        replacement?.Dispose();
                        if (!ReferenceEquals(pseudo3dReplacement, replacement)
                            && pseudo3dReplacement is IDisposable disposablePseudo3d)
                        {
                            disposablePseudo3d.Dispose();
                        }
                    }
                    catch
                    {
                        // Cleanup must not mask a configuration or persistence failure.
                    }

                    if (pseudo3dGateEntered)
                    {
                        pseudo3dBackendGate.Release();
                    }

                    backendGate.Release();
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ApplyManifestProbeResultAsync(
        WslPyEidorsReconstructionOptions baseline,
        WslPyEidorsReconstructionOptions refreshed,
        IReadOnlyList<SelectionOption> profileOptions,
        Func<bool> isRealtimeActive,
        Action<string> diagnostic)
    {
        var admitted = false;
        var entered = false;
        try
        {
            AdmitOperation();
            admitted = true;
            await configurationGate.WaitAsync().ConfigureAwait(false);
            entered = true;
            if (!ReferenceEquals(Options, baseline))
            {
                return;
            }

            if (refreshed == baseline)
            {
                ProfileOptions = profileOptions;
                Status = string.IsNullOrWhiteSpace(baseline.BackendProfile)
                    ? "PyEIDORS 后端验证完成；请选择后端路线后再开始实时成像。"
                    : CreateStatus();
                StateChanged?.Invoke();
                return;
            }

            if (!isRealtimeActive())
            {
                await ApplyOptionsCoreAsync(refreshed, profileOptions).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            diagnostic($"PyEIDORS backend manifest apply failed: {ex.Message}");
        }
        finally
        {
            if (entered)
            {
                configurationGate.Release();
            }

            if (admitted)
            {
                CompleteOperation();
            }
        }
    }

    private async Task ApplyManifestProbeFailureAsync(
        WslPyEidorsReconstructionOptions baseline,
        Exception error,
        Action<string> diagnostic)
    {
        var admitted = false;
        var entered = false;
        try
        {
            AdmitOperation();
            admitted = true;
            await configurationGate.WaitAsync().ConfigureAwait(false);
            entered = true;
            if (!ReferenceEquals(Options, baseline))
            {
                return;
            }

            Options = ClearUnverifiedBackendConfiguration(baseline);
            ProfileOptions = CreateProfileOptions(Options, []);
            Status = "PyEIDORS 后端后台验证失败，当前不可用；"
                + "请确认 WSL 发行版可访问，且所选安装根目录包含 pyeidors.backend.json，然后重新选择后端路径："
                + error.Message;
            StateChanged?.Invoke();
            diagnostic($"PyEIDORS backend manifest probe failed: {error.Message}");
        }
        catch (Exception ex)
        {
            diagnostic($"PyEIDORS backend manifest failure apply failed: {ex.Message}");
        }
        finally
        {
            if (entered)
            {
                configurationGate.Release();
            }

            if (admitted)
            {
                CompleteOperation();
            }
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
        Exception? firstFailure = null;
        try
        {
            backend.Dispose();
        }
        catch (Exception ex)
        {
            firstFailure = ex;
        }

        try
        {
            if (!ReferenceEquals(pseudo3dBackend, backend)
                && pseudo3dBackend is IDisposable disposablePseudo3d)
            {
                disposablePseudo3d.Dispose();
            }
        }
        catch (Exception ex)
        {
            firstFailure ??= ex;
        }

        backendGate.Dispose();
        pseudo3dBackendGate.Dispose();
        configurationGate.Dispose();
        if (!suppressErrors && firstFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
    }

    private string CreateStatus() =>
        $"PyEIDORS 后端：{Options.DistroName}:{Options.BackendRepositoryPath} · {ProfileLabel}" +
        (ownsBackend && UsesDedicatedPseudo3dWorker ? " · 伪三维按需独立轻量进程" : string.Empty);

    private void ThrowIfBackendConfigurationUnavailable()
    {
        if (!string.IsNullOrWhiteSpace(Options.WorkerLaunchCommand)
            || Options.UseNixDevelop
            || (string.Equals(
                    Options.BackendProfile,
                    WslPyEidorsBackendManifest.CustomProfile,
                    StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(Options.WorkerExecutable)))
        {
            return;
        }

        var configPath = string.IsNullOrWhiteSpace(Options.BackendRepositoryPath)
            ? ConfigPath
            : Path.Combine(Options.BackendRepositoryPath, WslPyEidorsBackendManifest.FileName);
        throw PyEidorsReconstructionException.FromFrontendConfiguration(
            configPath,
            "BackendConfigurationUnavailable",
            "PyEIDORS 后端不可用；请重新选择 PyEIDORS 后端路径。");
    }

    private static WslPyEidorsReconstructionOptions ClearUnverifiedBackendConfiguration(
        WslPyEidorsReconstructionOptions options) =>
        CreateOptionsWithoutDerivedLaunchState(options, string.Empty);

    private static WslPyEidorsReconstructionOptions ClearDeferredBackendLaunchState(
        WslPyEidorsReconstructionOptions options) =>
        CreateOptionsWithoutDerivedLaunchState(options, options.BackendProfile);

    private static WslPyEidorsReconstructionOptions CreateOptionsWithoutDerivedLaunchState(
        WslPyEidorsReconstructionOptions options,
        string backendProfile) =>
        new(
            DistroName: options.DistroName,
            BackendRepositoryPath: options.BackendRepositoryPath,
            ExchangeDirectory: options.ExchangeDirectory,
            BackendProfile: backendProfile,
            UseNixDevelop: false,
            NixDevelopProfile: string.Empty,
            WorkerExecutable: string.Empty,
            WorkerArguments: string.Empty,
            WorkerLaunchCommand: null,
            DoctorCommand: null);

    private static bool IsWslBackedRepositoryPath(string backendRepositoryPath)
    {
        var repositoryPath = backendRepositoryPath.Trim();
        return repositoryPath.StartsWith("/", StringComparison.Ordinal)
            || WslPathMapper.TryParseWslUncPath(repositoryPath, out _, out _);
    }

    private static bool HasValidCustomDirectConfiguration(WslPyEidorsReconstructionOptions options)
    {
        var directLaunch = !string.IsNullOrWhiteSpace(options.WorkerLaunchCommand)
            || options.UseNixDevelop;
        var customProfile = string.IsNullOrWhiteSpace(options.BackendProfile)
            || string.Equals(
                options.BackendProfile,
                WslPyEidorsBackendManifest.CustomProfile,
                StringComparison.Ordinal);
        return directLaunch && customProfile;
    }

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

    internal static string CreateInitialDirectory(WslPyEidorsReconstructionOptions options)
    {
        var displayPath = CreateDisplayPath(options);
        if (!string.IsNullOrWhiteSpace(displayPath))
        {
            return displayPath;
        }

        var distroName = options.DistroName.Trim();
        if (!string.IsNullOrWhiteSpace(distroName))
        {
            return $@"\\wsl.localhost\{distroName}\home";
        }

        return @"\\wsl.localhost";
    }

    private sealed class DeferredManifestBackend : IRealtimeReconstructionBackend
    {
        public Task<RealtimeReconstructionResult> ReconstructAsync(
            RealtimeReconstructionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<RealtimeReconstructionResult>(new InvalidOperationException(
                "PyEIDORS backend manifest validation has not completed."));

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
