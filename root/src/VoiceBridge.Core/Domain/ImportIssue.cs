namespace VoiceBridge.Core.Domain;

public sealed record ImportIssue(
    string Code,
    string Message,
    string? SourceRelativePath,
    int? SourceRowIndex = null);
