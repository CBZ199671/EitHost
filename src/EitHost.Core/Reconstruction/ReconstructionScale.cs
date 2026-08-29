namespace EitHost.Core.Reconstruction;

public static class ReconstructionScale
{
    public const string ModelRelative = "model_relative";
    public const string PhysicalCalibrated = "physical_calibrated";
    public const string NormalizedModelProvenance =
        "unit-radius;geometry-scale=1;normalized-drive=1;use-measured-current=false;difference=raw";
    public const string CommonScaleNormalizedRelativeProvenance =
        NormalizedModelProvenance + ";boundary-common-scale=robust-median-ratio-v1";

    public static string NormalizeStatus(string? status)
    {
        return status?.Trim() switch
        {
            PhysicalCalibrated => PhysicalCalibrated,
            ModelRelative or "" or null => ModelRelative,
            _ => throw new ArgumentException("Unsupported reconstruction scale status.", nameof(status))
        };
    }

    public static string NormalizeProvenance(string status, string? provenance)
    {
        var normalizedStatus = NormalizeStatus(status);
        if (normalizedStatus == PhysicalCalibrated)
        {
            ValidatePhysicalCalibrationProvenance(provenance);
        }

        return string.IsNullOrWhiteSpace(provenance)
            ? NormalizedModelProvenance
            : provenance.Trim();
    }

    public static string ToDisplayLabel(string? status)
    {
        return NormalizeStatus(status) == PhysicalCalibrated
            ? "物理标定电导率 S/m"
            : "模型相对值（未标定 S/m）";
    }

    private static void ValidatePhysicalCalibrationProvenance(string? provenance)
    {
        if (string.IsNullOrWhiteSpace(provenance)
            || !provenance.Contains("geometry=", StringComparison.OrdinalIgnoreCase)
            || !provenance.Contains("measured-current=", StringComparison.OrdinalIgnoreCase)
            || (!provenance.Contains("baseline-calibration=", StringComparison.OrdinalIgnoreCase)
                && !provenance.Contains("device-calibration=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Physical-calibrated reconstruction requires geometry, measured-current, and baseline/device calibration provenance.",
                nameof(provenance));
        }
    }
}
