using VoiceBridge.Core.Domain;

namespace VoiceBridge.Storage;

public sealed record StoredArchiveMetadata(SourceKind SourceKind, string SourcePath, string? SourceSha256);

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
