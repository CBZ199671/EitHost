namespace EitHost.Core.Diagnostics;

public sealed record HardwareSmokeReadiness(
    bool ReadyForSingleSetSmoke,
    IReadOnlyList<string> Blockers);
