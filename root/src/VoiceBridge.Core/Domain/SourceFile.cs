namespace VoiceBridge.Core.Domain;

public sealed record SourceFile(string RelativePath, long SizeBytes, string? MediaType, string? ContentSha256 = null);
