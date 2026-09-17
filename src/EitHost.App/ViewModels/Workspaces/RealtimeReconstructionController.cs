using System.IO;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeReconstructionCallbacks(
    Action<string> Diagnostic,
    Action<string, string?, string?, string?, string?, RealtimeRunState, int, int> PublishQualityAxes,
    Action<string, string, RealtimeRunState, int, int> PublishReconstructionActivity,
    Action<string, RealtimeReconstructionResult, DateTimeOffset, RealtimeRunState, int, int> PublishPseudo3dLayer,
    Action<string, RealtimeReconstructionResult, double, RealtimeRunState, int, int, string> PublishRoiMeasurement,
    Action<string, RealtimeRunState, int, int> PublishProvisionalRoiUnavailable,
    Action<string> QueueLog,
    Action<RealtimeRunState, int, int, IReadOnlyList<string>, string?> PublishUi,
    Func<RealtimeRunState, bool> ShouldRenderBoundaryFit,
    Func<RealtimeRunState, bool> ShouldRenderImage,
    Func<RealtimeRunState, bool> ShouldPublishStatus);

internal sealed class RealtimeReconstructionController
{
    private const int MaxConsecutiveFailures = 3;
    internal static readonly TimeSpan WarmupTimeout = Timeout.InfiniteTimeSpan;
    internal static readonly TimeSpan BackendResetTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly IRealtimeReconstructionBackend backend;
    private readonly RealtimeDerivedPersistenceController persistence;
    private readonly RealtimeReconstructionCallbacks callbacks;

    internal RealtimeReconstructionController(
        IRealtimeReconstructionBackend backend,
        RealtimeDerivedPersistenceController persistence,
        RealtimeReconstructionCallbacks callbacks)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal static TimeSpan GetRequestTimeout(int completedReconstructionFrames) =>
        GetRequestTimeout(
            completedReconstructionFrames,
            backendSessionResetPending: false,
            backendSessionWarmupPending: false);

    internal static TimeSpan GetRequestTimeout(
        int completedReconstructionFrames,
        bool backendSessionResetPending) =>
        GetRequestTimeout(
            completedReconstructionFrames,
            backendSessionResetPending,
            backendSessionWarmupPending: false);

    internal static TimeSpan GetRequestTimeout(
        int completedReconstructionFrames,
        bool backendSessionResetPending,
        bool backendSessionWarmupPending) =>
        completedReconstructionFrames <= 0 || backendSessionWarmupPending
            ? WarmupTimeout
            : backendSessionResetPending
                ? BackendResetTimeout
                : RequestTimeout;

    internal async Task ExecuteAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        IReadOnlyList<double> measurementWeights,
        string weightPolicyVersion,
        bool temporalInnovationCandidate,
        ElectrodeContactDiagnosticResult? contactResult,
        EcdCwrWaveformTemplateDisplayPackage? templateDisplayPackage,
        EcdCwrBoundaryChangeDecision? boundaryChangeDecision,
        bool degradedDemodulation,
        double? imageQualityCap,
        string? degradedStatus,
        bool publishRoiMeasurement,
        CancellationToken cancellationToken)
    {
        var dynamicGeneration = state.DynamicKalmanGeneration;
        var referenceEpoch = state.ReferenceEpoch;
        var referenceLockKind = state.ActiveReferenceLockKind;
        var timeout = GetRequestTimeout(
            Volatile.Read(ref state.ReconstructionFrames),
            config.EnableDynamicKalman &&
            !degradedDemodulation &&
            state.DynamicKalmanResetPending,
            Volatile.Read(ref state.BackendSessionWarmupPending));
        var hasAutomaticTimeout = timeout != Timeout.InfiniteTimeSpan;
        var timeoutLabel = hasAutomaticTimeout
            ? FormattableString.Invariant($"{timeout.TotalSeconds:F0}s")
            : "manual-cancel-only";
        try
        {
            if (ShouldLogMilestone(block.BlockNumber))
            {
                callbacks.Diagnostic(
                    $"{config.SetLabel} reconstruction begin block={block.BlockNumber} timeout={timeoutLabel} route={config.ReconstructionRoute}");
            }

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (hasAutomaticTimeout)
            {
                requestTimeout.CancelAfter(timeout);
            }
            var dynamicMode = state.DynamicKalmanForceSafeImage ||
                string.Equals(config.DynamicKalmanMode, "auto", StringComparison.Ordinal)
                    ? "fast_image"
                    : config.DynamicKalmanMode;
            var holdDynamic = boundaryChangeDecision is not null &&
                EcdCwrBoundaryChangeReconstructionDisposition.FromDecision(boundaryChangeDecision).HoldDynamicState;
            var dynamicKalman = config.EnableDynamicKalman && !degradedDemodulation && !holdDynamic
                ? new RealtimeDynamicKalmanOptions(
                    sessionId: $"{config.ImagingRunId:N}:ref{dynamicGeneration}",
                    fingerprint: FormattableString.Invariant(
                        $"set={config.SetLabel};run={config.ImagingRunId:N};ref={dynamicGeneration};profile={config.BackendProfile};route={config.ReconstructionRoute};mesh={config.MeshSize:G17};freq={config.DacSettings.ActualFrequencyHz:G17};orientation={config.DifferenceOrientation};dynamic={dynamicMode}"),
                    resetSession: state.DynamicKalmanResetPending,
                    innovationCandidate: temporalInnovationCandidate,
                    upstreamLatencyFrames: 2,
                    processNoiseRelativeStd: dynamicMode == "fast_image"
                        ? RealtimeDynamicKalmanOptions.SafeImageProcessNoiseRelativeStd
                        : RealtimeDynamicKalmanOptions.AdvancedMeasurementProcessNoiseRelativeStd,
                    measurementNoiseRelativeStd: RealtimeDynamicKalmanOptions.DefaultMeasurementNoiseRelativeStd,
                    mode: dynamicMode)
                : null;
            var acquiredAt = RealtimeDerivedPersistenceController.CalculateBlockAcquiredAt(config, state, block);
            var request = new RealtimeReconstructionRequest(
                config.SetLabel,
                block.BlockNumber,
                acquiredAt,
                reference,
                target,
                config.DacSettings.ActualFrequencyHz,
                state.ExecutionReceipt?.CalculateEffectiveChannelCycles(config.DacSettings.ActualFrequencyHz)
                    ?? config.ExcitationSettings.ChannelCycles,
                config.MeshSize,
                config.DifferenceLambda,
                config.PersistReconstructionResults,
                config.ReconstructionRoute,
                config.CustomLambdaEnabled,
                config.DifferenceOrientation,
                measurementWeights,
                weightPolicyVersion,
                dynamicKalman,
                ReconstructionScale.ModelRelative,
                state.ReferenceUsesCommonScaleNormalization
                    ? ReconstructionScale.CommonScaleNormalizedRelativeProvenance
                    : ReconstructionScale.NormalizedModelProvenance);
            var reconstructionTask = backend.ReconstructAsync(request, requestTimeout.Token);
            var boundedReconstructionTask = hasAutomaticTimeout
                ? reconstructionTask.WaitAsync(timeout + TimeSpan.FromMilliseconds(250), cancellationToken)
                : reconstructionTask.WaitAsync(cancellationToken);
            var result = await boundedReconstructionTask.ConfigureAwait(false);
            if (!result.Succeeded || ShouldLogMilestone(result.BlockNumber))
            {
                callbacks.Diagnostic(result.Succeeded
                    ? $"{config.SetLabel} reconstruction ok block={result.BlockNumber} elapsed={result.BackendElapsed.TotalMilliseconds:F0}ms"
                    : $"{config.SetLabel} reconstruction failed block={block.BlockNumber}: {result.ErrorMessage}");
            }

            if (result.Succeeded)
            {
                try
                {
                    await HandleSuccessAsync(
                        config,
                        state,
                        block,
                        reference,
                        target,
                        measurementWeights,
                        weightPolicyVersion,
                        temporalInnovationCandidate,
                        contactResult,
                        templateDisplayPackage,
                        boundaryChangeDecision,
                        degradedDemodulation,
                        imageQualityCap,
                        degradedStatus,
                        publishRoiMeasurement,
                        dynamicGeneration,
                        referenceEpoch,
                        referenceLockKind,
                        dynamicKalman,
                        acquiredAt,
                        result).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PyEidorsReconstructionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw PyEidorsReconstructionException.FromFrontendProcessing(
                        "结果后处理/规范网格契约/持久化",
                        ex);
                }

                return;
            }

            var error = result.ErrorMessage ?? "unknown reconstruction failure";
            if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "failure"))
            {
                return;
            }

            await persistence.RecordReconstructionFailureAsync(config, state, block, error).ConfigureAwait(false);
            if (!TryRegisterFailure(state, dynamicGeneration, referenceEpoch, error, out var failures))
            {
                LogStaleReconstruction(config.SetLabel, block.BlockNumber, dynamicGeneration, referenceEpoch, "failure-after-persistence", state);
                return;
            }

            var suspended = failures >= MaxConsecutiveFailures;
            if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "failure-activity"))
            {
                return;
            }

            callbacks.PublishReconstructionActivity(
                config.SetLabel,
                suspended ? $"重构状态：已暂停 · {error}" : $"重构状态：失败 · {error}",
                state,
                dynamicGeneration,
                referenceEpoch);
            if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "failure-quality"))
            {
                return;
            }

            callbacks.PublishQualityAxes(
                config.SetLabel,
                null,
                null,
                suspended ? $"重构质量：已暂停 · {error}" : $"重构质量：失败 · {error}",
                "ROI 就绪：否 · 当前目标重构失败",
                state,
                dynamicGeneration,
                referenceEpoch);
            PublishFailureUi(
                config.SetLabel,
                state,
                dynamicGeneration,
                referenceEpoch,
                $"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon failed {error}",
                failures,
                $"{config.SetLabel} 连续 {failures} 次重构失败，已暂停重构；采集、解调和参考稳定性继续运行。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callbacks.Diagnostic($"{config.SetLabel} reconstruction canceled by stop block={block.BlockNumber}");
        }
        catch (OperationCanceledException)
        {
            await HandleTimeoutAsync(
                config,
                state,
                block,
                "reconstruction timeout",
                timeout,
                waitTimeout: false,
                dynamicGeneration,
                referenceEpoch).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await HandleTimeoutAsync(
                config,
                state,
                block,
                "reconstruction wait timeout",
                timeout,
                waitTimeout: true,
                dynamicGeneration,
                referenceEpoch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "exception"))
            {
                return;
            }

            await persistence.RecordReconstructionFailureAsync(config, state, block, ex.Message).ConfigureAwait(false);
            if (!TryRegisterFailure(state, dynamicGeneration, referenceEpoch, ex.Message, out var failures))
            {
                LogStaleReconstruction(config.SetLabel, block.BlockNumber, dynamicGeneration, referenceEpoch, "exception-after-persistence", state);
                return;
            }

            callbacks.Diagnostic(
                $"{config.SetLabel} reconstruction exception block={block.BlockNumber} failures={failures}: {ex}");
            if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "exception-activity"))
            {
                return;
            }

            callbacks.PublishReconstructionActivity(
                config.SetLabel,
                $"重构状态：异常 · {ex.Message}",
                state,
                dynamicGeneration,
                referenceEpoch);
            if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "exception-quality"))
            {
                return;
            }

            callbacks.PublishQualityAxes(
                config.SetLabel,
                null,
                null,
                $"重构质量：失败 · {ex.Message}",
                "ROI 就绪：否 · 当前目标重构异常",
                state,
                dynamicGeneration,
                referenceEpoch);
            PublishFailureUi(
                config.SetLabel,
                state,
                dynamicGeneration,
                referenceEpoch,
                $"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon exception {ex.Message}",
                failures,
                $"{config.SetLabel} 连续 {failures} 次重构异常，已暂停重构；采集和解调继续运行。");
        }
    }

    private async Task HandleSuccessAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        IReadOnlyList<double> measurementWeights,
        string weightPolicyVersion,
        bool temporalInnovationCandidate,
        ElectrodeContactDiagnosticResult? contactResult,
        EcdCwrWaveformTemplateDisplayPackage? templateDisplayPackage,
        EcdCwrBoundaryChangeDecision? boundaryChangeDecision,
        bool degradedDemodulation,
        double? imageQualityCap,
        string? degradedStatus,
        bool publishRoiMeasurement,
        int dynamicGeneration,
        int referenceEpoch,
        string referenceLockKind,
        RealtimeDynamicKalmanOptions? dynamicKalman,
        DateTimeOffset acquiredAt,
        RealtimeReconstructionResult result)
    {
        if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, result.BlockNumber, "success"))
        {
            return;
        }

        var acceptedDynamicGeneration = dynamicGeneration;
        var advancedGeneration = false;
        if (result.DynamicKalmanApplied &&
            !ApplyDynamicKalmanResult(
                config,
                state,
                dynamicGeneration,
                referenceEpoch,
                result,
                out result,
                out advancedGeneration))
        {
            return;
        }
        if (advancedGeneration)
        {
            acceptedDynamicGeneration++;
        }

        // Bind and verify the one DataRoot-wide canonical mesh before this result can
        // affect state, rendering, ROI, replay evidence, or persistence.
        _ = await persistence.EnsureCanonicalMeshAsync(
            config,
            state,
            result,
            acquiredAt).ConfigureAwait(false);

        if (!IsCurrentReconstruction(state, acceptedDynamicGeneration, referenceEpoch, result.BlockNumber, "success-after-mesh"))
        {
            return;
        }

        var imageQualityScore = RefineImageQuality(contactResult, result);
        if (imageQualityCap is { } qualityCap)
        {
            imageQualityScore = Math.Min(imageQualityScore ?? qualityCap, qualityCap);
        }

        result = result with
        {
            ImageQualityScore = imageQualityScore,
            TimeDivision = block.TimeDivision is { } stamp ? stamp with
            {
                TrustedZeroDifference = boundaryChangeDecision is not null &&
                    EcdCwrBoundaryChangeReconstructionDisposition.FromDecision(boundaryChangeDecision).UseZeroDifferenceInput
            } : null
        };

        var persistedLiveEvidence = await persistence.PersistReconstructionResultAsync(
            config,
            state,
            referenceEpoch,
            block,
            result,
            imageQualityScore,
            reference,
            target,
            measurementWeights,
            weightPolicyVersion,
            dynamicKalman).ConfigureAwait(false);
        if (!state.TryRecordReconstructionSuccess(
                acceptedDynamicGeneration,
                referenceEpoch,
                result.BackendElapsed,
                degradedDemodulation,
                out var completedFrames))
        {
            LogStaleReconstruction(
                config.SetLabel,
                result.BlockNumber,
                acceptedDynamicGeneration,
                referenceEpoch,
                "success-after-persistence",
                state);
            return;
        }
        if (!state.TryCommitReconstructionState(
                acceptedDynamicGeneration,
                referenceEpoch,
                () =>
                {
                    if (result.DynamicKalmanApplied && !advancedGeneration)
                    {
                        state.DynamicKalmanResetPending = false;
                    }

                    Volatile.Write(ref state.BackendSessionWarmupPending, false);
                    UpdateContactSubspaceEvidence(state, result);
                    state.RoiGeometry = new RealtimeRoiGeometry(
                        result.NodeCoords,
                        result.CellConnectivity,
                        result.GetMeshIndexMetadata());
                }))
        {
            LogStaleReconstruction(
                config.SetLabel,
                result.BlockNumber,
                acceptedDynamicGeneration,
                referenceEpoch,
                "success-before-presentation",
                state);
            return;
        }

        if (!IsCurrentReconstruction(state, acceptedDynamicGeneration, referenceEpoch, result.BlockNumber, "quality-presentation"))
        {
            return;
        }

        callbacks.PublishQualityAxes(
            config.SetLabel,
            null,
            null,
            degradedDemodulation
                ? $"重构质量：受限 · {degradedStatus ?? "健康通道降级重构"}"
                : imageQualityScore is { } quality
                    ? $"重构质量：成功 · Q={quality:F3} · condition={result.WeightedSystemConditionNumber:G3}"
                    : $"重构质量：成功 · condition={result.WeightedSystemConditionNumber:G3}",
            null,
            state,
            acceptedDynamicGeneration,
            referenceEpoch);
        if (!IsCurrentReconstruction(state, acceptedDynamicGeneration, referenceEpoch, result.BlockNumber, "pseudo3d-presentation"))
        {
            return;
        }

        callbacks.PublishPseudo3dLayer(
            config.SetLabel,
            result,
            acquiredAt,
            state,
            acceptedDynamicGeneration,
            referenceEpoch);
        var renderBoundaryFit = callbacks.ShouldRenderBoundaryFit(state);
        var renderImage = callbacks.ShouldRenderImage(state);
        if ((renderBoundaryFit || renderImage) &&
            state.VisualizationWorker?.TryPost(new RealtimeVisualizationWorkItem(
                result,
                reference.ToArray(),
                target.ToArray(),
                contactResult,
                templateDisplayPackage,
                imageQualityScore,
                completedFrames,
                renderBoundaryFit,
                renderImage,
                referenceEpoch,
                acceptedDynamicGeneration,
                degradedStatus,
                boundaryChangeDecision,
                PersistedLiveEvidence: persistedLiveEvidence)) != true)
        {
            callbacks.Diagnostic($"{config.SetLabel} visualization rejected block={result.BlockNumber}");
        }

        if (publishRoiMeasurement)
        {
            callbacks.PublishRoiMeasurement(
                config.SetLabel,
                result,
                imageQualityScore ?? block.QualityWeight,
                state,
                referenceEpoch,
                acceptedDynamicGeneration,
                referenceLockKind);
        }
        else if (IsCurrentReconstruction(
                     state,
                     acceptedDynamicGeneration,
                     referenceEpoch,
                     result.BlockNumber,
                     "provisional-roi-presentation"))
        {
            callbacks.PublishProvisionalRoiUnavailable(
                config.SetLabel,
                state,
                acceptedDynamicGeneration,
                referenceEpoch);
        }

        if (result.OutputPersisted &&
            callbacks.ShouldPublishStatus(state))
        {
            callbacks.PublishUi(
                state,
                acceptedDynamicGeneration,
                referenceEpoch,
                [$"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon block {result.BlockNumber} {result.BackendElapsed.TotalMilliseconds:F0}ms {result.OutputHdf5Path}"],
                null);
        }
    }

    private bool ApplyDynamicKalmanResult(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        int expectedDynamicGeneration,
        int expectedReferenceEpoch,
        RealtimeReconstructionResult current,
        out RealtimeReconstructionResult result,
        out bool advancedGeneration)
    {
        result = current;
        advancedGeneration = false;
        if (result.DynamicKalmanTotalLatencyFrames != 2)
        {
            throw new InvalidDataException(
                $"Dynamic Kalman returned total latency {result.DynamicKalmanTotalLatencyFrames}; realtime contract requires 2 blocks.");
        }

        var forceSafeImage = string.Equals(result.DynamicKalmanMode, "measurement", StringComparison.Ordinal) &&
            string.Equals(result.DynamicKalmanAction, "static_guard_reset", StringComparison.Ordinal);
        if (string.Equals(result.DynamicKalmanMode, "measurement", StringComparison.Ordinal))
        {
            var stability = RealtimeDynamicKalmanStabilityGuard.Evaluate(result.RawConductivity, result.Conductivity);
            if (stability.ShouldFallback)
            {
                forceSafeImage = true;
                var raw = result.RawConductivity;
                if (raw is null || raw.Length != result.Conductivity.Length || raw.Any(value => !double.IsFinite(value)))
                {
                    if (!state.TryAdvanceDynamicKalmanGeneration(
                            expectedDynamicGeneration,
                            expectedReferenceEpoch,
                            forceSafeImage: true,
                            out _))
                    {
                        LogStaleReconstruction(
                            config.SetLabel,
                            result.BlockNumber,
                            expectedDynamicGeneration,
                            expectedReferenceEpoch,
                            "kalman-malformed",
                            state);
                        return false;
                    }

                    Interlocked.Increment(ref state.SkippedReconstructionBlocks);
                    callbacks.Diagnostic(
                        $"{config.SetLabel} Kalman guard dropped malformed block={result.BlockNumber} reason={stability.Reason}; next=fast_image");
                    return false;
                }

                result = result with
                {
                    Conductivity = raw.ToArray(),
                    DynamicKalmanAction = "static_guard_reset",
                    DynamicKalmanFallback = true
                };
                callbacks.Diagnostic(FormattableString.Invariant(
                    $"{config.SetLabel} Kalman spatial guard block={result.BlockNumber} rms={stability.SpatialRmsRatio:F3} robust={stability.RobustSpreadRatio:F3} dev={stability.DeviationRelative:F4}; current=NOSER next=fast_image"));
            }
        }

        if (forceSafeImage)
        {
            if (!state.TryAdvanceDynamicKalmanGeneration(
                    expectedDynamicGeneration,
                    expectedReferenceEpoch,
                    forceSafeImage: true,
                    out _))
            {
                LogStaleReconstruction(
                    config.SetLabel,
                    result.BlockNumber,
                    expectedDynamicGeneration,
                    expectedReferenceEpoch,
                    "kalman-fallback",
                    state);
                return false;
            }

            advancedGeneration = true;
        }

        return true;
    }

    private async Task HandleTimeoutAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        string persistenceMessage,
        TimeSpan timeout,
        bool waitTimeout,
        int dynamicGeneration,
        int referenceEpoch)
    {
        if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "timeout"))
        {
            return;
        }

        await persistence.RecordReconstructionFailureAsync(config, state, block, persistenceMessage).ConfigureAwait(false);
        if (!TryRegisterFailure(state, dynamicGeneration, referenceEpoch, "reconstruction timeout", out var failures))
        {
            LogStaleReconstruction(config.SetLabel, block.BlockNumber, dynamicGeneration, referenceEpoch, "timeout-after-persistence", state);
            return;
        }

        if (!state.TryCommitReconstructionState(
                dynamicGeneration,
                referenceEpoch,
                () => Volatile.Write(ref state.BackendSessionWarmupPending, true)))
        {
            LogStaleReconstruction(
                config.SetLabel,
                block.BlockNumber,
                dynamicGeneration,
                referenceEpoch,
                "timeout-before-presentation",
                state);
            return;
        }

        callbacks.Diagnostic(
            $"{config.SetLabel} reconstruction{(waitTimeout ? " wait" : string.Empty)} timeout block={block.BlockNumber} failures={failures}");
        if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "timeout-activity"))
        {
            return;
        }

        callbacks.PublishReconstructionActivity(
            config.SetLabel,
            $"重构状态：超时 · block {block.BlockNumber} 超过 {timeout.TotalSeconds:F0}s",
            state,
            dynamicGeneration,
            referenceEpoch);
        if (!IsCurrentReconstruction(state, dynamicGeneration, referenceEpoch, block.BlockNumber, "timeout-quality"))
        {
            return;
        }

        callbacks.PublishQualityAxes(
            config.SetLabel,
            null,
            null,
            waitTimeout ? "重构质量：失败 · 后端等待超时" : "重构质量：失败 · 后端超时",
            "ROI 就绪：否 · 当前目标重构超时",
            state,
            dynamicGeneration,
            referenceEpoch);
        PublishFailureUi(
            config.SetLabel,
            state,
            dynamicGeneration,
            referenceEpoch,
            $"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon timeout block {block.BlockNumber}",
            failures,
            $"{config.SetLabel} 连续 {failures} 次重构超时，已暂停重构；采集和解调继续运行。");
    }

    private bool TryRegisterFailure(
        RealtimeRunState state,
        int dynamicGeneration,
        int referenceEpoch,
        string message,
        out int failures)
    {
        if (!state.TryRecordReconstructionFailure(
                dynamicGeneration,
                referenceEpoch,
                message,
                MaxConsecutiveFailures,
                out failures))
        {
            return false;
        }

        if (state.ReconstructionSuspended)
        {
            callbacks.Diagnostic($"{state.SetLabel} reconstruction suspended after {failures} failures: {message}");
        }

        return true;
    }

    private bool IsCurrentReconstruction(
        RealtimeRunState state,
        int dynamicGeneration,
        int referenceEpoch,
        int blockNumber,
        string stage)
    {
        if (state.IsReconstructionContextCurrent(dynamicGeneration, referenceEpoch))
        {
            return true;
        }

        LogStaleReconstruction(state.SetLabel, blockNumber, dynamicGeneration, referenceEpoch, stage, state);
        return false;
    }

    private void LogStaleReconstruction(
        string setLabel,
        int blockNumber,
        int dynamicGeneration,
        int referenceEpoch,
        string stage,
        RealtimeRunState state)
    {
        callbacks.Diagnostic(
            $"{setLabel} discard stale reconstruction block={blockNumber} stage={stage} " +
            $"generation={dynamicGeneration}->{state.DynamicKalmanGeneration} " +
            $"reference-epoch={referenceEpoch}->{state.ReferenceEpoch} invalidated={state.ReferenceInvalidated}");
    }

    private void PublishFailureUi(
        string setLabel,
        RealtimeRunState state,
        int dynamicGeneration,
        int referenceEpoch,
        string logLine,
        int failures,
        string status)
    {
        if (failures < MaxConsecutiveFailures)
        {
            callbacks.PublishUi(state, dynamicGeneration, referenceEpoch, [logLine], null);
            return;
        }

        callbacks.PublishUi(
            state,
            dynamicGeneration,
            referenceEpoch,
            [logLine, $"{DateTime.Now:HH:mm:ss} {setLabel} recon circuit breaker pause"],
            status);
    }

    internal static void UpdateContactSubspaceEvidence(
        RealtimeRunState state,
        RealtimeReconstructionResult result)
    {
        var current = Volatile.Read(ref state.ContactSubspaceEvidence);
        if (result.ContactJacobian is { } contactJacobian)
        {
            var measurementSpace = string.IsNullOrWhiteSpace(result.ContactJacobianMeasurementSpace)
                ? contactJacobian.GetLength(0) switch
                {
                    208 => EcdCwrContactSubspaceEvidenceInput.Amplitude208,
                    416 => EcdCwrContactSubspaceEvidenceInput.ComplexStacked416,
                    _ => string.Empty
                }
                : result.ContactJacobianMeasurementSpace.Trim();
            var source = string.IsNullOrWhiteSpace(result.ContactJacobianSource)
                ? $"{result.OutputHdf5Path}#contact_jacobian_208x16"
                : result.ContactJacobianSource.Trim();
            Volatile.Write(
                ref state.ContactSubspaceEvidence,
                new EcdCwrContactSubspaceEvidenceInput(
                    contactJacobian,
                    measurementSpace,
                    source,
                    result.ContactJacobianStatus ?? "available: optional realtime J_z"));
            return;
        }

        if (current.ContactJacobian is null)
        {
            Volatile.Write(
                ref state.ContactSubspaceEvidence,
                EcdCwrContactSubspaceEvidenceInput.Unavailable(
                    result.ContactJacobianStatus ??
                    "unavailable: selected backend did not emit /contact_jacobian_208x16"));
        }
    }

    private static double? RefineImageQuality(
        ElectrodeContactDiagnosticResult? contactResult,
        RealtimeReconstructionResult reconstructionResult)
    {
        if (contactResult is null)
        {
            return null;
        }

        return new EcdCwrImageQualityEstimator().Estimate(new EcdCwrImageQualityInput(
            contactResult.States,
            contactResult.MeasurementWeight208,
            contactResult.FaultTypes,
            ConditionNumber: reconstructionResult.WeightedSystemConditionNumber,
            VoltageFitResidualNorm: reconstructionResult.VoltageFitResidualNorm,
            VoltageFitRelativeResidual: reconstructionResult.VoltageFitRelativeResidual,
            VoltageFitCosineSimilarity: reconstructionResult.VoltageFitCosineSimilarity,
            VoltageFitResidualL1Norm: reconstructionResult.VoltageFitResidualL1Norm,
            VoltageFitRelativeL1Residual: reconstructionResult.VoltageFitRelativeL1Residual,
            VoltageFitResidualLinfNorm: reconstructionResult.VoltageFitResidualLinfNorm,
            VoltageFitMeasuredNorm: reconstructionResult.VoltageFitMeasuredNorm,
            VoltageFitSimulatedNorm: reconstructionResult.VoltageFitSimulatedNorm,
            VoltageFitR2: reconstructionResult.VoltageFitR2,
            ReconstructionConductivityRange: reconstructionResult.ReconstructionConductivityRange));
    }

    private static bool ShouldLogMilestone(int blockNumber) => blockNumber <= 5 || blockNumber % 100 == 0;
}
