using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Parsing;
using VoiceBridge.Core.Reconstruction;
using VoiceBridge.Core.Scanning;

namespace VoiceBridge.Core.Importing;

public sealed class TakeoutImportService(ITakeoutImportStoreFactory storeFactory)
{
    public event EventHandler<ImportProgress>? ProgressChanged;

    public async Task<ImportReport> ImportAsync(
        string sourcePath,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(storeFactory);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullDatabasePath = Path.GetFullPath(databasePath);
        SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(fullSourcePath, Path.GetDirectoryName(fullDatabasePath)!);
        var startedAt = DateTimeOffset.UtcNow;
        ReportProgress(new ImportProgress(ImportProgressStage.Scanning, 0, 1, "Scanning source archive"));

        var scanResult = new TakeoutScanner().Scan(fullSourcePath, cancellationToken);
        if (!scanResult.IsSuccess)
        {
            throw new InvalidDataException($"{scanResult.Error!.Code}: {scanResult.Error.Message}");
        }

        var scanReport = scanResult.Value!;
        var sourceItems = OpenSourceItems(fullSourcePath, scanReport.SourceKind);
        if (sourceItems.Items.Count != scanReport.FilesScanned)
        {
            await sourceItems.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("Source inventory changed while the import was being prepared.");
        }

        await using var sourceLifetime = sourceItems;
        var sourceFiles = sourceItems.Items.Select(item => item.SourceFile).ToArray();
        var rootIsVoice = IsVoiceRoot(fullSourcePath, scanReport.SourceKind);
        var sourceFileHashIssues = new List<ImportIssueRecord>();
        if (scanReport.SourceKind == SourceKind.Directory)
        {
            for (var index = 0; index < sourceItems.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = sourceItems.Items[index];
                if (!IsMediaSourceFile(item.SourceFile) || !IsVoicePath(item.SourceFile.RelativePath, rootIsVoice))
                {
                    continue;
                }

                try
                {
                    await using var mediaStream = item.OpenRead();
                    if (mediaStream.CanSeek && mediaStream.Length != item.SourceFile.SizeBytes)
                    {
                        throw new InvalidDataException("The media file size changed after the source inventory was scanned.");
                    }

                    var hash = await ComputeSha256Async(mediaStream, cancellationToken).ConfigureAwait(false);
                    sourceFiles[index] = item.SourceFile with { ContentSha256 = hash };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    sourceFileHashIssues.Add(new ImportIssueRecord(
                        new ImportIssue(
                            "media_source_hash_unavailable",
                            "A media source file could not be verified during import; its reference is retained without copied media.",
                            item.SourceFile.RelativePath),
                        ImportIssueSeverity.Warning));
                }
            }
        }

        var sourceSha256 = scanReport.SourceKind == SourceKind.ZipArchive
            ? await ComputeSha256Async(fullSourcePath, cancellationToken).ConfigureAwait(false)
            : null;
        var identity = new ImportInputIdentity(scanReport.SourceKind, fullSourcePath, sourceSha256, scanReport.FilesScanned);
        await using var store = storeFactory.Create(fullDatabasePath);
        await store.InitializeAsync(identity, startedAt, scanReport, sourceFiles, cancellationToken).ConfigureAwait(false);

        var issues = new List<ImportIssueRecord>(sourceFileHashIssues);
        foreach (var warning in scanReport.Warnings)
        {
            var issue = new ImportIssue(warning.Code, warning.Message, warning.RelativePath);
            var record = new ImportIssueRecord(issue, ImportIssueSeverity.Warning);
            issues.Add(record);
            await store.WriteIssueAsync(record, cancellationToken).ConfigureAwait(false);
        }

        foreach (var issue in sourceFileHashIssues)
        {
            await store.WriteIssueAsync(issue, cancellationToken).ConfigureAwait(false);
        }

        var voiceHtmlItems = sourceItems.Items
            .Where(item => IsVoiceHtml(item.SourceFile.RelativePath, IsVoiceRoot(fullSourcePath, scanReport.SourceKind)))
            .ToArray();
        var mediaSourceFiles = sourceFiles
            .Where(sourceFile => IsMediaSourceFile(sourceFile) && IsVoicePath(sourceFile.RelativePath, rootIsVoice))
            .ToArray();
        var reconstructor = new ConversationReconstructor(mediaSourceFiles);
        var parser = new VoiceMessageParser();
        long messagesParsed = 0;
        long conversationsParsed = 0;
        long attachmentsParsed = 0;
        long recordsSkipped = 0;
        long errors = 0;

        for (var index = 0; index < voiceHtmlItems.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = voiceHtmlItems[index];
            ReportProgress(new ImportProgress(
                ImportProgressStage.ProcessingMessages,
                index,
                voiceHtmlItems.Length,
                $"Processing Voice HTML page {index + 1:N0} of {voiceHtmlItems.Length:N0}"));

            ConversationReconstructionResult reconstruction;
            try
            {
                await using var content = item.OpenRead();
                using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var page = parser.Parse(html, item.SourceFile.RelativePath);
                reconstruction = reconstructor.Reconstruct(page);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                errors++;
                recordsSkipped++;
                var issueRecord = new ImportIssueRecord(
                    new ImportIssue(
                        "message_page_processing_failed",
                        "A Voice HTML file could not be read or processed and was retained as a source file.",
                        item.SourceFile.RelativePath),
                    ImportIssueSeverity.Error);
                issues.Add(issueRecord);
                await store.WriteIssueAsync(issueRecord, cancellationToken).ConfigureAwait(false);
                continue;
            }

            messagesParsed += reconstruction.Conversation?.Messages.Count ?? 0;
            if (reconstruction.Conversation is not null)
            {
                conversationsParsed++;
                attachmentsParsed += reconstruction.Conversation.Messages.Sum(message => message.AttachmentReferences?.Count ?? 0);
                await store.WriteConversationAsync(reconstruction.Conversation, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                recordsSkipped++;
            }

            foreach (var issue in reconstruction.Issues)
            {
                var issueRecord = new ImportIssueRecord(issue, ImportIssueSeverity.Warning);
                issues.Add(issueRecord);
                await store.WriteIssueAsync(issueRecord, cancellationToken).ConfigureAwait(false);
            }
        }

        ReportProgress(new ImportProgress(ImportProgressStage.Finalizing, voiceHtmlItems.Length, voiceHtmlItems.Length, "Finalizing local database and report"));
        var finishedAt = DateTimeOffset.UtcNow;
        var unsupported = issues
            .Where(item => item.Code == "unsupported_message_page")
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);
        var report = new ImportReport(
            identity,
            startedAt,
            finishedAt,
            scanReport.FilesScanned,
            new ImportRecordCounts(messagesParsed, conversationsParsed, attachmentsParsed, sourceFiles.Length),
            recordsSkipped,
            issues.LongCount(issue => issue.Severity == ImportIssueSeverity.Warning),
            errors,
            unsupported,
            issues);
        await store.CompleteAsync(report, cancellationToken).ConfigureAwait(false);
        ReportProgress(new ImportProgress(ImportProgressStage.Completed, voiceHtmlItems.Length, voiceHtmlItems.Length, "Import complete"));
        return report;
    }

    private void ReportProgress(ImportProgress progress) => ProgressChanged?.Invoke(this, progress);

    private static bool IsMediaSourceFile(SourceFile sourceFile)
    {
        var extension = Path.GetExtension(sourceFile.RelativePath);
        return extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff"
            or ".3gp" or ".3gpp" or ".mp4" or ".m4v" or ".mov" or ".webm"
            or ".aac" or ".amr" or ".m4a" or ".mp3" or ".ogg" or ".wav";
    }

    private static bool IsVoiceHtml(string relativePath, bool rootIsVoice) =>
        Path.GetExtension(relativePath).Equals(".html", StringComparison.OrdinalIgnoreCase)
        && IsVoicePath(relativePath, rootIsVoice);

    private static bool IsVoicePath(string relativePath, bool rootIsVoice) => rootIsVoice
        || relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("Voice", StringComparison.OrdinalIgnoreCase));

    private static bool IsVoiceRoot(string sourcePath, SourceKind sourceKind)
    {
        var directory = sourceKind == SourceKind.Directory
            ? new DirectoryInfo(sourcePath)
            : new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        var name = sourceKind == SourceKind.Directory ? directory.Name : Path.GetFileNameWithoutExtension(sourcePath);
        return name.Equals("Voice", StringComparison.OrdinalIgnoreCase)
            || ((name.Equals("Calls", StringComparison.OrdinalIgnoreCase) || name.Equals("Spam", StringComparison.OrdinalIgnoreCase))
                && directory.Parent?.Name.Equals("Voice", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static SourceItems OpenSourceItems(string sourcePath, SourceKind sourceKind)
    {
        var items = new List<ImportSourceItem>();
        if (sourceKind == SourceKind.Directory)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", options))
            {
                var relativePath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
                var info = new FileInfo(filePath);
                var sourceFile = new SourceFile(relativePath, info.Length, GetMediaType(relativePath));
                items.Add(new ImportSourceItem(sourceFile, () => new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)));
            }

            return new SourceItems(items, null);
        }

        var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var relativePath = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (relativePath.Length == 0 || entry.Name.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                var sourceFile = new SourceFile(relativePath, entry.Length, GetMediaType(relativePath));
                items.Add(new ImportSourceItem(sourceFile, entry.Open));
            }

            return new SourceItems(items, archive);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string? GetMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" => "image",
        ".3gp" or ".3gpp" or ".mp4" or ".m4v" or ".mov" or ".webm" => "video",
        ".aac" or ".amr" or ".m4a" or ".mp3" or ".ogg" or ".wav" => "audio",
        ".html" => "text/html",
        _ => null
    };

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha256.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(sha256.GetHashAndReset());
    }

    private sealed record ImportSourceItem(SourceFile SourceFile, Func<Stream> OpenRead);

    private sealed class SourceItems(List<ImportSourceItem> items, ZipArchive? archive) : IAsyncDisposable
    {
        public IReadOnlyList<ImportSourceItem> Items { get; } = items;

        public ValueTask DisposeAsync()
        {
            archive?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
