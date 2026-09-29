namespace VoiceBridge.Core.Domain;

public sealed record Voicemail(
    string SourceRelativePath,
    string? RawTimestamp,
    DateTimeOffset? Timestamp,
    string? Transcript,
    string? AudioReference,
    TimeSpan? Duration,
    string? RawContact = null,
    string? RawPhoneNumber = null,
    string? RawDurationTitle = null,
    string? DurationDisplayText = null,
    string? MatchedAudioRelativePath = null,
    string AudioMatchStatus = "not_checked",
    IReadOnlyList<MediaReference>? MediaReferences = null,
    string? RawFilenameContact = null,
    string? RawContactSource = null);
