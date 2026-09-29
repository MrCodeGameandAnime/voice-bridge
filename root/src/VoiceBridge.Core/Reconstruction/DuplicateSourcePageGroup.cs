namespace VoiceBridge.Core.Reconstruction;

public sealed record DuplicateSourcePageGroup(
    string SourceContentSha256,
    IReadOnlyList<string> SourceRelativePaths);
