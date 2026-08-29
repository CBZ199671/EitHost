using System.IO;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeReconstructionCallbacks(
    Action<string> Diagnostic,
    Action<string, string?, string?, string?, string?> PublishQualityAxes,
    Action<string, string> PublishReconstructionActivity,
    Action<string, RealtimeReconstructionResult, DateTimeOffset> PublishPseudo3dLayer,
    Action<string, RealtimeReconstructionResult, double, RealtimeRunState> PublishRoiMeasurement,
    Action<string> PublishProvisionalRoiUnavailable,
    Action<string> QueueLog,
    Action<IReadOnlyList<string>, string?> PublishUi,
    Func<RealtimeRunState, bool> ShouldRenderBoundaryFit,
    Func<RealtimeRunState, bool> ShouldRenderImage,
    Func<RealtimeRunState, bool> ShouldPublishStatus);

internal sealed class RealtimeReconstructionController
{
    private const int MaxConsecutiveFailures = 3;
    internal static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(20);
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
        completedReconstructionFrames <= 0 ? WarmupTimeout : RequestTimeout;

    internal static TimeSpan GetRequestTimeout(
        int completedReconstructionFrames,
        bool backendSessionResetPending) =>
        backendSessionResetPending
            ? WarmupTimeout
            : GetRequestTimeout(completedReconstructionFrames);

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
        var timeout = GetRequestTimeout(
            Volatile.Read(ref state.ReconstructionFrames),
            config.EnableDynamicKalman &&
            !degradedDemodulation &&
            state.DynamicKalmanResetPending);
        try
        {
            var dynamicGeneration = state.DynamicKalmanGeneration;
            if (ShouldLogMilestone(block.BlockNumber))
            {
                callbacks.Diagnostic(
                    $"{config.SetLabel} reconstruction begin block={block.BlockNumber} timeout={timeout.TotalSeconds:F0}s route={config.ReconstructionRoute}");
            }

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout);
            var dynamicMode = state.DynamicKalmanForceSafeImage ||
                string.Equals(config.DynamicKalmanMode, "auto", StringComparison.Ordinal)
                    ? "fast_image"
                    : config.DynamicKalmanMode;
            var dynamicKalman = config.EnableDynamicKalman && !degradedDemodulation
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
            var result = await backend
                .ReconstructAsync(request, requestTimeout.Token)
                .WaitAsync(timeout + TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded || ShouldLogMilestone(result.BlockNumber))
            {
                callbacks.Diagnostic(result.Succeeded
                    ? $"{config.SetLabel} reconstruction ok block={result.BlockNumber} elapsed={result.BackendElapsed.TotalMilliseconds:F0}ms"
                    : $"{config.SetLabel} reconstruction failed block={block.BlockNumber}: {result.ErrorMessage}");
            }

            if (result.Succeeded)
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
                    dynamicKalman,
                    acquiredAt,
                    result).ConfigureAwait(false);
                return;
            }

            var error = result.ErrorMessage ?? "unknown reconstruction failure";
            await persistence.RecordReconstructionFailureAsync(config, state, block, error).ConfigureAwait(false);
            RegisterFailure(state, error);
            callbacks.PublishReconstructionActivity(config.SetLabel, $"重构状态：失败 · {error}");
            callbacks.PublishQualityAxes(
                config.SetLabel,
                null,
                null,
                $"重构质量：失败 · {error}",
                "ROI 就绪：否 · 当前目标重构失败");
            callbacks.PublishUi(
                [$"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon failed {error}"],
                null);
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
                waitTimeout: false).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await HandleTimeoutAsync(
                config,
                state,
                block,
                "reconstruction wait timeout",
                timeout,
                waitTimeout: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failures = RegisterFailure(state, ex.Message);
            await persistence.RecordReconstructionFailureAsync(config, state, block, ex.Message).ConfigureAwait(false);
            callbacks.Diagnostic(
                $"{config.SetLabel} reconstruction exception block={block.BlockNumber} failures={failures}: {ex}");
            callbacks.PublishReconstructionActivity(config.SetLabel, $"重构状态：异常 · {ex.Message}");
            callbacks.PublishQualityAxes(
                config.SetLabel,
                null,
                null,
                $"重构质量：失败 · {ex.Message}",
                "ROI 就绪：否 · 当前目标重构异常");
            PublishFailureUi(
                config.SetLabel,
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
        RealtimeDynamicKalmanOptions? dynamicKalman,
        DateTimeOffset acquiredAt,
        RealtimeReconstructionResult result)
    {
        if (dynamicGeneration != state.DynamicKalmanGeneration)
        {
            callbacks.Diagnostic(
                $"{config.SetLabel} discard stale Kalman result block={result.BlockNumber} ref-generation={dynamicGeneration}->{state.DynamicKalmanGeneration}");
            return;
        }

        if (result.DynamicKalmanApplied &&
            !ApplyDynamicKalmanResult(config, state, result, out result))
        {
            return;
        }

        UpdateContactSubspaceEvidence(state, result);
        state.RoiGeometry = new RealtimeRoiGeometry(result.NodeCoords, result.CellConnectivity);
        var completedFrames = state.RecordReconstructionSuccess(result.BackendElapsed, degradedDemodulation);
        var imageQualityScore = RefineImageQuality(contactResult, result);
        if (imageQualityCap is { } qualityCap)
        {
            imageQualityScore = Math.Min(imageQualityScore ?? qualityCap, qualityCap);
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
            null);
        callbacks.PublishPseudo3dLayer(config.SetLabel, result, acquiredAt);

        await persistence.PersistReconstructionResultAsync(
            config,
            state,
            block,
            result,
            imageQualityScore,
            reference,
            target,
            measurementWeights,
            weightPolicyVersion,
            result.DynamicKalmanApplied ? dynamicKalman?.SessionId : null).ConfigureAwait(false);
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
                state.ReferenceEpoch,
                degradedStatus,
                boundaryChangeDecision)) != true)
        {
            callbacks.Diagnostic($"{config.SetLabel} visualization rejected block={result.BlockNumber}");
        }

        if (publishRoiMeasurement)
        {
            callbacks.PublishRoiMeasurement(
                config.SetLabel,
                result,
                imageQualityScore ?? block.QualityWeight,
                state);
        }
        else
        {
            callbacks.PublishProvisionalRoiUnavailable(config.SetLabel);
        }

        if (result.OutputPersisted && callbacks.ShouldPublishStatus(state))
        {
            callbacks.QueueLog(
                $"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon block {result.BlockNumber} {result.BackendElapsed.TotalMilliseconds:F0}ms {result.OutputHdf5Path}");
        }
    }

    private bool ApplyDynamicKalmanResult(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeReconstructionResult current,
        out RealtimeReconstructionResult result)
    {
        result = current;
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
                    state.DynamicKalmanForceSafeImage = true;
                    state.DynamicKalmanGeneration++;
                    state.DynamicKalmanResetPending = true;
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
            state.DynamicKalmanForceSafeImage = true;
            state.DynamicKalmanGeneration++;
            state.DynamicKalmanResetPending = true;
        }
        else
        {
            state.DynamicKalmanResetPending = false;
        }

        return true;
    }

    private async Task HandleTimeoutAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        string persistenceMessage,
        TimeSpan timeout,
        bool waitTimeout)
    {
        var failures = RegisterFailure(state, "reconstruction timeout");
        await persistence.RecordReconstructionFailureAsync(config, state, block, persistenceMessage).ConfigureAwait(false);
        callbacks.Diagnostic(
            $"{config.SetLabel} reconstruction{(waitTimeout ? " wait" : string.Empty)} timeout block={block.BlockNumber} failures={failures}");
        callbacks.PublishReconstructionActivity(
            config.SetLabel,
            $"重构状态：超时 · block {block.BlockNumber} 超过 {timeout.TotalSeconds:F0}s");
        callbacks.PublishQualityAxes(
            config.SetLabel,
            null,
            null,
            waitTimeout ? "重构质量：失败 · 后端等待超时" : "重构质量：失败 · 后端超时",
            "ROI 就绪：否 · 当前目标重构超时");
        PublishFailureUi(
            config.SetLabel,
            $"{DateTime.Now:HH:mm:ss} {config.SetLabel} recon timeout block {block.BlockNumber}",
            failures,
            $"{config.SetLabel} 连续 {failures} 次重构超时，已暂停重构；采集和解调继续运行。");
    }

    private int RegisterFailure(RealtimeRunState state, string message)
    {
        var failures = state.RecordReconstructionFailure(message, MaxConsecutiveFailures);
        if (state.ReconstructionSuspended)
        {
            callbacks.Diagnostic($"{state.SetLabel} reconstruction suspended after {failures} failures: {message}");
        }

        return failures;
    }

    private void PublishFailureUi(string setLabel, string logLine, int failures, string status)
    {
        if (failures < MaxConsecutiveFailures)
        {
            callbacks.PublishUi([logLine], null);
            return;
        }

        callbacks.PublishUi(
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
