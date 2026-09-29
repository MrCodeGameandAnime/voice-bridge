namespace VoiceBridge.Core.Domain;

public sealed record Voicemail(
    string SourceRelativePath,
    string? RawTimestamp,
    DateTimeOffset? Timestamp,
    string? Transcript,
    string? AudioReference,
    TimeSpan? Duration);
