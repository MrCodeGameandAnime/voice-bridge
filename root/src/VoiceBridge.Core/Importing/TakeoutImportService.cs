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
        var mediaMatcher = new AttachmentReferenceMatcher(mediaSourceFiles);
        var parser = new VoiceMessageParser();
        var eventParser = new VoiceEventParser();
        long messagesParsed = 0;
        long conversationsParsed = 0;
        long attachmentsParsed = 0;
        long callsParsed = 0;
        long voicemailsParsed = 0;
        long mediaReferencesParsed = 0;
        long mediaReferencesMatched = 0;
        long mediaReferencesUnresolved = 0;
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

            string html;
            MessagePageParseResult messagePage;
            try
            {
                await using var content = item.OpenRead();
                using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                messagePage = parser.Parse(html, item.SourceFile.RelativePath);
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
                        "voice_html_page_processing_failed",
                        "A Voice HTML file could not be read or processed and was retained as a source file.",
                        item.SourceFile.RelativePath),
                    ImportIssueSeverity.Error);
                issues.Add(issueRecord);
                await store.WriteIssueAsync(issueRecord, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (messagePage.IsSupportedMessagePage)
            {
                var reconstruction = reconstructor.Reconstruct(messagePage);
                if (reconstruction.Conversation is null)
                {
                    recordsSkipped++;
                }
                else
                {
                    messagesParsed += reconstruction.Conversation.Messages.Count;
                    conversationsParsed++;
                    var messageAttachments = reconstruction.Conversation.Messages
                    .SelectMany(message => message.AttachmentReferences ?? [])
                    .ToArray();
                    attachmentsParsed += messageAttachments.Length;
                    mediaReferencesParsed += messageAttachments.Length;
                    mediaReferencesMatched += messageAttachments.LongCount(attachment => attachment.MatchedRelativePath is not null);
                    mediaReferencesUnresolved += messageAttachments.LongCount(attachment => attachment.MatchedRelativePath is null);
                    await store.WriteConversationAsync(reconstruction.Conversation, cancellationToken).ConfigureAwait(false);
                }

                var messageIssues = reconstruction.Conversation is null ? messagePage.Issues : reconstruction.Issues;
                foreach (var issue in messageIssues)
                {
                    await WriteWarningAsync(issue).ConfigureAwait(false);
                }

                continue;
            }

            var eventResult = eventParser.Parse(html, item.SourceFile.RelativePath);
            if (!eventResult.IsSupportedEventPage)
            {
                recordsSkipped++;
            }

            foreach (var issue in eventResult.Issues)
            {
                await WriteWarningAsync(issue).ConfigureAwait(false);
            }

            if (eventResult.CallRecord is not null)
            {
                var call = ResolveMediaReferences(eventResult.CallRecord, mediaMatcher, out var mediaIssues);
                callsParsed++;
                var references = call.MediaReferences ?? [];
                mediaReferencesParsed += references.Count;
                mediaReferencesMatched += references.LongCount(reference => reference.MatchStatus == "matched");
                mediaReferencesUnresolved += references.LongCount(reference => reference.MatchStatus is "unresolved" or "ambiguous");
                await store.WriteCallAsync(call, cancellationToken).ConfigureAwait(false);
                foreach (var issue in mediaIssues)
                {
                    await WriteWarningAsync(issue).ConfigureAwait(false);
                }
            }

            if (eventResult.Voicemail is not null)
            {
                var voicemail = ResolveMediaReferences(eventResult.Voicemail, mediaMatcher, out var mediaIssues);
                voicemailsParsed++;
                var references = voicemail.MediaReferences ?? [];
                mediaReferencesParsed += references.Count;
                mediaReferencesMatched += references.LongCount(reference => reference.MatchStatus == "matched");
                mediaReferencesUnresolved += references.LongCount(reference => reference.MatchStatus is "unresolved" or "ambiguous");
                await store.WriteVoicemailAsync(voicemail, cancellationToken).ConfigureAwait(false);
                foreach (var issue in mediaIssues)
                {
                    await WriteWarningAsync(issue).ConfigureAwait(false);
                }
            }
        }

        ReportProgress(new ImportProgress(ImportProgressStage.Finalizing, voiceHtmlItems.Length, voiceHtmlItems.Length, "Finalizing local database and report"));
        var finishedAt = DateTimeOffset.UtcNow;
        var unsupported = issues
            .Where(item => item.Code.StartsWith("unsupported_", StringComparison.Ordinal))
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);
        var report = new ImportReport(
            identity,
            startedAt,
            finishedAt,
            scanReport.FilesScanned,
            new ImportRecordCounts(
                messagesParsed,
                conversationsParsed,
                attachmentsParsed,
                sourceFiles.Length,
                callsParsed,
                voicemailsParsed,
                mediaReferencesParsed,
                mediaReferencesMatched,
                mediaReferencesUnresolved),
            recordsSkipped,
            issues.LongCount(issue => issue.Severity == ImportIssueSeverity.Warning),
            errors,
            unsupported,
            issues);
        await store.CompleteAsync(report, cancellationToken).ConfigureAwait(false);
        ReportProgress(new ImportProgress(ImportProgressStage.Completed, voiceHtmlItems.Length, voiceHtmlItems.Length, "Import complete"));
        return report;

        async ValueTask WriteWarningAsync(ImportIssue issue)
        {
            var issueRecord = new ImportIssueRecord(issue, ImportIssueSeverity.Warning);
            issues.Add(issueRecord);
            await store.WriteIssueAsync(issueRecord, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CallRecord ResolveMediaReferences(
        CallRecord call,
        AttachmentReferenceMatcher matcher,
        out IReadOnlyList<ImportIssue> issues)
    {
        var resolved = ResolveReferences(call.MediaReferences ?? [], call.SourceRelativePath, matcher, "call", out var localIssues);
        issues = localIssues;
        return call with { MediaReferences = resolved };
    }

    private static Voicemail ResolveMediaReferences(
        Voicemail voicemail,
        AttachmentReferenceMatcher matcher,
        out IReadOnlyList<ImportIssue> issues)
    {
        var resolved = ResolveReferences(voicemail.MediaReferences ?? [], voicemail.SourceRelativePath, matcher, "voicemail", out var localIssues);
        issues = localIssues;
        var audio = resolved.FirstOrDefault(reference => reference.MediaType == "audio");
        var status = audio is null
            ? "missing_reference"
            : audio.MatchStatus;
        return voicemail with
        {
            AudioReference = audio?.RawReference,
            MatchedAudioRelativePath = audio?.MatchedRelativePath,
            AudioMatchStatus = status,
            MediaReferences = resolved
        };
    }

    private static IReadOnlyList<MediaReference> ResolveReferences(
        IReadOnlyList<MediaReference> references,
        string sourceRelativePath,
        AttachmentReferenceMatcher matcher,
        string recordKind,
        out IReadOnlyList<ImportIssue> issues)
    {
        var resolved = new MediaReference[references.Count];
        var localIssues = new List<ImportIssue>();
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            var match = matcher.Match(
                new Attachment(reference.RawReference, reference.MatchedRelativePath, reference.MediaType),
                sourceRelativePath,
                null);
            var status = match.Attachment.MatchedRelativePath is not null
                ? "matched"
                : match.Issue?.Code == "attachment_reference_ambiguous" ? "ambiguous" : "unresolved";
            resolved[index] = reference with
            {
                MatchedRelativePath = match.Attachment.MatchedRelativePath,
                MediaType = match.Attachment.MediaType,
                MatchStatus = status
            };

            if (match.Issue is not null)
            {
                localIssues.Add(new ImportIssue(
                    $"{recordKind}_media_reference_{status}",
                    status == "ambiguous"
                        ? "The event media reference matches multiple source files and was left unresolved."
                        : "The event media reference did not match a source media file and was left unresolved.",
                    sourceRelativePath));
            }
        }

        issues = localIssues;
        return resolved;
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
