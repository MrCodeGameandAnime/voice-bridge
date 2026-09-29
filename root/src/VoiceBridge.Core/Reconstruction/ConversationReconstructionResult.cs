using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Reconstruction;

public sealed record ConversationReconstructionResult(
    Conversation? Conversation,
    IReadOnlyList<ImportIssue> Issues);
