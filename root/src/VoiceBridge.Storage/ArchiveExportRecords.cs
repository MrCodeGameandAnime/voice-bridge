using VoiceBridge.Core.Domain;

namespace VoiceBridge.Storage;

public sealed record StoredArchiveMetadata(SourceKind SourceKind, string SourcePath, string? SourceSha256);

public sealed record StoredPage<T>(IReadOnlyList<T> Items, long TotalCount);

public sealed record StoredMediaBrowserSummary(long ReferenceCount, long SourceMediaFileCount);

public sealed record StoredConversationBrowserRow(
    long Id,
    string? RawLabel,
    string Kind,
    string SourceRelativePath,
    long MessageCount,
    string? FirstTimestampUtc,
    string? LastTimestampUtc,
    string? Preview,
    string ParticipantSummary);

public sealed record StoredMediaBrowserItem(
    string RecordType,
    long RecordId,
    long? ParentRecordId,
    string? RawReference,
    long? MatchedSourceFileId,
    string? MatchedRelativePath,
    string? MediaType,
    string MatchStatus,
    string? SourceRelativePath,
    long? SizeBytes,
    string? ContentSha256);

public sealed record StoredConversationSummary(
    long Id,
    long? SourceFileId,
    string SourceRelativePath,
    string? RawLabel,
    string Kind);

public sealed record StoredParticipantEvidence(
    long Id,
    string SourceType,
    int? SourceRowIndex,
    string? RawDisplayName,
    string? RawPhoneNumber);

public sealed record StoredParticipant(
    long Id,
    long ConversationId,
    string? NormalizedDisplayName,
    string? NormalizedPhoneNumber,
    IReadOnlyList<StoredParticipantEvidence> Evidence);

public sealed record StoredAttachment(
    long Id,
    long MessageId,
    string RawReference,
    long? MatchedSourceFileId,
    string? MatchedRelativePath,
    string? MediaType);

public sealed record StoredMessage(
    long Id,
    long ConversationId,
    long? SourceFileId,
    string SourceRelativePath,
    int? SourceRowIndex,
    string? RawTimestamp,
    string? TimestampUtc,
    string? Body,
    string? SenderDisplayName,
    string? SenderPhoneNumber,
    string? Direction,
    IReadOnlyList<StoredAttachment> Attachments);

public sealed record StoredImportIssue(
    long Id,
    string Code,
    string Message,
    string Severity,
    long? SourceFileId,
    string? SourceRelativePath,
    int? SourceRowIndex);

public sealed record StoredSourceFile(
    long Id,
    long Ordinal,
    string RelativePath,
    long SizeBytes,
    string? MediaType,
    string? ContentSha256);

public sealed record StoredSearchMessage(
    long MessageId,
    long ConversationId,
    string? TimestampUtc,
    string? Body,
    string? ConversationLabel,
    string? SenderDisplayName,
    string? SenderPhoneNumber);

public sealed record StoredMediaReference(
    long Id,
    long ParentId,
    string RawReference,
    long? MatchedSourceFileId,
    string? MatchedRelativePath,
    string? MediaType,
    string MatchStatus);

public sealed record StoredCallRecord(
    long Id,
    long? SourceFileId,
    string SourceRelativePath,
    string? RawEventType,
    string? RawTimestamp,
    string? TimestampUtc,
    string? RawContact,
    string? RawFilenameContact,
    string? RawContactSource,
    string? RawPhoneNumber,
    string? RawDurationTitle,
    string? DurationDisplayText,
    double? DurationSeconds,
    IReadOnlyList<StoredMediaReference> MediaReferences);

public sealed record StoredVoicemail(
    long Id,
    long? SourceFileId,
    string SourceRelativePath,
    string? RawTimestamp,
    string? TimestampUtc,
    string? RawContact,
    string? RawFilenameContact,
    string? RawContactSource,
    string? RawPhoneNumber,
    string? Transcript,
    string? RawDurationTitle,
    string? DurationDisplayText,
    double? DurationSeconds,
    string? AudioReference,
    long? MatchedAudioSourceFileId,
    string? MatchedAudioRelativePath,
    string AudioMatchStatus,
    IReadOnlyList<StoredMediaReference> MediaReferences);

public sealed record StoredSearchEvent(
    string RecordType,
    long RecordId,
    string? TimestampUtc,
    string? Label,
    string? Contact,
    string? FilenameContact,
    string? PhoneNumber,
    string? Body);
