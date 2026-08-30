namespace EitHost.Core.Storage.Catalog;

public static class ReconstructionLane
{
    public const string Live = "live";
    public const string OfflineComplete = "offline-complete";
    public const string LegacyOfflineIncomplete = "legacy-offline-incomplete";
    public const string LegacyLiveUnverified = "legacy-live-unverified";

    public static bool IsKnown(string value) => value is
        Live or OfflineComplete or LegacyOfflineIncomplete or LegacyLiveUnverified;
}

public static class ReconstructionRevisionStatus
{
    public const string Staged = "staged";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool IsKnown(string value) => value is Staged or Published or Failed or Canceled;
}

public static class ReconstructionFrameOutcome
{
    public const string Reconstructed = "reconstructed";
    public const string Neutral = "neutral";
    public const string ExcludedNoReference = "excluded-no-reference";
    public const string ExcludedInvalid = "excluded-invalid";
    public const string ExcludedDiscontinuity = "excluded-discontinuity";

    public static bool IsKnown(string value) => value is
        Reconstructed or Neutral or ExcludedNoReference or ExcludedInvalid or ExcludedDiscontinuity;

    public static bool IsExcluded(string value) => value is
        ExcludedNoReference or ExcludedInvalid or ExcludedDiscontinuity;
}

public sealed record ReconstructionRevisionCatalogRecord(
    Guid ExperimentRunId,
    string Lane,
    string RevisionId,
    string Status,
    string AlgorithmFingerprint,
    long RawDenominator,
    int DemodDenominator,
    int TerminalOutcomeCount,
    int ReconstructedCount,
    int NeutralCount,
    int ExcludedCount,
    long EstimatedIncrementalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt = null,
    string? FailureMessage = null)
{
    public bool IsPublished => string.Equals(
        Status,
        ReconstructionRevisionStatus.Published,
        StringComparison.Ordinal);

    public bool IsComplete =>
        IsPublished &&
        DemodDenominator == TerminalOutcomeCount &&
        TerminalOutcomeCount == ReconstructedCount + NeutralCount + ExcludedCount;
}

public sealed record ReconstructionLaneFrameCatalogRecord(
    Guid ExperimentRunId,
    string Lane,
    string RevisionId,
    int SourceBlockNumber,
    int SequenceNumber,
    string Outcome,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ProcessedAt,
    string AlgorithmFingerprint,
    string? ArtifactPath = null,
    string? DatasetPath = null,
    string? FinalWeightHash = null,
    string? KalmanSessionId = null,
    string? KalmanDisposition = null,
    string? PresentationJson = null,
    string? ExclusionReason = null,
    long? SourceStartSampleIndex = null,
    long? SourceEndSampleIndex = null,
    string? ResultHash = null);

public sealed record ReconstructionLaneCoverage(
    string Lane,
    string RevisionId,
    string Status,
    int Denominator,
    int TerminalOutcomeCount,
    int ReconstructedCount,
    int NeutralCount,
    int ExcludedCount)
{
    public bool IsPublishedComplete =>
        string.Equals(Status, ReconstructionRevisionStatus.Published, StringComparison.Ordinal) &&
        Denominator == TerminalOutcomeCount &&
        TerminalOutcomeCount == ReconstructedCount + NeutralCount + ExcludedCount;
}
