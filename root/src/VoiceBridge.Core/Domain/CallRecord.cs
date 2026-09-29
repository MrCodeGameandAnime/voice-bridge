namespace VoiceBridge.Core.Domain;

public sealed record CallRecord(
    string SourceRelativePath,
    string? RawEventType,
    string? RawTimestamp,
    DateTimeOffset? Timestamp,
    string? RawContact,
    string? RawPhoneNumber,
    TimeSpan? Duration,
    string? RawDurationTitle = null,
    string? DurationDisplayText = null,
    IReadOnlyList<MediaReference>? MediaReferences = null,
    string? RawFilenameContact = null,
    string? RawContactSource = null);
