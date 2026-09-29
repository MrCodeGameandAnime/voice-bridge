namespace VoiceBridge.Core.Domain;

public sealed record CallRecord(
    string SourceRelativePath,
    string? RawEventType,
    string? RawTimestamp,
    DateTimeOffset? Timestamp,
    string? RawContact,
    string? RawPhoneNumber,
    TimeSpan? Duration);
