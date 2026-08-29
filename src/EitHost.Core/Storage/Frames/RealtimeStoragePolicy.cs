namespace EitHost.Core.Storage.Frames;

public enum RealtimeStorageMode
{
    FullRecord = 0,
    Preview = 1
}

public sealed record RealtimeStoragePolicy(
    RealtimeStorageMode Mode,
    bool PersistContinuousRaw,
    bool PersistAllDemodulatedBlocks,
    bool PersistFullComplex256,
    bool KeepRawRingBuffer,
    bool PersistImagingFrames)
{
    public const string DebugValue = "debug";
    public const string ExperimentValue = "experiment";
    public const string ImagingValue = "imaging";
    public const string FullRecordValue = "full_record";
    public const string PreviewValue = "preview";
    public const string DefaultValue = FullRecordValue;

    public string Value => Mode == RealtimeStorageMode.Preview ? PreviewValue : FullRecordValue;

    public static RealtimeStoragePolicy From(string? value)
    {
        return Normalize(value) switch
        {
            PreviewValue => new RealtimeStoragePolicy(
                RealtimeStorageMode.Preview,
                PersistContinuousRaw: false,
                PersistAllDemodulatedBlocks: false,
                PersistFullComplex256: false,
                KeepRawRingBuffer: false,
                PersistImagingFrames: false),
            _ => new RealtimeStoragePolicy(
                RealtimeStorageMode.FullRecord,
                PersistContinuousRaw: true,
                PersistAllDemodulatedBlocks: true,
                PersistFullComplex256: true,
                KeepRawRingBuffer: false,
                PersistImagingFrames: true)
        };
    }

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            PreviewValue => PreviewValue,
            FullRecordValue or DebugValue or ImagingValue or ExperimentValue => FullRecordValue,
            _ => DefaultValue
        };
    }
}
