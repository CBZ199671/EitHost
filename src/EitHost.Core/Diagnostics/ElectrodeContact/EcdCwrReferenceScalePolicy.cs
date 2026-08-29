namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrReferenceScalePolicy
{
    public const string PreservePhysicalScale = "preserve_physical_scale";
    public const string CommonScaleNormalized = "common_scale_normalized";

    public static string Normalize(string? policy)
    {
        return policy?.Trim() switch
        {
            null or "" or PreservePhysicalScale => PreservePhysicalScale,
            CommonScaleNormalized => CommonScaleNormalized,
            _ => throw new ArgumentException("Unsupported realtime reference scale policy.", nameof(policy))
        };
    }

    public static bool UsesCommonScaleNormalization(string? policy)
    {
        return string.Equals(
            Normalize(policy),
            CommonScaleNormalized,
            StringComparison.Ordinal);
    }
}
