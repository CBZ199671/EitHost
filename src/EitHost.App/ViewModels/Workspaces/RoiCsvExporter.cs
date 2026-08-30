using System.Globalization;
using System.Text;
using EitHost.Core.Analysis;
using EitHost.Core.Diagnostics;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal static class RoiCsvExporter
{
    internal static string BuildCurve(
        IReadOnlyList<RoiCurvePoint> series,
        string? reconstructionLane = null,
        string? reconstructionRevisionId = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("frame_index,block_number,captured_at_local,set_label,reconstruction_lane,reconstruction_revision_id,reference_epoch,reference_lock_kind,reference_epoch_boundary,reconstruction_scale_status,reconstruction_scale_provenance,roi_value_source,roi_mode,roi_id,resolution_profile_id,nominal_resolution_diameter_fraction,roi_ring,roi_sector,roi_sector_count,roi_shape,roi_center_x,roi_center_y,roi_size_fraction,roi_inner_radius_fraction,roi_outer_radius_fraction,roi_start_angle_degrees,roi_end_angle_degrees,mean_conductivity,despiked_mean_conductivity,raw_mean_conductivity,roi_filter_state,roi_filter_score,roi_filter_return_score,roi_filter_policy,roi_noise_center,roi_noise_sigma,roi_noise_sigma_multiplier,roi_noise_sample_count,roi_noise_band_ready,roi_outside_noise_band,roi_sustained_event,roi_trend_policy,selected_cell_count,area_weight,min_conductivity,max_conductivity,quality_weight");
        RoiCurvePoint? previous = null;
        foreach (var point in series)
        {
            var selection = point.RoiSelection;
            var custom = selection.CustomDefinition;
            var fixedCell = selection.FixedCell;
            var shape = fixedCell is not null
                ? fixedCell.IsCenter ? "center_disk" : "annular_sector"
                : custom?.Shape == RoiSelectionShape.Circle ? "circle" : "square";
            var roiId = fixedCell?.Id ?? $"custom-{shape}";
            builder.Append(point.FrameIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.BlockNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.CapturedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(point.SetLabel)).Append(',');
            builder.Append(Escape(reconstructionLane ?? string.Empty)).Append(',');
            builder.Append(Escape(reconstructionRevisionId ?? string.Empty)).Append(',');
            builder.Append(Format(point.ReferenceEpoch)).Append(',');
            builder.Append(Escape(point.ReferenceLockKind)).Append(',');
            builder.Append(previous is not null && !RoiVisualizationEngine.IsSameRoiReferenceEpoch(previous, point) ? "1" : "0").Append(',');
            builder.Append(ReconstructionScale.ModelRelative).Append(',');
            builder.Append(Escape(ReconstructionScale.NormalizedModelProvenance)).Append(',');
            builder.Append(Escape(point.ValueSource)).Append(',');
            builder.Append(Escape(selection.Mode)).Append(',');
            builder.Append(Escape(roiId)).Append(',');
            builder.Append(Escape(fixedCell?.ResolutionProfileId ?? string.Empty)).Append(',');
            builder.Append(Format(fixedCell is null ? null : selection.NominalResolutionDiameterFraction)).Append(',');
            builder.Append(Format(fixedCell?.RingNumber)).Append(',');
            builder.Append(Format(fixedCell?.SectorNumber)).Append(',');
            builder.Append(Format(fixedCell?.SectorCount)).Append(',');
            builder.Append(shape).Append(',');
            builder.Append(Format(custom?.CenterX)).Append(',');
            builder.Append(Format(custom?.CenterY)).Append(',');
            builder.Append(Format(custom?.SizeFraction)).Append(',');
            builder.Append(Format(fixedCell?.InnerRadiusFraction)).Append(',');
            builder.Append(Format(fixedCell?.OuterRadiusFraction)).Append(',');
            builder.Append(Format(fixedCell?.StartAngleRadians * 180.0 / Math.PI)).Append(',');
            builder.Append(Format(fixedCell?.EndAngleRadians * 180.0 / Math.PI)).Append(',');
            builder.Append(point.MeanConductivity.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.DespikedMeanConductivity.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.RawMeanConductivity.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(RoiVisualizationEngine.FormatRoiFilterStateCode(point.FilterState)).Append(',');
            builder.Append(point.FilterScore.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.FilterReturnScore.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(EcdCwrRealtimeRoiDespiker.PolicyVersion).Append(',');
            builder.Append(point.NoiseCenter.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.NoiseSigma.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.NoiseSigmaMultiplier.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.NoiseSampleCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.NoiseBandReady ? "1" : "0").Append(',');
            builder.Append(point.IsOutsideNoiseBand ? "1" : "0").Append(',');
            builder.Append(point.IsSustainedEvent ? "1" : "0").Append(',');
            builder.Append(EcdCwrRealtimeRoiTrendFilter.PolicyVersion).Append(',');
            builder.Append(point.SelectedCellCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.AreaWeight.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.MinConductivity.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.MaxConductivity.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(point.QualityWeight.ToString("G17", CultureInfo.InvariantCulture)).AppendLine();
            previous = point;
        }

        return builder.ToString();
    }

    internal static string BuildFixedTemporal(
        FixedRoiGrid grid,
        string setLabel,
        IReadOnlyList<FixedRoiTemporalAnalysis> analyses,
        string? reconstructionLane = null,
        string? reconstructionRevisionId = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("frame_index,block_number,captured_at_local,set_label,reconstruction_lane,reconstruction_revision_id,reference_epoch,reference_lock_kind,reference_epoch_boundary,reconstruction_scale_status,reconstruction_scale_provenance,roi_mode,roi_id,resolution_profile_id,nominal_resolution_diameter_fraction,roi_ring,roi_sector,roi_sector_count,roi_inner_radius_fraction,roi_outer_radius_fraction,roi_start_angle_degrees,roi_end_angle_degrees,raw_mean_conductivity,baseline_median,baseline_delta,robust_scale,z_score,arrival_series_index,arrival_frame_index,arrival_at_local,arrival_seconds_after_baseline,arrival_reached_by_frame,peak_absolute_z,quality_weight,area_weight,selected_mesh_cell_count,confidence,center_low_confidence");
        for (var analysisIndex = 0; analysisIndex < analyses.Count; analysisIndex++)
        {
            var analysis = analyses[analysisIndex];
            for (var frameIndex = 0; frameIndex < analysis.Frames.Count; frameIndex++)
            {
                var frame = analysis.Frames[frameIndex];
                for (var cellIndex = 0; cellIndex < grid.Cells.Count; cellIndex++)
                {
                    var cell = grid.Cells[cellIndex];
                    var summary = analysis.Cells[cellIndex];
                    builder.Append(frame.FrameIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.BlockNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.CapturedAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(Escape(setLabel)).Append(',');
                    builder.Append(Escape(reconstructionLane ?? string.Empty)).Append(',');
                    builder.Append(Escape(reconstructionRevisionId ?? string.Empty)).Append(',');
                    builder.Append(Format(frame.ReferenceEpoch)).Append(',');
                    builder.Append(Escape(frame.ReferenceLockKind)).Append(',');
                    builder.Append(analysisIndex > 0 && frameIndex == 0 ? "1" : "0").Append(',');
                    builder.Append(ReconstructionScale.ModelRelative).Append(',');
                    builder.Append(Escape(ReconstructionScale.NormalizedModelProvenance)).Append(',');
                    builder.Append("fixed_nominal").Append(',');
                    builder.Append(cell.Id).Append(',');
                    builder.Append(Escape(cell.ResolutionProfileId)).Append(',');
                    builder.Append(grid.ResolutionProfile.NominalResolutionDiameterFraction.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(cell.RingNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(cell.SectorNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(cell.SectorCount.ToString(CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(cell.InnerRadiusFraction.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(cell.OuterRadiusFraction.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append((cell.StartAngleRadians * 180.0 / Math.PI).ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append((cell.EndAngleRadians * 180.0 / Math.PI).ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.RawMeanConductivity[cellIndex].ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(summary.BaselineMedian.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.BaselineDelta[cellIndex].ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(summary.RobustScale.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.ZScores[cellIndex].ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(Format(summary.ArrivalSeriesIndex)).Append(',');
                    builder.Append(Format(summary.ArrivalFrameIndex)).Append(',');
                    builder.Append(summary.ArrivalAt?.ToLocalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
                    builder.Append(Format(summary.ArrivalSecondsAfterBaseline)).Append(',');
                    builder.Append(summary.ArrivalSeriesIndex is { } arrivalIndex && frameIndex >= arrivalIndex ? "true" : "false").Append(',');
                    builder.Append(summary.PeakAbsoluteZ.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.QualityWeight.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.AreaWeights[cellIndex].ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(frame.SelectedMeshCellCounts[cellIndex].ToString(CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(summary.Confidence.ToString("G17", CultureInfo.InvariantCulture)).Append(',');
                    builder.Append(cell.IsCenter ? "true" : "false").AppendLine();
                }
            }
        }

        return builder.ToString();
    }

    private static string Format(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string value) =>
        value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
