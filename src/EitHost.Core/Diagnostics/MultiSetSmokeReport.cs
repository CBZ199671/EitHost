using EitHost.Core.Sync;

namespace EitHost.Core.Diagnostics;

public sealed record MultiSetSmokeReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Ready,
    bool ExecuteRequested,
    bool Passed,
    string Status,
    SingleSetSmokeHardwareSummary Hardware,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<MultiSetSmokeSetReport> Sets,
    IReadOnlyList<SyncSetStartRecord> SyncRecords,
    IReadOnlyList<string> Warnings);

public sealed record MultiSetSmokeSetReport(
    SingleSetSmokePairing Pairing,
    SingleSetSmokeDdsCommand? StartExcitationCommand,
    SingleSetSmokeDdsCommand? StopExcitationCommand,
    SingleSetSmokeAcquisition? Acquisition,
    SingleSetSmokeArtifacts? Artifacts);
