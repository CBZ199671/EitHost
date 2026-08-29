using System.IO;
using System.Text.Json;

namespace EitHost.App;

internal sealed record OperatorContactSettings(
    int SchemaVersion,
    string ContactFirmwareBuildId,
    string ContactSubjectProfile)
{
    public static OperatorContactSettings Default { get; } = new(
        SchemaVersion: 1,
        ContactFirmwareBuildId: string.Empty,
        ContactSubjectProfile: "water-tank");
}

internal static class OperatorContactSettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static OperatorContactSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                return OperatorContactSettings.Default;
            }

            var restored = JsonSerializer.Deserialize<OperatorContactSettings>(
                File.ReadAllText(path),
                JsonOptions);
            return restored is { SchemaVersion: 1 } &&
                !string.IsNullOrWhiteSpace(restored.ContactSubjectProfile)
                ? restored
                : OperatorContactSettings.Default;
        }
        catch
        {
            return OperatorContactSettings.Default;
        }
    }

    public static void Save(string path, OperatorContactSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("Operator settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp";
        lock (Gate)
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
    }
}
