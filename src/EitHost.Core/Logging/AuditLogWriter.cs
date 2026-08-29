using System.Text.Json;

namespace EitHost.Core.Logging;

public sealed class AuditLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public void Append(string logPath, AuditLogEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(entry);

        var directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(logPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
    }

    public IReadOnlyList<AuditLogEntry> ReadAll(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        if (!File.Exists(logPath))
        {
            return [];
        }

        return File.ReadLines(logPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<AuditLogEntry>(line, JsonOptions)!)
            .ToArray();
    }
}
