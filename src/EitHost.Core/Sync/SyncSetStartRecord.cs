namespace EitHost.Core.Sync;

public sealed record SyncSetStartRecord(
    string Label,
    DateTimeOffset? AcquisitionStartRequestedAt,
    DateTimeOffset? ExcitationStartRequestedAt);
