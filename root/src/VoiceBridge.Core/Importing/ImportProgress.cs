namespace VoiceBridge.Core.Importing;

public enum ImportProgressStage
{
    Scanning,
    ProcessingMessages,
    Finalizing,
    Completed
}

public sealed record ImportProgress(
    ImportProgressStage Stage,
    long Completed,
    long Total,
    string Message);
