using System.Runtime.CompilerServices;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Frames;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class DeviceRunParameterEditor : ObservableObject
{
    private readonly Func<PairingSummaryItem?> getSelectedPairing;
    private readonly Action<string> setStatus;
    private readonly Dictionary<string, DeviceRunParameterProfile> profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private int ddsDacChannel = 1;
    private int ddsFrequencyHz = 3_125;
    private double ddsGain = 0.3;
    private int ddsPhaseDegrees;
    private int ddsPgaGain = 1;
    private DdsExcitationMode excitationMode = DdsExcitationMode.Adjacent;
    private double excitationChannelCycles = DdsProtocolConstants.DefaultExcitationChannelCycles;
    private double demodDiscardLeadingCycles = 3.0;
    private double demodDiscardTrailingCycles = 2.0;
    private int excitationScanTimes;
    private int acquisitionSampleRateHz = 200_000;
    private Usb2070AdRange acquisitionRange = Usb2070AdRange.Bipolar5V;
    private Usb2070TriggerMode acquisitionTriggerMode = Usb2070TriggerMode.Continue;
    private Usb2070TriggerSource acquisitionTriggerSource = Usb2070TriggerSource.ExternalRising;
    private int acquisitionTriggerDelay;
    private int acquisitionTriggerLength = 1024;
    private int acquisitionTriggerLevel = 2048;
    private int acquisitionReadSampleRows = 100;
    private int realtimeFramesPerBlock = 3;
    private int realtimeMinimumAcceptedFrames = 3;
    private double realtimeMeshSize = 0.08;
    private double realtimeDifferenceLambda = 1.0e-2;
    private string realtimeStorageMode = RealtimeStoragePolicy.DefaultValue;
    private bool realtimeSaveReconstructionResults;
    private bool realtimeEnableOutlierDetection = true;
    private bool realtimeEnableOutlierCompensation = true;
    private bool realtimeEnableTemporalDespiking = true;
    private bool realtimeEnableDynamicKalman = true;
    private string realtimeDynamicKalmanMode = "auto";
    private string realtimeReconstructionRoute = RealtimeReconstructionRequest.DefaultReconstructionRoute;
    private bool realtimeUseCustomLambda = true;
    private bool realtimeUseFrequencyDivisionLockIn;
    private string realtimeDifferenceOrientation = RealtimeReconstructionRequest.DefaultDifferenceOrientation;
    private string realtimeReferenceScalePolicy = EcdCwrReferenceScalePolicy.PreservePhysicalScale;
    private bool applyingProfile;

    internal DeviceRunParameterEditor(
        Func<PairingSummaryItem?> getSelectedPairing,
        Action<string> setStatus)
    {
        this.getSelectedPairing = getSelectedPairing ?? throw new ArgumentNullException(nameof(getSelectedPairing));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
    }

    internal int DdsDacChannel { get => ddsDacChannel; set => SetEdited(ref ddsDacChannel, value); }
    internal int DdsFrequencyHz { get => ddsFrequencyHz; set => SetEdited(ref ddsFrequencyHz, value); }
    internal double DdsGain { get => ddsGain; set => SetEdited(ref ddsGain, value); }
    internal int DdsPhaseDegrees { get => ddsPhaseDegrees; set => SetEdited(ref ddsPhaseDegrees, value); }
    internal int DdsPgaGain { get => ddsPgaGain; set => SetEdited(ref ddsPgaGain, value); }
    internal DdsExcitationMode ExcitationMode { get => excitationMode; set => SetEdited(ref excitationMode, value); }
    internal double ExcitationChannelCycles { get => excitationChannelCycles; set => SetEdited(ref excitationChannelCycles, value); }
    internal double DemodDiscardLeadingCycles { get => demodDiscardLeadingCycles; set => SetEdited(ref demodDiscardLeadingCycles, value); }
    internal double DemodDiscardTrailingCycles { get => demodDiscardTrailingCycles; set => SetEdited(ref demodDiscardTrailingCycles, value); }

    internal int ExcitationScanTimes
    {
        get => excitationScanTimes;
        set
        {
            if (value < 0)
            {
                setStatus("扫描圈数不能为负数；0 表示连续扫描。");
                return;
            }

            SetEdited(ref excitationScanTimes, value);
        }
    }

    internal int ExcitationOverheadUs
    {
        get => 0;
        set
        {
            if (value != 0)
            {
                OnPropertyChanged();
                SaveSelected();
            }
        }
    }

    internal int AcquisitionSampleRateHz { get => acquisitionSampleRateHz; set => SetEdited(ref acquisitionSampleRateHz, value); }
    internal Usb2070AdRange AcquisitionRange { get => acquisitionRange; set => SetEdited(ref acquisitionRange, value); }
    internal Usb2070TriggerMode AcquisitionTriggerMode { get => acquisitionTriggerMode; set => SetEdited(ref acquisitionTriggerMode, value); }
    internal Usb2070TriggerSource AcquisitionTriggerSource { get => acquisitionTriggerSource; set => SetEdited(ref acquisitionTriggerSource, value); }
    internal int AcquisitionTriggerDelay { get => acquisitionTriggerDelay; set => SetEdited(ref acquisitionTriggerDelay, value); }
    internal int AcquisitionTriggerLength { get => acquisitionTriggerLength; set => SetEdited(ref acquisitionTriggerLength, value); }
    internal int AcquisitionTriggerLevel { get => acquisitionTriggerLevel; set => SetEdited(ref acquisitionTriggerLevel, value); }
    internal int AcquisitionReadSampleRows { get => acquisitionReadSampleRows; set => SetEdited(ref acquisitionReadSampleRows, value); }

    internal int RealtimeFramesPerBlock
    {
        get => realtimeFramesPerBlock;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetEdited(ref realtimeFramesPerBlock, normalized) && realtimeMinimumAcceptedFrames > normalized)
            {
                RealtimeMinimumAcceptedFrames = normalized;
            }
        }
    }

    internal int RealtimeMinimumAcceptedFrames
    {
        get => realtimeMinimumAcceptedFrames;
        set => SetEdited(ref realtimeMinimumAcceptedFrames, Math.Clamp(value, 1, RealtimeFramesPerBlock));
    }

    internal string RealtimeBlockModeCode
    {
        get => RealtimeBlockAggregationProfile.Resolve(RealtimeFramesPerBlock, RealtimeMinimumAcceptedFrames).Code;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var profile = RealtimeBlockAggregationProfile.FromCode(value);
            RealtimeFramesPerBlock = profile.FramesPerBlock;
            RealtimeMinimumAcceptedFrames = profile.MinimumAcceptedFrames;
            OnPropertyChanged();
        }
    }

    internal string RealtimeBlockLatencySummary
    {
        get
        {
            if (DdsFrequencyHz <= 0 || ExcitationChannelCycles <= 0)
            {
                return "分块延迟：参数待修正";
            }

            var profile = RealtimeBlockAggregationProfile.Resolve(RealtimeFramesPerBlock, RealtimeMinimumAcceptedFrames);
            var latency = profile.EstimateAcquisitionLatencyMilliseconds(DdsFrequencyHz, ExcitationChannelCycles);
            var blockRate = latency > 0 ? 1000.0 / latency : 0.0;
            var usableCycles = Math.Max(
                0.0,
                ExcitationChannelCycles - DemodDiscardLeadingCycles - DemodDiscardTrailingCycles);
            return $"{GetBlockModeLabel(profile.Code)} · 接受 {RealtimeMinimumAcceptedFrames}/{RealtimeFramesPerBlock} 帧 · " +
                   $"预计采集聚合 {latency:0.###} ms ≈ {blockRate:0.###} block/s · " +
                   $"每驻留有效积分 {usableCycles:0.###}/{ExcitationChannelCycles:0.###} 周期（不含重构与界面刷新）";
        }
    }

    internal string DemodEffectiveDiscardSummary
    {
        get
        {
            try
            {
                var settings = new OfflineDemodulationSettings(
                    AcquisitionSampleRateHz,
                    DdsFrequencyHz,
                    channelCycles: ExcitationChannelCycles,
                    discardLeadingCycles: DemodDiscardLeadingCycles,
                    discardTrailingCycles: DemodDiscardTrailingCycles,
                    discardMode: DemodulationDiscardMode.Manual);
                var nominalWindowSamples = AcquisitionSampleRateHz / (double)DdsFrequencyHz * ExcitationChannelCycles;
                var discard = settings.ResolveWindowDiscard(
                    nominalWindowSamples,
                    Math.Max(2, (int)Math.Round(nominalWindowSamples)));
                var usableCycles = Math.Max(
                    0.0,
                    ExcitationChannelCycles - discard.LeadingCycles - discard.TrailingCycles);
                return $"手动有效裁剪：前 {discard.LeadingCycles:0.###} 周期 / {discard.LeadingSamples} 点，" +
                       $"后 {discard.TrailingCycles:0.###} 周期 / {discard.TrailingSamples} 点；" +
                       $"有效积分 {usableCycles:0.###}/{ExcitationChannelCycles:0.###} 周期；无隐藏裁剪";
            }
            catch (Exception)
            {
                return "手动有效裁剪：参数待修正";
            }
        }
    }

    internal double RealtimeMeshSize { get => realtimeMeshSize; set => SetEdited(ref realtimeMeshSize, Math.Max(1.0e-4, value)); }
    internal double RealtimeDifferenceLambda { get => realtimeDifferenceLambda; set => SetEdited(ref realtimeDifferenceLambda, Math.Max(1.0e-12, value)); }
    internal bool RealtimeSaveReconstructionResults
    {
        get => realtimeSaveReconstructionResults;
        set => SetEdited(
            ref realtimeSaveReconstructionResults,
            value && RealtimeStorageMode == RealtimeStoragePolicy.FullRecordValue);
    }

    internal string RealtimeStorageMode
    {
        get => realtimeStorageMode;
        set
        {
            var normalized = RealtimeStoragePolicy.Normalize(value);
            if (SetEdited(ref realtimeStorageMode, normalized) &&
                normalized == RealtimeStoragePolicy.PreviewValue &&
                realtimeSaveReconstructionResults)
            {
                realtimeSaveReconstructionResults = false;
                OnPropertyChanged(nameof(RealtimeSaveReconstructionResults));
            }
        }
    }

    internal bool RealtimeEnableOutlierDetection { get => realtimeEnableOutlierDetection; set => SetEdited(ref realtimeEnableOutlierDetection, value); }
    internal bool RealtimeEnableOutlierCompensation { get => realtimeEnableOutlierCompensation; set => SetEdited(ref realtimeEnableOutlierCompensation, value); }
    internal bool RealtimeEnableTemporalDespiking { get => realtimeEnableTemporalDespiking; set => SetEdited(ref realtimeEnableTemporalDespiking, value); }
    internal bool RealtimeEnableDynamicKalman { get => realtimeEnableDynamicKalman; set => SetEdited(ref realtimeEnableDynamicKalman, value); }
    internal string RealtimeDynamicKalmanMode { get => realtimeDynamicKalmanMode; set => SetEdited(ref realtimeDynamicKalmanMode, RealtimeDynamicKalmanOptions.NormalizeMode(value)); }
    internal string RealtimeReconstructionRoute { get => realtimeReconstructionRoute; set => SetEdited(ref realtimeReconstructionRoute, RealtimeReconstructionRequest.NormalizeReconstructionRoute(value)); }
    internal bool RealtimeUseCustomLambda { get => realtimeUseCustomLambda; set => SetEdited(ref realtimeUseCustomLambda, value); }
    internal bool RealtimeUseFrequencyDivisionLockIn { get => realtimeUseFrequencyDivisionLockIn; set => SetEdited(ref realtimeUseFrequencyDivisionLockIn, value); }
    internal string RealtimeDifferenceOrientation { get => realtimeDifferenceOrientation; set => SetEdited(ref realtimeDifferenceOrientation, RealtimeReconstructionRequest.NormalizeDifferenceOrientation(value)); }
    internal string RealtimeReferenceScalePolicy { get => realtimeReferenceScalePolicy; set => SetEdited(ref realtimeReferenceScalePolicy, EcdCwrReferenceScalePolicy.Normalize(value)); }

    internal void SaveSelected()
    {
        if (!applyingProfile && getSelectedPairing() is { } pairing)
        {
            profiles[pairing.Title] = CreateProfile();
        }
    }

    internal void Initialize(PairingSummaryItem pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        profiles[pairing.Title] = CreateProfile();
    }

    internal DeviceRunParameterProfile Get(PairingSummaryItem pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        if (!profiles.TryGetValue(pairing.Title, out var profile))
        {
            profile = CreateProfile();
            profiles[pairing.Title] = profile;
        }

        return profile;
    }

    internal void Load(PairingSummaryItem pairing)
    {
        var profile = Get(pairing);
        var migratedLegacyOverhead = profile.ExcitationOverheadUs != 0;
        applyingProfile = true;
        try
        {
            ddsDacChannel = profile.DdsDacChannel;
            ddsFrequencyHz = profile.DdsFrequencyHz;
            ddsGain = profile.DdsGain;
            ddsPhaseDegrees = profile.DdsPhaseDegrees;
            ddsPgaGain = profile.DdsPgaGain;
            excitationMode = profile.ExcitationMode;
            excitationChannelCycles = profile.ExcitationChannelCycles;
            demodDiscardLeadingCycles = profile.DemodDiscardLeadingCycles;
            demodDiscardTrailingCycles = profile.DemodDiscardTrailingCycles;
            excitationScanTimes = Math.Max(0, profile.ExcitationScanTimes);
            acquisitionSampleRateHz = profile.AcquisitionSampleRateHz;
            acquisitionRange = profile.AcquisitionRange;
            acquisitionTriggerMode = profile.AcquisitionTriggerMode;
            acquisitionTriggerSource = profile.AcquisitionTriggerSource;
            acquisitionTriggerDelay = profile.AcquisitionTriggerDelay;
            acquisitionTriggerLength = profile.AcquisitionTriggerLength;
            acquisitionTriggerLevel = profile.AcquisitionTriggerLevel;
            acquisitionReadSampleRows = profile.AcquisitionReadSampleRows;
            realtimeFramesPerBlock = profile.RealtimeFramesPerBlock;
            realtimeMinimumAcceptedFrames = Math.Clamp(profile.RealtimeMinimumAcceptedFrames, 1, realtimeFramesPerBlock);
            realtimeMeshSize = profile.RealtimeMeshSize;
            realtimeDifferenceLambda = profile.RealtimeDifferenceLambda;
            realtimeStorageMode = RealtimeStoragePolicy.Normalize(profile.RealtimeStorageMode);
            realtimeSaveReconstructionResults = profile.RealtimeSaveReconstructionResults;
            realtimeEnableOutlierDetection = profile.RealtimeEnableOutlierDetection;
            realtimeEnableOutlierCompensation = profile.RealtimeEnableOutlierCompensation;
            realtimeEnableTemporalDespiking = profile.RealtimeEnableTemporalDespiking;
            realtimeEnableDynamicKalman = profile.RealtimeEnableDynamicKalman;
            realtimeDynamicKalmanMode = profile.RealtimeDynamicKalmanMode;
            realtimeReconstructionRoute = profile.RealtimeReconstructionRoute;
            realtimeUseCustomLambda = profile.RealtimeUseCustomLambda;
            realtimeUseFrequencyDivisionLockIn = profile.RealtimeUseFrequencyDivisionLockIn;
            realtimeDifferenceOrientation = profile.RealtimeDifferenceOrientation;
            realtimeReferenceScalePolicy = EcdCwrReferenceScalePolicy.Normalize(profile.RealtimeReferenceScalePolicy);
        }
        finally
        {
            applyingProfile = false;
        }

        if (migratedLegacyOverhead || profile.ExcitationScanTimes < 0)
        {
            profiles[pairing.Title] = CreateProfile();
        }

        OnPropertyChanged(string.Empty);
        setStatus(migratedLegacyOverhead
            ? $"{pairing.Title} 已载入独立参数档；旧版人工补偿 {profile.ExcitationOverheadUs} us 已迁移为 0，时序改由固件 ACK 闭环。"
            : $"{pairing.Title} 已载入独立参数档。");
    }

    internal DeviceRunParameterProfile CreateProfile() =>
        new(
            DdsDacChannel,
            DdsFrequencyHz,
            DdsGain,
            DdsPhaseDegrees,
            DdsPgaGain,
            ExcitationMode,
            ExcitationChannelCycles,
            DemodDiscardLeadingCycles,
            DemodDiscardTrailingCycles,
            ExcitationScanTimes,
            0,
            AcquisitionSampleRateHz,
            AcquisitionRange,
            AcquisitionTriggerMode,
            AcquisitionTriggerSource,
            AcquisitionTriggerDelay,
            AcquisitionTriggerLength,
            AcquisitionTriggerLevel,
            AcquisitionReadSampleRows,
            RealtimeFramesPerBlock,
            RealtimeMinimumAcceptedFrames,
            RealtimeMeshSize,
            RealtimeDifferenceLambda,
            RealtimeStorageMode,
            RealtimeSaveReconstructionResults,
            RealtimeEnableOutlierDetection,
            RealtimeEnableOutlierCompensation,
            RealtimeEnableTemporalDespiking,
            RealtimeEnableDynamicKalman,
            RealtimeDynamicKalmanMode,
            RealtimeReconstructionRoute,
            RealtimeUseCustomLambda,
            RealtimeUseFrequencyDivisionLockIn,
            RealtimeDifferenceOrientation,
            RealtimeReferenceScalePolicy);

    internal string CreateExcitationSummary(
        IReadOnlyList<PairingSummaryItem> pairings,
        Func<string, bool> isActive)
    {
        if (pairings.Count == 0)
        {
            return FormattableString.Invariant(
                $"{DdsFrequencyHz} Hz | {FormatCurrentLabel(DdsGain)} | V{DdsDacChannel}");
        }

        return string.Join(
            Environment.NewLine,
            pairings.Select(pairing =>
            {
                var parameters = ReferenceEquals(getSelectedPairing(), pairing) ? CreateProfile() : Get(pairing);
                var state = isActive(pairing.Title) ? "RUN" : "SET";
                return FormattableString.Invariant(
                    $"{pairing.Title}: {parameters.DdsFrequencyHz} Hz | {FormatCurrentLabel(parameters.DdsGain)} | V{parameters.DdsDacChannel} | {state}");
            }));
    }

    internal static string FormatCurrentLabel(double gain) =>
        FormattableString.Invariant($"{gain * 100.0:0} uA");

    private bool SetEdited<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        SaveSelected();
        return true;
    }

    private static string GetBlockModeLabel(string code) => code switch
    {
        "fast" => "快速",
        "balanced" => "平衡",
        "stable" => "稳定（推荐）",
        "tolerant" => "容错",
        _ => "自定义",
    };
}
