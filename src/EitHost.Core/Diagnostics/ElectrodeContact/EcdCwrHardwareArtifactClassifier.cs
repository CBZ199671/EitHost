namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrHardwareArtifactClassifier
{
    private static readonly HashSet<string> NonExperimentFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README.md",
        "expected-artifacts.md",
        "ecd-cwr-hardware-evidence.json",
        "ecd-cwr-hardware-evidence.md",
        "ecd-cwr-hardware-evidence-audit.json",
        "ecd-cwr-hardware-evidence-audit.md"
    };

    public static bool IsExperimentArtifact(string? artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return false;
        }

        var segments = artifactPath
            .Trim()
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, "protocols", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return segments.Length > 0 && !NonExperimentFileNames.Contains(segments[^1]);
    }

    public static IReadOnlyList<string> GetExperimentArtifacts(IReadOnlyList<string>? artifacts)
    {
        return artifacts?
            .Where(IsExperimentArtifact)
            .ToArray() ?? [];
    }
}
