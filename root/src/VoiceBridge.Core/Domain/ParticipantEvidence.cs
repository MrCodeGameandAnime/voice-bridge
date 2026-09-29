namespace VoiceBridge.Core.Domain;

public enum ParticipantEvidenceSource
{
    MessageSender,
    PageParticipant
}

public sealed record ParticipantEvidence(
    Participant Participant,
    ParticipantEvidenceSource Source,
    int? SourceRowIndex);
