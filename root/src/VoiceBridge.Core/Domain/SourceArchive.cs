namespace VoiceBridge.Core.Domain;

public enum SourceKind
{
    ZipArchive,
    Directory
}

public sealed record SourceArchive(string SourcePath, SourceKind Kind);
