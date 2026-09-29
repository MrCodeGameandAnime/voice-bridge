namespace VoiceBridge.Core.Domain;

public enum ImportRunStatus
{
    Created,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record ImportRun(
    Guid Id,
    SourceArchive Source,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    ImportRunStatus Status);
