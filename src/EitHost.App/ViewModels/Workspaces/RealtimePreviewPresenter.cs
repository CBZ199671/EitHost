namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimePreviewPresenter(VisualizationWorkspaceViewModel workspace)
{
    internal RealtimePreviewUiUpdate ApplyDisplay(
        RealtimeDevicePreviewCache? cache,
        string? setLabel,
        string signalViewMode,
        string demodDisplayMode)
    {
        if (cache is null)
        {
            workspace.RealtimeRawWaveStats = "等待采集数据";
            workspace.RealtimeDemodStats = CreateEmptyDemodSummary(signalViewMode);
            workspace.RealtimeBoundaryStats = "等待边界电压";
            workspace.RealtimeImageStats = "重构图像：无。";
            workspace.RealtimeReconstructionActivity = "重构状态：等待开始";
            workspace.RealtimeRawChannel1Geometry = null;
            workspace.RealtimeRawChannel2Geometry = null;
            ClearDemod();
            workspace.RealtimeBoundaryTargetGeometry = null;
            workspace.RealtimeBoundaryReferenceGeometry = null;
            workspace.RealtimeBoundaryTemplateGeometry = null;
            ClearBoundaryAxis();
            workspace.RealtimeImageSource = null;
            ClearRoi();
            return new RealtimePreviewUiUpdate(
                ImagingSummary: null,
                ReferenceSummary: "参考帧：未锁定。",
                BaselineIntegritySummary: "基线诊断：等待参考。",
                ContactSummary: "接触诊断：等待 qc_ref。",
                MultiFrequencySummary: "多频证据：单频模式，证据 E 未启用。",
                DataQualityStatus: "数据质量：等待采集",
                ReferenceModeStatus: "参考模式：尚未锁定",
                ReconstructionQualityStatus: "重构质量：尚未开始",
                RoiReadinessStatus: "ROI 就绪：否 · 等待参考与重构",
                ReferenceInvalidated: false,
                LowConfidenceImage: false,
                [],
                LiveFrameCommit: null);
        }

        if (cache.Raw is { } raw)
        {
            workspace.RealtimeRawChannel1Geometry = raw.Channel1Geometry;
            workspace.RealtimeRawChannel2Geometry = raw.Channel2Geometry;
            workspace.RealtimeRawWaveStats = raw.Stats;
        }
        else
        {
            workspace.RealtimeRawChannel1Geometry = null;
            workspace.RealtimeRawChannel2Geometry = null;
            workspace.RealtimeRawWaveStats = "等待采集数据";
        }

        if (cache.SignalSource is { } source)
        {
            ApplyDemod(RealtimeVisualizationProjection.CreateRealtimeSignalPreviewSnapshot(
                source,
                signalViewMode,
                demodDisplayMode));
        }
        else
        {
            ClearDemod();
            workspace.RealtimeDemodStats = CreateEmptyDemodSummary(signalViewMode);
        }

        if (cache.BoundaryFit is { } boundaryFit)
        {
            ApplyBoundary(boundaryFit);
        }
        else
        {
            workspace.RealtimeBoundaryReferenceGeometry = null;
            workspace.RealtimeBoundaryTargetGeometry = null;
            workspace.RealtimeBoundaryTemplateGeometry = null;
            workspace.RealtimeBoundaryStats = "等待边界电压";
            ClearBoundaryAxis();
        }

        workspace.RealtimeImageStats = cache.ImageStats ?? "重构图像：无。";
        workspace.RealtimeReconstructionActivity = cache.ReconstructionActivity ?? "重构状态：等待开始";
        var lowConfidence = false;
        if (cache.Image is { } image)
        {
            workspace.RealtimeImageSource = image.Image;
            workspace.RealtimeImageStats = image.Stats;
            lowConfidence = image.LowConfidence;
        }
        else
        {
            workspace.RealtimeImageSource = null;
        }

        if (cache.Roi is { } roi)
        {
            ApplyRoi(roi);
        }
        else
        {
            ClearRoi();
        }

        return new RealtimePreviewUiUpdate(
            cache.Summary ?? $"{setLabel ?? "当前设备"} 暂无实时成像数据。",
            cache.ReferenceSummary ?? "参考帧：未锁定。",
            cache.BaselineIntegritySummary ?? "基线诊断：等待参考。",
            cache.ContactSummary ?? "接触诊断：等待 qc_ref。",
            cache.MultiFrequencySummary ?? "多频证据：单频模式，证据 E 未启用。",
            cache.DataQualityStatus ?? "数据质量：等待采集",
            cache.ReferenceModeStatus ?? "参考模式：尚未锁定",
            cache.ReconstructionQualityStatus ?? "重构质量：尚未开始",
            cache.RoiReadinessStatus ?? "ROI 就绪：否 · 等待参考与重构",
            cache.ReferenceInvalidated,
            lowConfidence,
            [],
            LiveFrameCommit: null);
    }

    internal RealtimePreviewUiUpdate ApplyPending(RealtimePreviewPendingBatch pending)
    {
        if (pending.Raw is { } raw)
        {
            workspace.RealtimeRawChannel1Geometry = raw.Channel1Geometry;
            workspace.RealtimeRawChannel2Geometry = raw.Channel2Geometry;
            workspace.RealtimeRawWaveStats = raw.Stats;
        }

        if (pending.Demod is { } demod)
        {
            ApplyDemod(demod);
        }

        if (pending.BoundaryFit is { } boundaryFit)
        {
            ApplyBoundary(boundaryFit);
        }

        if (!string.IsNullOrWhiteSpace(pending.ImageStats))
        {
            workspace.RealtimeImageStats = pending.ImageStats;
        }


        if (!string.IsNullOrWhiteSpace(pending.ReconstructionActivity))
        {
            workspace.RealtimeReconstructionActivity = pending.ReconstructionActivity;
        }

        bool? lowConfidence = null;
        if (pending.Image is { } image)
        {
            workspace.RealtimeImageSource = image.Image;
            workspace.RealtimeImageStats = image.Stats;
            lowConfidence = image.LowConfidence;
        }

        if (pending.Roi is { } roi)
        {
            ApplyRoi(roi);
        }

        return new RealtimePreviewUiUpdate(
            pending.Summary,
            pending.ReferenceSummary,
            pending.BaselineIntegritySummary,
            pending.ContactSummary,
            pending.MultiFrequencySummary,
            pending.DataQualityStatus,
            pending.ReferenceModeStatus,
            pending.ReconstructionQualityStatus,
            pending.RoiReadinessStatus,
            pending.ReferenceInvalidated,
            lowConfidence,
            pending.LogLines,
            pending.Image?.LiveFrameCommit);
    }

    internal void ApplyDemod(RealtimeDemodPreviewSnapshot demod)
    {
        workspace.RealtimeDemodPrimaryGeometry = demod.PrimaryGeometry;
        workspace.RealtimeDemodSecondaryGeometry = demod.SecondaryGeometry;
        workspace.RealtimeDemodGridGeometry = demod.GridGeometry;
        workspace.RealtimeDemodZeroLineGeometry = demod.ZeroLineGeometry;
        workspace.RealtimeDemodStats = demod.Stats;
        workspace.RealtimeDemodYAxisTicks = demod.AxisTicks;
    }

    internal void ClearDemod()
    {
        workspace.RealtimeDemodPrimaryGeometry = null;
        workspace.RealtimeDemodSecondaryGeometry = null;
        workspace.RealtimeDemodYAxisTicks = [];
        workspace.RealtimeDemodGridGeometry = null;
        workspace.RealtimeDemodZeroLineGeometry = null;
    }

    internal void ClearBoundaryAxis()
    {
        workspace.RealtimeBoundaryYAxisTop = string.Empty;
        workspace.RealtimeBoundaryYAxisMiddle = string.Empty;
        workspace.RealtimeBoundaryYAxisBottom = string.Empty;
    }

    private void ApplyBoundary(RealtimeBoundaryFitPreviewSnapshot boundaryFit)
    {
        workspace.RealtimeBoundaryReferenceGeometry = boundaryFit.SimulatedDeltaGeometry;
        workspace.RealtimeBoundaryTargetGeometry = boundaryFit.MeasuredDeltaGeometry;
        workspace.RealtimeBoundaryTemplateGeometry = boundaryFit.TemplateExpectedGeometry;
        workspace.RealtimeBoundaryStats = boundaryFit.Stats;
        workspace.RealtimeBoundaryYAxisTop = boundaryFit.YAxisTop;
        workspace.RealtimeBoundaryYAxisMiddle = boundaryFit.YAxisMiddle;
        workspace.RealtimeBoundaryYAxisBottom = boundaryFit.YAxisBottom;
    }

    private void ApplyRoi(RealtimeRoiPreviewSnapshot roi)
    {
        workspace.RealtimeRoiCurveGeometry = roi.CurveGeometry;
        workspace.RealtimeRoiRawCurveGeometry = roi.RawCurveGeometry;
        workspace.RealtimeRoiNoiseBandGeometry = roi.NoiseBandGeometry;
        workspace.RealtimeRoiMarkers = roi.Markers;
        workspace.RealtimeRoiAxisStart = roi.AxisStart;
        workspace.RealtimeRoiAxisMiddle = roi.AxisMiddle;
        workspace.RealtimeRoiAxisEnd = roi.AxisEnd;
        workspace.RealtimeRoiSummary = roi.Summary;
        workspace.RealtimeFixedRoiTemporal = roi.FixedTemporal;
    }

    private void ClearRoi()
    {
        workspace.RealtimeRoiCurveGeometry = null;
        workspace.RealtimeRoiRawCurveGeometry = null;
        workspace.RealtimeRoiNoiseBandGeometry = null;
        workspace.RealtimeRoiMarkers = [];
        workspace.RealtimeRoiAxisStart = string.Empty;
        workspace.RealtimeRoiAxisMiddle = string.Empty;
        workspace.RealtimeRoiAxisEnd = string.Empty;
        workspace.RealtimeRoiSummary = "ROI：等待重构图像。";
        workspace.RealtimeFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
    }

    private static string CreateEmptyDemodSummary(string signalViewMode)
    {
        return signalViewMode switch
        {
            "reference" => "参考帧 · 未锁定",
            "target" => "目标帧 · 等待采集",
            _ => "等待解调数据"
        };
    }
}

internal sealed record RealtimePreviewUiUpdate(
    string? ImagingSummary,
    string? ReferenceSummary,
    string? BaselineIntegritySummary,
    string? ContactSummary,
    string? MultiFrequencySummary,
    string? DataQualityStatus,
    string? ReferenceModeStatus,
    string? ReconstructionQualityStatus,
    string? RoiReadinessStatus,
    bool? ReferenceInvalidated,
    bool? LowConfidenceImage,
    IReadOnlyList<string> LogLines,
    RealtimeLiveFrameCommit? LiveFrameCommit);
