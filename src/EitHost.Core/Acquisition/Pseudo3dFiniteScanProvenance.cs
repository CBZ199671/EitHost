using System.Text.Json;

namespace EitHost.Core.Acquisition;

/// <summary>A complete raw burst boundary, including passive samples needed to reproduce its onset search.</summary>
public sealed record Pseudo3dFiniteScanProvenance(
    Pseudo3dAcquisitionStamp Stamp, long RawStart, long RawEnd,
    long AnalysisStart, long AnalysisEnd, Pseudo3dFiniteScanWindow Window)
{
    public string Kind => "pseudo3d-finite-scan-v2";

    public static Pseudo3dFiniteScanProvenance? Parse(string reason)
    {
        if (!reason.StartsWith('{')) return null;
        using var document = JsonDocument.Parse(reason);
        if (!document.RootElement.TryGetProperty("Kind", out var kind) || kind.GetString() != "pseudo3d-finite-scan-v2") return null;
        var result = document.RootElement.Deserialize<Pseudo3dFiniteScanProvenance>() ?? throw new InvalidDataException("Missing finite scan provenance.");
        if (result.Stamp is null || result.Window is null || result.RawStart < 0 || result.RawEnd <= result.RawStart ||
            result.RawEnd - result.RawStart > Pseudo3dAcquisitionProfile.SampleRateHz * 3L ||
            result.AnalysisStart < result.RawStart || result.AnalysisEnd <= result.AnalysisStart || result.AnalysisEnd > result.RawEnd ||
            result.Window.MinimumStartRow < 16 || result.Window.MaximumStartRow < result.Window.MinimumStartRow ||
            result.Window.MaximumStartRow >= result.RawEnd - result.RawStart ||
            result.Stamp.Profile != Pseudo3dAcquisitionProfile.Version || result.Stamp.FrequencyTuningWord != Pseudo3dAcquisitionProfile.FrequencyTuningWord ||
            result.Stamp.SessionId == Guid.Empty || result.Stamp.Round < 1 || result.Stamp.Slot is < 0 or > 1)
            throw new InvalidDataException("Finite scan provenance violates its capture boundary contract.");
        return result;
    }
}
