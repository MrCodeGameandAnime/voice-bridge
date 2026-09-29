using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Importing;

public enum ImportIssueSeverity
{
    Warning,
    Error
}

public sealed record ImportIssueRecord(
    string Code,
    string Message,
    string? SourceRelativePath,
    int? SourceRowIndex,
    ImportIssueSeverity Severity)
{
    public ImportIssueRecord(ImportIssue issue, ImportIssueSeverity severity)
        : this(issue.Code, issue.Message, issue.SourceRelativePath, issue.SourceRowIndex, severity)
    {
    }
}

public sealed record ImportInputIdentity(
    SourceKind SourceKind,
    string SourcePath,
    string? SourceSha256,
    long FilesScanned);

public sealed record ImportRecordCounts(
    long Messages,
    long Conversations,
    long Attachments,
    long SourceFiles);

public sealed record ImportReport(
    ImportInputIdentity InputIdentity,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long FilesScanned,
    ImportRecordCounts RecordsParsed,
    long RecordsSkipped,
    long Warnings,
    long Errors,
    IReadOnlyDictionary<string, long> UnsupportedStructures,
    IReadOnlyList<ImportIssueRecord> Issues);
