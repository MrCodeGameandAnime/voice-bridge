namespace VoiceBridge.Core.Domain;

public sealed record Attachment(string RawReference, string? MatchedRelativePath, string? MediaType);
