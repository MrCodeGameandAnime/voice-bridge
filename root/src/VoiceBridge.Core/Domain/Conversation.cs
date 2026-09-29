namespace VoiceBridge.Core.Domain;

public sealed record Conversation(string SourceRelativePath, string? RawLabel)
{
    public ConversationKind Kind { get; init; } = ConversationKind.Unknown;

    public IReadOnlyList<ConversationParticipant> Participants { get; init; } = [];

    public IReadOnlyList<Message> Messages { get; init; } = [];
}
