using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Parsing;

public sealed record MessagePageParseResult(
    bool IsSupportedMessagePage,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<Participant> PageParticipantEvidence,
    IReadOnlyList<ImportIssue> Issues);
