using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Scanning;

namespace VoiceBridge.Core.Importing;

public interface ITakeoutImportStoreFactory
{
    ITakeoutImportStore Create(string databasePath);
}

public interface ITakeoutImportStore : IAsyncDisposable
{
    ValueTask InitializeAsync(
        ImportInputIdentity inputIdentity,
        DateTimeOffset startedAt,
        ScanReport scanReport,
        IReadOnlyList<SourceFile> sourceFiles,
        CancellationToken cancellationToken);

    ValueTask WriteConversationAsync(Conversation conversation, CancellationToken cancellationToken);

    ValueTask WriteIssueAsync(ImportIssueRecord issue, CancellationToken cancellationToken);

    ValueTask CompleteAsync(ImportReport report, CancellationToken cancellationToken);
}
