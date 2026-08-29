namespace EitHost.Core.Logging;

public sealed record AuditLogEntry(
    DateTimeOffset TimestampUtc,
    string Category,
    string Message,
    string? SessionId = null,
    string? RunId = null,
    string? SetLabel = null,
    string? PacketHex = null);
