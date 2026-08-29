namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrDiagnosticPolicy
{
    public const string CurrentVersion = "ecd-cwr-current-v359-p3.4";
    public const string P2BaselineVersion = "ecd-cwr-p2-baseline-v221";

    public static string ForProfile(EcdCwrDiagnosticReplayProfile profile)
    {
        return profile switch
        {
            EcdCwrDiagnosticReplayProfile.EcdCwrCurrent => CurrentVersion,
            EcdCwrDiagnosticReplayProfile.P2Baseline => P2BaselineVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };
    }

    public static string ToPathSegment(string? version)
    {
        var source = string.IsNullOrWhiteSpace(version) ? "legacy" : version.Trim();
        var chars = source
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray();
        return new string(chars);
    }
}
