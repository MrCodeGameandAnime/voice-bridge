namespace VoiceBridge.Core.Domain;

public enum MessageDirection
{
    Incoming,
    Outgoing
}

public sealed record Message(
    string SourceRelativePath,
    string? RawTimestamp,
    DateTimeOffset? Timestamp,
    string? Body,
    Participant? Sender,
    MessageDirection? Direction);
