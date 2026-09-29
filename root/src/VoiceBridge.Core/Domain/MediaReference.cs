namespace VoiceBridge.Core.Domain;

public sealed record MediaReference(
    string RawReference,
    string? MediaType,
    string? MatchedRelativePath = null,
    string MatchStatus = "not_checked");
