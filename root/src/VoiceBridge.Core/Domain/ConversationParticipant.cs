namespace VoiceBridge.Core.Domain;

public sealed record ConversationParticipant(
    string? NormalizedDisplayName,
    string? NormalizedPhoneNumber,
    IReadOnlyList<ParticipantEvidence> Evidence);
