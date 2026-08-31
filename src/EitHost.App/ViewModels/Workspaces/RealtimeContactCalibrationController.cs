using System.Diagnostics;
using System.IO;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Hardware.Dds;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeContactCalibrationCallbacks(
    Action<string> Diagnostic,
    Action<string, string> PublishReferenceSummary,
    Action<string, string> PublishContactSummary,
    Action<string, bool> PublishReferenceInvalidated,
    Action CalibrationStateChanged);

internal sealed class RealtimeContactCalibrationController
{
    private const int RealtimeContactCalibrationMinimumFrames = 100;
    private const int RealtimeContactCalibrationMaximumFrames = 300;
    private static readonly TimeSpan RealtimeUiStatusInterval = TimeSpan.FromMilliseconds(250);
    private readonly string dataRootPath;
    private readonly RealtimeContactCalibrationCallbacks callbacks;

    internal RealtimeContactCalibrationController(
        string dataRootPath,
        RealtimeContactCalibrationCallbacks callbacks)
    {
        this.dataRootPath = string.IsNullOrWhiteSpace(dataRootPath)
            ? throw new ArgumentException("Data root path is required.", nameof(dataRootPath))
            : Path.GetFullPath(dataRootPath);
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal static bool IsExportableCalibration(EcdCwrHealthCalibration? calibration)
    {
        return calibration is not null &&
            calibration.FrameCount >= RealtimeContactCalibrationMinimumFrames &&
            calibration.Quality.Passed;
    }

    internal void InitializeAdaptiveThresholdState(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        DdsExecutionReceipt execution)
    {
        var fingerprint = new EcdCwrOperatingFingerprint(
            DeviceLabel: config.SetLabel,
            FirmwareVersion: execution.FirmwareVersion.ToString(),
            FirmwareBuildId: config.ContactFirmwareBuildId,
            ExcitationFrequencyHz: config.DacSettings.ActualFrequencyHz,
            DacGain: config.DacSettings.Gain,
            DacPhaseDegrees: config.DacSettings.PhaseDegrees,
            PgaGain: config.PgaGain,
            SampleRateHz: config.AcquisitionSettings.SampleRateHz,
            ChannelCycles: execution.CalculateEffectiveChannelCycles(config.DacSettings.ActualFrequencyHz),
            DiscardLeadingCycles: config.DemodDiscardLeadingCycles,
            DiscardTrailingCycles: config.DemodDiscardTrailingCycles,
            SubjectProfile: config.ContactSubjectProfile,
            AlgorithmVersion: EcdCwrDiagnosticPolicy.CurrentVersion);
        var match = new EcdCwrAdaptiveContactProfileMatcher().Select(
            fingerprint,
            LoadAdaptiveContactProfiles());
        state.ContactOperatingFingerprint = fingerprint;
        state.AdaptiveContactProfileMatch = match;
        state.AdaptiveShadowContactMonitor = match.Profile is null
            ? null
            : new EcdCwrPreReferenceContactMonitor(match.Profile.Thresholds.ApplyTo());
        var status = RealtimeContactDiagnosticController.FormatAdaptiveContactThresholdStatus(state);
        callbacks.Diagnostic($"{config.SetLabel} {status}");
        callbacks.PublishContactSummary(config.SetLabel, $"接触诊断：{status}；等待首个诊断 block。");
    }

    private IReadOnlyList<EcdCwrAdaptiveContactProfile> LoadAdaptiveContactProfiles()
    {
        var directory = Path.Combine(dataRootPath, "EcdCwrContactProfiles");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var profiles = new List<EcdCwrAdaptiveContactProfile>();
        foreach (var path in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                profiles.Add(new EcdCwrAdaptiveContactProfileStore().Load(path));
            }
            catch (Exception ex)
            {
                callbacks.Diagnostic($"adaptive contact profile skipped path={path} reason={ex.Message}");
            }
        }

        return profiles;
    }

    internal void LockBaseline(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        EcdCwrRobustReference robustReference)
    {
        if (block.Average.FullRealComponents is null || block.Average.FullImaginaryComponents is null)
        {
            callbacks.Diagnostic($"{config.SetLabel} contact qc_ref skipped: full 16x16 demod data unavailable");
            return;
        }

        try
        {
            var baseline = ElectrodeContactBaseline.FromReference(
                UnflattenFullObservation(robustReference.FullReal256),
                UnflattenFullObservation(robustReference.FullImaginary256));
            state.ContactCalibrationFrames.Clear();
            state.ContactCalibrationFrames.AddRange(state.ReferenceCandidateFrames);
            var provisionalCalibration = TryCreateRealtimeContactCalibration(
                config,
                block,
                state.ContactCalibrationFrames,
                RealtimeContactCalibrationMinimumFrames);
            state.ContactMonitor = config.EnableOutlierDetection
                ? new ElectrodeContactMonitor(
                    baseline,
                    RealtimeReferenceTolerancePolicy.CreateContactMonitorOptions(),
                    healthCalibration: provisionalCalibration)
                : null;
            state.ContactCalibration = provisionalCalibration;
            state.ExportableContactCalibration = IsExportableCalibration(provisionalCalibration)
                ? provisionalCalibration
                : null;
            UpdateRealtimeCalibrationArtifacts(config, state);
            if (state.ExportableContactCalibration is not null)
            {
                state.ContactCalibrationFrames.Clear();
                state.ReferenceCandidateFrames.Clear();
                Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
            }
            state.LatestContactResult = null;
            state.ReferenceInvalidated = false;
            callbacks.PublishReferenceInvalidated(config.SetLabel, false);
            callbacks.CalibrationStateChanged();
            callbacks.Diagnostic(
                $"{config.SetLabel} contact qc_ref locked block={block.BlockNumber} robust_frames={robustReference.FrameCount} detect={(config.EnableOutlierDetection ? "on" : "off")} comp={(config.EnableOutlierCompensation ? "on" : "off")} reference_tolerance={RealtimeReferenceTolerancePolicy.ProfileVersion}");
            callbacks.PublishContactSummary(
                config.SetLabel,
                config.EnableOutlierDetection
                    ? $"接触诊断：qc_ref 已锁定 block {block.BlockNumber}，等待下一帧。"
                    : "异常值检测：已关闭。");
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{config.SetLabel} contact qc_ref failed: {ex.Message}");
        }
    }

    private EcdCwrHealthCalibration? TryCreateRealtimeContactCalibration(
        RealtimeImagingRunConfig config,
        RealtimeDemodulatedBlock block,
        IReadOnlyList<DemodulatedFrame>? frames = null,
        int? requiredFrameCount = null)
    {
        try
        {
            var calibrationFrames = frames ?? block.Frames;
            var minimumFrameCount = requiredFrameCount ?? Math.Max(1, block.AcceptedFrameCount);
            var calibration = new EcdCwrHealthCalibrationBuilder().Create(
                new OfflineDemodulationResult(
                    block.PeakLocations,
                    calibrationFrames,
                    block.Average,
                    UsedUniformCadence: true,
                    block.UniformOffsetSamples,
                    block.EstimatedWindowSamples),
                new EcdCwrHealthCalibrationMetadata(
                    config.SetLabel,
                    config.DacSettings.ActualFrequencyHz,
                    DateTimeOffset.Now,
                    SourceLabel: $"realtime-qc-ref-block-{block.BlockNumber}"),
                new EcdCwrHealthCalibrationOptions(MinimumFrameCount: minimumFrameCount));
            return calibration.Quality.Passed ? calibration : null;
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{config.SetLabel} contact template qc_ref skipped: {ex.Message}");
            return null;
        }
    }

    private static double[,] UnflattenFullObservation(IReadOnlyList<double> values)
    {
        if (values.Count != DemodulatedFrame.FlattenedFullMeasurementCount)
        {
            throw new ArgumentException("Full observation must contain 256 values.", nameof(values));
        }

        var matrix = new double[DemodulatedFrame.StimulationCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var offset = 0;
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            for (var channel = 0; channel < DemodulatedFrame.FullMeasurementsPerStimulation; channel++)
            {
                matrix[stimulation, channel] = values[offset++];
            }
        }

        return matrix;
    }

    internal void UpdateAccumulator(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block,
        ElectrodeContactDiagnosticResult? contactResult)
    {
        if (block.Average.FullRealComponents is null || block.Average.FullImaginaryComponents is null)
        {
            return;
        }

        if (contactResult is not null &&
            contactResult.States.Any(contactState => contactState != ElectrodeContactState.Green))
        {
            return;
        }

        state.ContactCalibrationFrames.AddRange(block.Frames);
        if (state.ContactCalibrationFrames.Count > RealtimeContactCalibrationMaximumFrames)
        {
            state.ContactCalibrationFrames.RemoveRange(
                0,
                state.ContactCalibrationFrames.Count - RealtimeContactCalibrationMaximumFrames);
        }

        if (state.ContactCalibrationFrames.Count < RealtimeContactCalibrationMinimumFrames)
        {
            return;
        }

        try
        {
            var calibration = new EcdCwrHealthCalibrationBuilder().Create(
                new OfflineDemodulationResult(
                    block.PeakLocations,
                    state.ContactCalibrationFrames.ToArray(),
                    block.Average,
                    UsedUniformCadence: true,
                    block.UniformOffsetSamples,
                    block.EstimatedWindowSamples),
                new EcdCwrHealthCalibrationMetadata(
                    config.SetLabel,
                    config.DacSettings.ActualFrequencyHz,
                    DateTimeOffset.Now,
                    SourceLabel: $"realtime-qc-ref-green-window-{state.ContactCalibrationFrames.Count}"),
                new EcdCwrHealthCalibrationOptions(MinimumFrameCount: RealtimeContactCalibrationMinimumFrames));
            if (!calibration.Quality.Passed)
            {
                return;
            }

            var wasUnavailable = state.ExportableContactCalibration is null;
            state.ExportableContactCalibration = calibration;
            state.ContactCalibration = calibration;
            state.ContactMonitor?.SetHealthCalibration(calibration);
            UpdateRealtimeCalibrationArtifacts(config, state);
            TryCreateAuthorizedAdaptiveContactProfile(config, state, state.ContactCalibrationFrames);
            state.ContactCalibrationFrames.Clear();
            state.ReferenceCandidateFrames.Clear();
            Volatile.Write(ref state.ReferenceCandidateStrictGreenCount, 0);
            if (wasUnavailable)
            {
                callbacks.Diagnostic(
                    $"{config.SetLabel} exportable contact calibration ready frames={calibration.FrameCount} p99={calibration.Quality.Contact48WhitenedResidualP99:G3}");
                callbacks.PublishReferenceSummary(
                    config.SetLabel,
                    $"参考帧：正式健康标定已就绪，frames={calibration.FrameCount}，P99={calibration.Quality.Contact48WhitenedResidualP99:G3}。");
            }

            if (wasUnavailable)
            {
                callbacks.CalibrationStateChanged();
            }
        }
        catch (Exception ex)
        {
            if (ShouldUpdateRealtimeStatus(state))
            {
                callbacks.Diagnostic($"{config.SetLabel} exportable contact calibration pending: {ex.Message}");
            }
        }
    }

    private void UpdateRealtimeCalibrationArtifacts(
        RealtimeImagingRunConfig config,
        RealtimeRunState state)
    {
        if (!IsExportableCalibration(state.ExportableContactCalibration) ||
            state.RobustReference is null ||
            state.ReferenceBlockNumber <= 0)
        {
            return;
        }

        try
        {
            state.ExportableDeviceCalibration ??= new EcdCwrDeviceCalibrationBuilder().Create(
                state.ExportableContactCalibration!);
            state.ExportableSessionCalibration ??= new EcdCwrSessionCalibrationBuilder().Create(
                state.ExportableContactCalibration!,
                state.RobustReference,
                config.ImagingRunId,
                state.DynamicKalmanGeneration,
                state.ReferenceBlockNumber);
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{config.SetLabel} split calibration artifact pending: {ex.Message}");
        }
    }

    private void TryCreateAuthorizedAdaptiveContactProfile(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        IReadOnlyList<DemodulatedFrame> frames)
    {
        if (!config.ContactHealthyCalibrationAuthorized ||
            state.GeneratedAdaptiveContactProfile is not null ||
            state.ContactOperatingFingerprint is null)
        {
            return;
        }

        try
        {
            var extractor = new EcdCwrPreReferenceContactScoreExtractor();
            var observations = frames
                .Where(frame => frame.FullAmplitudes is not null)
                .Select(frame => extractor.Extract(
                    frame.FullAmplitudes!,
                    highQuality: true,
                    knownAllConnected: true))
                .ToArray();
            var profile = new EcdCwrAdaptiveContactProfileBuilder().Create(
                state.ContactOperatingFingerprint,
                observations,
                DateTimeOffset.Now,
                sourceLabel: $"realtime-authorized-all-connected-{config.SetLabel}");
            var directory = Path.Combine(dataRootPath, "EcdCwrContactProfiles");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{profile.ProfileId}.json");
            new EcdCwrAdaptiveContactProfileStore().Save(path, profile);
            state.GeneratedAdaptiveContactProfile = profile;
            callbacks.Diagnostic(
                $"{config.SetLabel} adaptive contact profile saved id={profile.ProfileId} frames={profile.HealthyFrameCount} path={path}");
            callbacks.PublishReferenceSummary(
                config.SetLabel,
                $"健康阈值配置已生成：{profile.ProfileId}，Y={profile.Thresholds.YellowEntry:F2}，R={profile.Thresholds.RedEntry:F2}；下次相同工况以影子模式验证。");
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{config.SetLabel} adaptive contact profile not created: {ex.Message}");
            callbacks.PublishReferenceSummary(
                config.SetLabel,
                $"健康阈值配置未生成：{ex.Message}");
        }
    }

    internal EcdCwrWaveformTemplateDisplayPackage? BuildTemplateDisplayPackage(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!config.EnableOutlierCompensation || state.ContactCalibration is null)
        {
            return null;
        }

        try
        {
            return new EcdCwrWaveformTemplateDisplayBuilder().Build(
                CreateAverageFrameForTemplateDisplay(block),
                state.ContactCalibration);
        }
        catch (Exception ex)
        {
            callbacks.Diagnostic($"{config.SetLabel} template display skipped block={block.BlockNumber}: {ex.Message}");
            return null;
        }
    }

    private static DemodulatedFrame CreateAverageFrameForTemplateDisplay(RealtimeDemodulatedBlock block)
    {
        return new DemodulatedFrame(
            block.BlockNumber,
            checked((int)Math.Clamp(block.StartSampleIndex, int.MinValue, int.MaxValue)),
            checked((int)Math.Clamp(block.EndSampleIndex, int.MinValue, int.MaxValue)),
            block.Average.Amplitudes,
            block.Average.RealComponents,
            block.Average.ImaginaryComponents,
            WindowQualities: [],
            block.Average.SampleCounts,
            block.Average.FullAmplitudes,
            block.Average.FullRealComponents,
            block.Average.FullImaginaryComponents,
            block.Average.FullSampleCounts);
    }

    private static bool ShouldUpdateRealtimeStatus(RealtimeRunState state)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref state.LastStatusTicks);
        var intervalTicks = (long)(RealtimeUiStatusInterval.TotalSeconds * Stopwatch.Frequency);
        if (previous != 0 && now - previous < intervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref state.LastStatusTicks, now, previous) == previous;
    }
}
