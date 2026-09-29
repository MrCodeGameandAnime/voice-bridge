using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Importing;
using VoiceBridge.Storage;

namespace VoiceBridge.Export;

public sealed record ExportSummary(
    long Conversations,
    long Messages,
    long Attachments,
    long MediaCopied,
    long MediaUnavailable,
    long Calls = 0,
    long Voicemails = 0);

public static class TakeoutArchiveExporter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<ExportSummary> ExportHtmlAsync(
        string databasePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var finalPath = ValidateOutput(databasePath, outputDirectory);
        using var reader = new SqliteArchiveReader(databasePath);
        var metadata = reader.ReadArchiveMetadata();
        SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(metadata.SourcePath, finalPath);
        var conversations = reader.ReadConversations();
        var calls = reader.ReadCalls();
        var voicemails = reader.ReadVoicemails();
        var stage = CreateStageDirectory(finalPath);

        try
        {
            Directory.CreateDirectory(Path.Combine(stage, "assets"));
            Directory.CreateDirectory(Path.Combine(stage, "conversations"));
            await WriteCssAsync(stage, cancellationToken).ConfigureAwait(false);
            await WriteSearchScriptAsync(stage, cancellationToken).ConfigureAwait(false);

            var mediaPaths = new Dictionary<long, string>();
            var unavailable = new List<UnavailableMedia>();
            long mediaCopied = 0;
            using (var source = await MediaSourceReader.CreateAsync(metadata, cancellationToken).ConfigureAwait(false))
            {
                foreach (var media in reader.ReadMatchedMediaFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extension = SafeExtension(media.RelativePath);
                    var outputName = $"sourcefile-{media.Id}{extension}";
                    var relativeOutput = $"assets/{outputName}";
                    if (source.TryOpen(media, out var content, out var openFailure))
                    {
                        var partialPath = Path.Combine(stage, "assets", outputName + ".partial");
                        bool copied;
                        try
                        {
                            await using (content.ConfigureAwait(false))
                            await using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                            {
                                var copyFailure = await TryCopyMediaAsync(content, destination, media.ContentSha256, cancellationToken).ConfigureAwait(false);
                                copied = copyFailure is null;
                                openFailure = copyFailure ?? openFailure;
                            }
                        }
                        catch
                        {
                            if (File.Exists(partialPath))
                            {
                                File.Delete(partialPath);
                            }

                            throw;
                        }

                        if (copied)
                        {
                            File.Move(partialPath, Path.Combine(stage, "assets", outputName));
                            mediaPaths[media.Id] = relativeOutput;
                            mediaCopied++;
                        }
                        else
                        {
                            File.Delete(partialPath);
                            unavailable.Add(new UnavailableMedia(media.Id, media.RelativePath, openFailure ?? "source_media_unavailable"));
                        }
                    }
                    else
                    {
                        unavailable.Add(new UnavailableMedia(media.Id, media.RelativePath, openFailure));
                    }
                }

                foreach (var conversation in conversations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WriteConversationAsync(stage, reader, conversation, mediaPaths, cancellationToken).ConfigureAwait(false);
                }

                await WriteIndexAsync(stage, reader, conversations, cancellationToken).ConfigureAwait(false);
                await WriteCallsPageAsync(stage, calls, mediaPaths, cancellationToken).ConfigureAwait(false);
                await WriteVoicemailsPageAsync(stage, voicemails, mediaPaths, cancellationToken).ConfigureAwait(false);
                await WriteSearchPageAsync(stage, reader, cancellationToken).ConfigureAwait(false);
                await WriteExportReportAsync(stage, conversations.Count, reader, unavailable, source.SourceArchiveStatus, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var summary = new ExportSummary(
                conversations.Count,
                CountMessages(reader, cancellationToken),
                CountAttachments(reader, cancellationToken),
                mediaCopied,
                unavailable.Count,
                calls.Count,
                voicemails.Count);
            Directory.Move(stage, finalPath);
            return summary;
        }
        catch
        {
            DeleteStageDirectory(stage);
            throw;
        }
    }

    public static async Task<ExportSummary> ExportCsvAsync(
        string databasePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var finalPath = ValidateOutput(databasePath, outputDirectory);
        using var reader = new SqliteArchiveReader(databasePath);
        var metadata = reader.ReadArchiveMetadata();
        SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(metadata.SourcePath, finalPath);
        var conversations = reader.ReadConversations();
        var calls = reader.ReadCalls();
        var voicemails = reader.ReadVoicemails();
        var stage = CreateStageDirectory(finalPath);
        var unavailable = new List<UnavailableMedia>();

        try
        {
            using var source = await MediaSourceReader.CreateAsync(metadata, cancellationToken).ConfigureAwait(false);
            await WriteSourceArchiveCsvAsync(stage, metadata, cancellationToken).ConfigureAwait(false);
            await WriteSourceFilesCsvAsync(stage, reader, cancellationToken).ConfigureAwait(false);
            await WriteConversationsCsvAsync(stage, conversations, cancellationToken).ConfigureAwait(false);
            await WriteParticipantsCsvAsync(stage, reader, conversations, cancellationToken).ConfigureAwait(false);
            await WriteMessagesCsvAsync(stage, reader, cancellationToken).ConfigureAwait(false);
            await WriteAttachmentsCsvAsync(stage, reader, source, unavailable, cancellationToken).ConfigureAwait(false);
            await WriteCallsCsvAsync(stage, calls, cancellationToken).ConfigureAwait(false);
            await WriteCallMediaReferencesCsvAsync(stage, calls, cancellationToken).ConfigureAwait(false);
            await WriteVoicemailsCsvAsync(stage, voicemails, cancellationToken).ConfigureAwait(false);
            await WriteVoicemailMediaReferencesCsvAsync(stage, voicemails, cancellationToken).ConfigureAwait(false);
            await WriteImportIssuesCsvAsync(stage, reader, cancellationToken).ConfigureAwait(false);
            await WriteExportReportAsync(stage, conversations.Count, reader, unavailable, source.SourceArchiveStatus, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var summary = new ExportSummary(
                conversations.Count,
                CountMessages(reader, cancellationToken),
                CountAttachments(reader, cancellationToken),
                0,
                unavailable.Count,
                calls.Count,
                voicemails.Count);
            Directory.Move(stage, finalPath);
            return summary;
        }
        catch
        {
            DeleteStageDirectory(stage);
            throw;
        }
    }

    private static string ValidateOutput(string databasePath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullDatabasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullDatabasePath))
        {
            throw new FileNotFoundException("The imported archive database was not found.", fullDatabasePath);
        }

        var finalPath = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(finalPath) || File.Exists(finalPath))
        {
            throw new IOException("The export destination already exists. Choose a new output directory.");
        }

        return finalPath;
    }

    private static string CreateStageDirectory(string finalPath)
    {
        var parent = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("The output directory must have a parent directory.", nameof(finalPath));
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, $".voicebridge-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        return stage;
    }

    private static void DeleteStageDirectory(string stage)
    {
        if (Directory.Exists(stage))
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task WriteIndexAsync(
        string stage,
        SqliteArchiveReader reader,
        IReadOnlyList<StoredConversationSummary> conversations,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "index.html");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        await WriteDocumentStartAsync(writer, "Google Voice archive", "assets/site.css", cancellationToken).ConfigureAwait(false);
        var calls = reader.ReadCalls();
        var voicemails = reader.ReadVoicemails();
        await writer.WriteLineAsync($"<main><header><h1>Google Voice archive</h1><p>Offline archive generated by VoiceBridge.</p><nav><a href=\"search.html\">Search history</a> · <a href=\"calls.html\">Calls ({calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)})</a> · <a href=\"voicemails.html\">Voicemails ({voicemails.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)})</a></nav></header>").ConfigureAwait(false);
        await writer.WriteLineAsync("<h2>Conversations</h2><ol class=\"conversation-list\">").ConfigureAwait(false);

        foreach (var conversation in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var participants = reader.ReadParticipants(conversation.Id);
            var title = ConversationTitle(conversation, participants);
            var messageCount = CountMessages(reader, conversation.Id, cancellationToken);
            await writer.WriteAsync($"<li><a href=\"conversations/{conversation.Id}.html\">{Html(title)}</a><span class=\"muted\"> — {messageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} messages · {Html(conversation.Kind)}</span>").ConfigureAwait(false);
            if (participants.Count > 0)
            {
                await writer.WriteAsync($"<p class=\"muted\">{Html(ParticipantSummary(participants))}</p>").ConfigureAwait(false);
            }

            await writer.WriteLineAsync("</li>").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("</ol><footer><a href=\"export-report.json\">Export report</a></footer></main>").ConfigureAwait(false);
        await WriteDocumentEndAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCallsPageAsync(
        string stage,
        IReadOnlyList<StoredCallRecord> calls,
        IReadOnlyDictionary<long, string> mediaPaths,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "calls.html");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        await WriteDocumentStartAsync(writer, "Call history", "assets/site.css", cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync($"<main><nav><a href=\"index.html\">All history</a> · <a href=\"search.html\">Search</a></nav><header><h1>Calls</h1><p>{calls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} call records. Event labels and source values are shown as exported.</p></header><ol class=\"messages\">").ConfigureAwait(false);
        foreach (var call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contact = JoinNonEmpty(call.RawContact, call.RawPhoneNumber) ?? "Unknown contact";
            var timestamp = call.TimestampUtc ?? call.RawTimestamp;
            await writer.WriteAsync($"<li class=\"message\" id=\"call-{call.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"><article><header><strong>{Html(contact)}</strong> <span class=\"muted\">{Html(call.RawEventType ?? "Event type unknown")}</span>").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                await writer.WriteAsync($"<time datetime=\"{HtmlAttribute(timestamp)}\">{Html(timestamp)}</time>").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(call.RawTimestamp) && !string.Equals(call.RawTimestamp, timestamp, StringComparison.Ordinal))
                {
                    await writer.WriteAsync($"<small>Raw timestamp: {Html(call.RawTimestamp)}</small>").ConfigureAwait(false);
                }
            }

            await writer.WriteLineAsync("</header>").ConfigureAwait(false);
            if (call.RawContactSource is not null)
            {
                await writer.WriteLineAsync($"<p class=\"muted\">Contact evidence source: {Html(call.RawContactSource)}</p>").ConfigureAwait(false);
            }

            if (call.RawFilenameContact is not null && !string.Equals(call.RawFilenameContact, call.RawContact, StringComparison.Ordinal))
            {
                await writer.WriteLineAsync($"<p class=\"muted\">Raw filename contact label: {Html(call.RawFilenameContact)}</p>").ConfigureAwait(false);
            }

            WriteDurationEvidence(writer, call.RawDurationTitle, call.DurationDisplayText, call.DurationSeconds);
            await WriteMediaReferencesHtmlAsync(writer, call.MediaReferences, mediaPaths, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync($"<details><summary>Source record</summary><code>{Html(call.SourceRelativePath)}</code></details></article></li>").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("</ol><footer><a href=\"export-report.json\">Export report</a></footer></main>").ConfigureAwait(false);
        await WriteDocumentEndAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteVoicemailsPageAsync(
        string stage,
        IReadOnlyList<StoredVoicemail> voicemails,
        IReadOnlyDictionary<long, string> mediaPaths,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "voicemails.html");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        await WriteDocumentStartAsync(writer, "Voicemail history", "assets/site.css", cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync($"<main><nav><a href=\"index.html\">All history</a> · <a href=\"search.html\">Search</a></nav><header><h1>Voicemails</h1><p>{voicemails.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} voicemail records. Transcript and audio fields remain optional.</p></header><ol class=\"messages\">").ConfigureAwait(false);
        foreach (var voicemail in voicemails)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contact = JoinNonEmpty(voicemail.RawContact, voicemail.RawPhoneNumber) ?? "Unknown contact";
            var timestamp = voicemail.TimestampUtc ?? voicemail.RawTimestamp;
            await writer.WriteAsync($"<li class=\"message\" id=\"voicemail-{voicemail.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"><article><header><strong>{Html(contact)}</strong><span class=\"muted\"> Voicemail</span>").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                await writer.WriteAsync($"<time datetime=\"{HtmlAttribute(timestamp)}\">{Html(timestamp)}</time>").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(voicemail.RawTimestamp) && !string.Equals(voicemail.RawTimestamp, timestamp, StringComparison.Ordinal))
                {
                    await writer.WriteAsync($"<small>Raw timestamp: {Html(voicemail.RawTimestamp)}</small>").ConfigureAwait(false);
                }
            }

            await writer.WriteLineAsync("</header>").ConfigureAwait(false);
            if (voicemail.RawContactSource is not null)
            {
                await writer.WriteLineAsync($"<p class=\"muted\">Contact evidence source: {Html(voicemail.RawContactSource)}</p>").ConfigureAwait(false);
            }

            if (voicemail.RawFilenameContact is not null && !string.Equals(voicemail.RawFilenameContact, voicemail.RawContact, StringComparison.Ordinal))
            {
                await writer.WriteLineAsync($"<p class=\"muted\">Raw filename contact label: {Html(voicemail.RawFilenameContact)}</p>").ConfigureAwait(false);
            }

            WriteDurationEvidence(writer, voicemail.RawDurationTitle, voicemail.DurationDisplayText, voicemail.DurationSeconds);
            await writer.WriteAsync($"<p class=\"body\">{Html(voicemail.Transcript ?? "[No transcript recorded]")}</p>").ConfigureAwait(false);
            await WriteMediaReferencesHtmlAsync(writer, voicemail.MediaReferences, mediaPaths, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync($"<p class=\"muted\">Audio association: {Html(voicemail.AudioMatchStatus)}</p><details><summary>Source record</summary><code>{Html(voicemail.SourceRelativePath)}</code></details></article></li>").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("</ol><footer><a href=\"export-report.json\">Export report</a></footer></main>").ConfigureAwait(false);
        await WriteDocumentEndAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteDurationEvidence(StreamWriter writer, string? rawTitle, string? displayText, double? durationSeconds)
    {
        if (rawTitle is null && displayText is null && durationSeconds is null)
        {
            return;
        }

        var details = string.Join("; ", new[]
        {
            rawTitle is null ? null : $"title: {rawTitle}",
            displayText is null ? null : $"display text: {displayText}",
            durationSeconds is null ? null : $"parsed seconds: {durationSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        writer.WriteLine($"<p class=\"muted\">Duration evidence: {Html(details)}</p>");
    }

    private static async Task WriteMediaReferencesHtmlAsync(
        StreamWriter writer,
        IReadOnlyList<StoredMediaReference> references,
        IReadOnlyDictionary<long, string> mediaPaths,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            await writer.WriteLineAsync("<p class=\"muted\">No media reference recorded.</p>").ConfigureAwait(false);
            return;
        }

        await writer.WriteLineAsync("<ul class=\"attachments\">").ConfigureAwait(false);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.MatchedSourceFileId is long sourceId && mediaPaths.TryGetValue(sourceId, out var mediaPath))
            {
                if (reference.MediaType == "audio")
                {
                    await writer.WriteAsync($"<li><audio controls preload=\"none\" src=\"{HtmlAttribute(mediaPath)}\"></audio> <a href=\"{HtmlAttribute(mediaPath)}\">Open matched audio</a></li>").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync($"<li><a href=\"{HtmlAttribute(mediaPath)}\">Open matched {Html(reference.MediaType ?? "media")}</a></li>").ConfigureAwait(false);
                }
            }
            else
            {
                await writer.WriteAsync($"<li>Media {Html(reference.MatchStatus)}: <code>{Html(reference.RawReference)}</code></li>").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync("</ul>").ConfigureAwait(false);
    }

    private static async Task WriteConversationAsync(
        string stage,
        SqliteArchiveReader reader,
        StoredConversationSummary conversation,
        IReadOnlyDictionary<long, string> mediaPaths,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "conversations", $"{conversation.Id}.html");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        var participants = reader.ReadParticipants(conversation.Id);
        await WriteDocumentStartAsync(writer, ConversationTitle(conversation, participants), "../assets/site.css", cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("<main><nav><a href=\"../index.html\">All conversations</a> · <a href=\"../search.html\">Search</a></nav>").ConfigureAwait(false);
        await writer.WriteLineAsync($"<header><h1>{Html(ConversationTitle(conversation, participants))}</h1><p class=\"muted\">Conversation #{conversation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)} · {Html(conversation.Kind)}</p><details><summary>Source page</summary><code>{Html(conversation.SourceRelativePath)}</code></details></header>").ConfigureAwait(false);
        if (participants.Count > 0)
        {
            await writer.WriteLineAsync("<section><h2>Participants</h2><ul>").ConfigureAwait(false);
            foreach (var participant in participants)
            {
                var display = JoinNonEmpty(participant.NormalizedDisplayName, participant.NormalizedPhoneNumber) ?? "Unknown participant";
                await writer.WriteAsync($"<li>{Html(display)}").ConfigureAwait(false);
                if (participant.Evidence.Count > 0)
                {
                    var evidence = string.Join("; ", participant.Evidence.Select(item =>
                        $"{item.SourceType}{(item.SourceRowIndex is null ? string.Empty : $" row {item.SourceRowIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")}: {JoinNonEmpty(item.RawDisplayName, item.RawPhoneNumber) ?? "no raw label"}"));
                    await writer.WriteAsync($" <span class=\"muted\">Evidence: {Html(evidence)}</span>").ConfigureAwait(false);
                }

                await writer.WriteLineAsync("</li>").ConfigureAwait(false);
            }

            await writer.WriteLineAsync("</ul></section>").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("<section><h2>Messages</h2><ol class=\"messages\">").ConfigureAwait(false);
        foreach (var message in reader.ReadMessages(conversation.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sender = JoinNonEmpty(message.SenderDisplayName, message.SenderPhoneNumber) ?? "Unknown sender";
            var timestamp = message.TimestampUtc ?? message.RawTimestamp;
            await writer.WriteAsync($"<li class=\"message\" id=\"message-{message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"><article>").ConfigureAwait(false);
            await writer.WriteAsync($"<header><strong>{Html(sender)}</strong> <span class=\"muted\">{Html(message.Direction ?? "Direction unknown")}</span>").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(timestamp))
            {
                await writer.WriteAsync($"<time datetime=\"{HtmlAttribute(timestamp)}\">{Html(timestamp)}</time>").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(message.RawTimestamp) && !string.Equals(message.RawTimestamp, timestamp, StringComparison.Ordinal))
                {
                    await writer.WriteAsync($"<small>Raw timestamp: {Html(message.RawTimestamp)}</small>").ConfigureAwait(false);
                }
            }

            await writer.WriteLineAsync("</header>").ConfigureAwait(false);
            await writer.WriteAsync($"<p class=\"body\">{Html(message.Body ?? "[No body recorded]")}</p>").ConfigureAwait(false);
            if (message.Attachments.Count > 0)
            {
                await writer.WriteLineAsync("<ul class=\"attachments\">").ConfigureAwait(false);
                foreach (var attachment in message.Attachments)
                {
                    if (attachment.MatchedSourceFileId is long sourceId && mediaPaths.TryGetValue(sourceId, out var mediaPath))
                    {
                        var link = "../" + mediaPath;
                        await writer.WriteLineAsync($"<li><a href=\"{HtmlAttribute(link)}\">{Html(attachment.RawReference)}</a> <span class=\"muted\">{Html(attachment.MediaType ?? "Matched media")}</span></li>").ConfigureAwait(false);
                    }
                    else
                    {
                        var status = attachment.MatchedSourceFileId is null ? "Not matched" : "Matched media unavailable";
                        var retainedPath = attachment.MatchedRelativePath is null ? string.Empty : $" ({attachment.MatchedRelativePath})";
                        await writer.WriteLineAsync($"<li>{Html(attachment.RawReference)} <span class=\"muted\">{Html(status + retainedPath)}</span></li>").ConfigureAwait(false);
                    }
                }

                await writer.WriteLineAsync("</ul>").ConfigureAwait(false);
            }

            await writer.WriteLineAsync($"<details><summary>Message provenance</summary><code>{Html(message.SourceRelativePath)}{(message.SourceRowIndex is null ? string.Empty : $" · row {message.SourceRowIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")}</code></details>").ConfigureAwait(false);
            await writer.WriteLineAsync("</article></li>").ConfigureAwait(false);
        }

        await writer.WriteLineAsync("</ol></section></main>").ConfigureAwait(false);
        await WriteDocumentEndAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSearchPageAsync(string stage, SqliteArchiveReader reader, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "search.html");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
        await WriteDocumentStartAsync(writer, "Search history", "assets/site.css", cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("<main><nav><a href=\"index.html\">All history</a></nav><header><h1>Search history</h1><label for=\"query\">Search messages, calls, voicemail transcripts, contacts, and labels</label><input id=\"query\" type=\"search\" autocomplete=\"off\"><p id=\"status\" class=\"muted\">Enter a search term.</p></header><ol id=\"results\" class=\"messages\"></ol>").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("<script type=\"application/json\" id=\"search-index\">"), cancellationToken).ConfigureAwait(false);
        await using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartArray();
            foreach (var message in reader.ReadSearchMessages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                json.WriteStartObject();
                json.WriteString("kind", "Message");
                json.WriteString("href", $"conversations/{message.ConversationId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.html#message-{message.MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                json.WriteString("timestamp", message.TimestampUtc);
                json.WriteString("body", message.Body);
                json.WriteString("label", message.ConversationLabel);
                json.WriteString("sender", JoinNonEmpty(message.SenderDisplayName, message.SenderPhoneNumber));
                json.WriteEndObject();
            }

            foreach (var record in reader.ReadSearchEvents())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var href = record.RecordType == "call"
                    ? $"calls.html#call-{record.RecordId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    : $"voicemails.html#voicemail-{record.RecordId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                json.WriteStartObject();
                json.WriteString("kind", record.RecordType == "call" ? "Call" : "Voicemail");
                json.WriteString("href", href);
                json.WriteString("timestamp", record.TimestampUtc);
                json.WriteString("body", record.Body);
                json.WriteString("label", record.Label);
                json.WriteString("sender", JoinNonEmpty(JoinNonEmpty(record.Contact, record.FilenameContact), record.PhoneNumber));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            await json.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes("</script><script src=\"assets/search.js\" defer></script></main>"), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await WriteDocumentEndAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDocumentStartAsync(StreamWriter writer, string title, string stylesheetPath, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync("<!doctype html>").ConfigureAwait(false);
        await writer.WriteLineAsync("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">").ConfigureAwait(false);
        await writer.WriteLineAsync($"<title>{Html(title)}</title><link rel=\"stylesheet\" href=\"{HtmlAttribute(stylesheetPath)}\"></head><body>").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDocumentEndAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync("</body></html>").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCssAsync(string stage, CancellationToken cancellationToken)
    {
        const string css = "*{box-sizing:border-box}body{margin:0;background:#f4f6f8;color:#202833;font:16px/1.55 system-ui,sans-serif}main{max-width:980px;margin:0 auto;padding:1.5rem}header,article{background:#fff;border:1px solid #d9e0e7;border-radius:.6rem;padding:1rem 1.25rem;margin:1rem 0}h1,h2{line-height:1.2}a{color:#0759a5}input{display:block;width:100%;padding:.8rem;margin:.5rem 0 1rem;font:inherit;border:1px solid #7a8794;border-radius:.3rem}.muted,small{color:#586777}.conversation-list>li{padding:.75rem;background:#fff;border:1px solid #d9e0e7;margin:.5rem 0;border-radius:.4rem}.conversation-list p{margin:.25rem 0 0}.messages{list-style:none;padding:0}.message{margin:1rem 0}.body{white-space:pre-wrap;overflow-wrap:anywhere}.attachments{overflow-wrap:anywhere}time{display:block;color:#586777;font-size:.9rem}code{overflow-wrap:anywhere;white-space:pre-wrap}footer{margin-top:2rem}";
        await File.WriteAllTextAsync(Path.Combine(stage, "assets", "site.css"), css, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSearchScriptAsync(string stage, CancellationToken cancellationToken)
    {
        const string script = "(() => { const rows = JSON.parse(document.getElementById('search-index').textContent); const input = document.getElementById('query'); const results = document.getElementById('results'); const status = document.getElementById('status'); const render = () => { const query = input.value.trim().toLocaleLowerCase(); results.replaceChildren(); if (!query) { status.textContent = 'Enter a search term.'; return; } const matches = rows.filter(row => [row.body, row.label, row.sender, row.kind].some(value => (value || '').toLocaleLowerCase().includes(query))).slice(0, 500); status.textContent = matches.length + (matches.length === 500 ? ' or more' : '') + ' matching records'; for (const row of matches) { const li = document.createElement('li'); li.className = 'message'; const article = document.createElement('article'); const link = document.createElement('a'); link.href = row.href; link.textContent = row.label || row.kind; const meta = document.createElement('p'); meta.className = 'muted'; meta.textContent = row.kind + ' · ' + (row.sender || 'Unknown contact') + (row.timestamp ? ' · ' + row.timestamp : ''); const body = document.createElement('p'); body.className = 'body'; body.textContent = row.body || '[No text recorded]'; article.append(link, meta, body); li.append(article); results.append(li); } }; input.addEventListener('input', render); })();";
        await File.WriteAllTextAsync(Path.Combine(stage, "assets", "search.js"), script, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSourceArchiveCsvAsync(string stage, StoredArchiveMetadata metadata, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "source_archive.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "source_kind", "source_path", "source_sha256").ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, metadata.SourceKind.ToString(), metadata.SourcePath, metadata.SourceSha256).ConfigureAwait(false);
    }

    private static async Task WriteSourceFilesCsvAsync(string stage, SqliteArchiveReader reader, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "source_files.csv", cancellationToken).ConfigureAwait(false);
            await CsvRowAsync(writer, cancellationToken, "source_file_id", "ordinal", "relative_path", "size_bytes", "media_type", "content_sha256").ConfigureAwait(false);
        foreach (var file in reader.ReadSourceFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsvRowAsync(writer, cancellationToken, file.Id, file.Ordinal, file.RelativePath, file.SizeBytes, file.MediaType, file.ContentSha256).ConfigureAwait(false);
        }
    }

    private static async Task WriteConversationsCsvAsync(string stage, IReadOnlyList<StoredConversationSummary> conversations, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "conversations.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "conversation_id", "source_file_id", "source_path", "raw_label", "kind").ConfigureAwait(false);
        foreach (var conversation in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsvRowAsync(writer, cancellationToken, conversation.Id, conversation.SourceFileId, conversation.SourceRelativePath, conversation.RawLabel, conversation.Kind).ConfigureAwait(false);
        }
    }

    private static async Task WriteParticipantsCsvAsync(string stage, SqliteArchiveReader reader, IReadOnlyList<StoredConversationSummary> conversations, CancellationToken cancellationToken)
    {
        await using var participants = await CreateCsvWriterAsync(stage, "participants.csv", cancellationToken).ConfigureAwait(false);
        await using var evidence = await CreateCsvWriterAsync(stage, "participant_evidence.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(participants, cancellationToken, "participant_id", "conversation_id", "normalized_display_name", "normalized_phone_number").ConfigureAwait(false);
        await CsvRowAsync(evidence, cancellationToken, "evidence_id", "participant_id", "conversation_id", "source_type", "source_row_index", "raw_display_name", "raw_phone_number").ConfigureAwait(false);
        foreach (var conversation in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var participant in reader.ReadParticipants(conversation.Id))
            {
                await CsvRowAsync(participants, cancellationToken, participant.Id, participant.ConversationId, participant.NormalizedDisplayName, participant.NormalizedPhoneNumber).ConfigureAwait(false);
                foreach (var item in participant.Evidence)
                {
                    await CsvRowAsync(evidence, cancellationToken, item.Id, participant.Id, participant.ConversationId, item.SourceType, item.SourceRowIndex, item.RawDisplayName, item.RawPhoneNumber).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task WriteMessagesCsvAsync(string stage, SqliteArchiveReader reader, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "messages.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "message_id", "conversation_id", "source_file_id", "source_path", "source_row_index", "raw_timestamp", "timestamp_utc", "sender_display_name", "sender_phone_number", "direction", "body").ConfigureAwait(false);
        foreach (var message in reader.ReadMessages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsvRowAsync(writer, cancellationToken, message.Id, message.ConversationId, message.SourceFileId, message.SourceRelativePath, message.SourceRowIndex, message.RawTimestamp, message.TimestampUtc, message.SenderDisplayName, message.SenderPhoneNumber, message.Direction, message.Body).ConfigureAwait(false);
        }
    }

    private static async Task WriteAttachmentsCsvAsync(string stage, SqliteArchiveReader reader, MediaSourceReader source, List<UnavailableMedia> unavailable, CancellationToken cancellationToken)
    {
        var mediaById = reader.ReadMatchedMediaFiles().ToDictionary(item => item.Id);
        var sourceStatuses = new Dictionary<long, string>();
        foreach (var media in mediaById.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await source.GetEntryStatusAsync(media, cancellationToken).ConfigureAwait(false);
            sourceStatuses[media.Id] = status;
            if (!status.StartsWith("present_", StringComparison.Ordinal))
            {
                unavailable.Add(new UnavailableMedia(media.Id, media.RelativePath, status));
            }
        }

        await using var writer = await CreateCsvWriterAsync(stage, "attachments.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "attachment_id", "message_id", "raw_reference", "matched_source_file_id", "matched_relative_path", "media_type", "source_status").ConfigureAwait(false);
        foreach (var message in reader.ReadMessages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var attachment in message.Attachments)
            {
                var status = attachment.MatchedSourceFileId is long id && sourceStatuses.TryGetValue(id, out var sourceStatus)
                    ? sourceStatus
                    : "unmatched";
                await CsvRowAsync(writer, cancellationToken, attachment.Id, message.Id, attachment.RawReference, attachment.MatchedSourceFileId, attachment.MatchedRelativePath, attachment.MediaType, status).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteCallsCsvAsync(string stage, IReadOnlyList<StoredCallRecord> calls, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "calls.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "call_record_id", "source_file_id", "source_path", "raw_event_type", "raw_timestamp", "timestamp_utc", "raw_contact", "raw_filename_contact", "raw_contact_source", "raw_phone_number", "raw_duration_title", "duration_display_text", "duration_seconds").ConfigureAwait(false);
        foreach (var call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsvRowAsync(writer, cancellationToken, call.Id, call.SourceFileId, call.SourceRelativePath, call.RawEventType, call.RawTimestamp, call.TimestampUtc, call.RawContact, call.RawFilenameContact, call.RawContactSource, call.RawPhoneNumber, call.RawDurationTitle, call.DurationDisplayText, call.DurationSeconds).ConfigureAwait(false);
        }
    }

    private static async Task WriteCallMediaReferencesCsvAsync(string stage, IReadOnlyList<StoredCallRecord> calls, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "call_media_references.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "media_reference_id", "call_record_id", "raw_reference", "matched_source_file_id", "matched_relative_path", "media_type", "match_status").ConfigureAwait(false);
        foreach (var call in calls)
        {
            foreach (var reference in call.MediaReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CsvRowAsync(writer, cancellationToken, reference.Id, call.Id, reference.RawReference, reference.MatchedSourceFileId, reference.MatchedRelativePath, reference.MediaType, reference.MatchStatus).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteVoicemailsCsvAsync(string stage, IReadOnlyList<StoredVoicemail> voicemails, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "voicemails.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "voicemail_id", "source_file_id", "source_path", "raw_timestamp", "timestamp_utc", "raw_contact", "raw_filename_contact", "raw_contact_source", "raw_phone_number", "transcript", "raw_duration_title", "duration_display_text", "duration_seconds", "audio_reference", "matched_audio_source_file_id", "matched_audio_relative_path", "audio_match_status").ConfigureAwait(false);
        foreach (var voicemail in voicemails)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsvRowAsync(writer, cancellationToken, voicemail.Id, voicemail.SourceFileId, voicemail.SourceRelativePath, voicemail.RawTimestamp, voicemail.TimestampUtc, voicemail.RawContact, voicemail.RawFilenameContact, voicemail.RawContactSource, voicemail.RawPhoneNumber, voicemail.Transcript, voicemail.RawDurationTitle, voicemail.DurationDisplayText, voicemail.DurationSeconds, voicemail.AudioReference, voicemail.MatchedAudioSourceFileId, voicemail.MatchedAudioRelativePath, voicemail.AudioMatchStatus).ConfigureAwait(false);
        }
    }

    private static async Task WriteVoicemailMediaReferencesCsvAsync(string stage, IReadOnlyList<StoredVoicemail> voicemails, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "voicemail_media_references.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "media_reference_id", "voicemail_id", "raw_reference", "matched_source_file_id", "matched_relative_path", "media_type", "match_status").ConfigureAwait(false);
        foreach (var voicemail in voicemails)
        {
            foreach (var reference in voicemail.MediaReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CsvRowAsync(writer, cancellationToken, reference.Id, voicemail.Id, reference.RawReference, reference.MatchedSourceFileId, reference.MatchedRelativePath, reference.MediaType, reference.MatchStatus).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteImportIssuesCsvAsync(string stage, SqliteArchiveReader reader, CancellationToken cancellationToken)
    {
        await using var writer = await CreateCsvWriterAsync(stage, "import_issues.csv", cancellationToken).ConfigureAwait(false);
        await CsvRowAsync(writer, cancellationToken, "issue_id", "code", "severity", "message", "source_file_id", "source_path", "source_row_index").ConfigureAwait(false);
        foreach (var issue in reader.ReadImportIssues())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CsvRowAsync(writer, cancellationToken, issue.Id, issue.Code, issue.Severity, issue.Message, issue.SourceFileId, issue.SourceRelativePath, issue.SourceRowIndex).ConfigureAwait(false);
        }
    }

    private static async Task WriteExportReportAsync(
        string stage,
        int conversationCount,
        SqliteArchiveReader reader,
        IReadOnlyList<UnavailableMedia> unavailable,
        string sourceArchiveStatus,
        CancellationToken cancellationToken)
    {
        var messageCount = CountMessages(reader, cancellationToken);
        var attachmentCount = CountAttachments(reader, cancellationToken);
        var calls = reader.ReadCalls();
        var voicemails = reader.ReadVoicemails();
        var eventMediaReferences = calls.Sum(call => (long)call.MediaReferences.Count)
            + voicemails.Sum(voicemail => (long)voicemail.MediaReferences.Count);
        var matchedEventMediaReferences = calls.Sum(call => call.MediaReferences.LongCount(reference => reference.MatchStatus == "matched"))
            + voicemails.Sum(voicemail => voicemail.MediaReferences.LongCount(reference => reference.MatchStatus == "matched"));
        var report = new
        {
            formatVersion = 1,
            generatedBy = "VoiceBridge",
            conversations = conversationCount,
            messages = messageCount,
            attachments = attachmentCount,
            calls = calls.Count,
            voicemails = voicemails.Count,
            callAndVoicemailMediaReferences = eventMediaReferences,
            callAndVoicemailMediaReferencesMatched = matchedEventMediaReferences,
            callAndVoicemailMediaReferencesUnresolved = eventMediaReferences - matchedEventMediaReferences,
            unavailableMediaCount = unavailable.Count,
            unavailableMedia = unavailable,
            sourceArchiveStatus,
            formulaEscapingPolicy = "CSV text cells starting with =, +, -, @, tab, or a line break are prefixed with an apostrophe for spreadsheet safety; the imported database values are unchanged.",
            note = "Unmatched or unavailable media references are retained in the exported message, call, voicemail, or media-reference records. CSV source media paths are not copied, and their content is not read during CSV export."
        };
        await using var stream = new FileStream(Path.Combine(stage, "export-report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StreamWriter> CreateCsvWriterAsync(string stage, string fileName, CancellationToken cancellationToken)
    {
        var stream = new FileStream(Path.Combine(stage, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        return await Task.FromResult(new StreamWriter(stream, Utf8WithoutBom));
    }

    private static async Task CsvRowAsync(StreamWriter writer, CancellationToken cancellationToken, params object?[] values)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = values.Select(CsvField);
        await writer.WriteLineAsync(string.Join(',', fields)).ConfigureAwait(false);
    }

    private static string CsvField(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
        if (value is string && IsSpreadsheetFormulaLike(text))
        {
            text = "'" + text;
        }

        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\r') && !text.Contains('\n'))
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsSpreadsheetFormulaLike(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var firstVisibleIndex = 0;
        while (firstVisibleIndex < value.Length && value[firstVisibleIndex] is ' ' or '\t' or '\r' or '\n')
        {
            firstVisibleIndex++;
        }

        return firstVisibleIndex < value.Length
            && value[firstVisibleIndex] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';
    }

    private static async Task<string?> TryCopyMediaAsync(
        Stream source,
        Stream destination,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return "source_media_unreadable";
            }

            if (bytesRead == 0)
            {
                var actualHash = Convert.ToHexString(sha256.GetHashAndReset());
                return expectedSha256 is null || string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "source_file_hash_mismatch";
            }

            sha256.AppendData(buffer.AsSpan(0, bytesRead));
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha256.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(sha256.GetHashAndReset());
    }

    private static long CountMessages(SqliteArchiveReader reader, CancellationToken cancellationToken) => CountMessages(reader, null, cancellationToken);

    private static long CountMessages(SqliteArchiveReader reader, long? conversationId, CancellationToken cancellationToken)
    {
        long count = 0;
        foreach (var _ in reader.ReadMessages(conversationId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private static long CountAttachments(SqliteArchiveReader reader, CancellationToken cancellationToken)
    {
        long count = 0;
        foreach (var message in reader.ReadMessages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count += message.Attachments.Count;
        }

        return count;
    }

    private static string ConversationTitle(StoredConversationSummary conversation, IReadOnlyList<StoredParticipant> participants) =>
        !string.IsNullOrWhiteSpace(conversation.RawLabel)
            ? conversation.RawLabel
            : ParticipantSummary(participants) is { Length: > 0 } summary
                ? summary
                : $"Conversation {conversation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string ParticipantSummary(IReadOnlyList<StoredParticipant> participants)
    {
        var labels = participants.Select(participant => JoinNonEmpty(participant.NormalizedDisplayName, participant.NormalizedPhoneNumber))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return labels.Length == 0 ? string.Empty : string.Join(", ", labels);
    }

    private static string? JoinNonEmpty(string? first, string? second)
    {
        var values = new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? null : string.Join(" · ", values);
    }

    private static string Html(string? value) =>
        HtmlEncoder.Default.Encode(value ?? string.Empty);

    private static string HtmlAttribute(string? value) => Html(value).Replace("'", "&#x27;", StringComparison.Ordinal);

    private static string SafeExtension(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var extension = normalized is null ? string.Empty : Path.GetExtension(normalized);
        return extension.Length is > 1 and <= 12 && extension[1..].All(char.IsAsciiLetterOrDigit)
            ? extension.ToLowerInvariant()
            : ".bin";
    }

    private static string? NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0'))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
        {
            return null;
        }

        var segments = normalized.Split('/');
        return segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':'))
            ? null
            : string.Join('/', segments);
    }

    private sealed record UnavailableMedia(long SourceFileId, string RelativePath, string Reason);

    private sealed class MediaSourceReader : IDisposable
    {
        private readonly SourceKind _sourceKind;
        private ZipArchive? _archive;
        private Dictionary<string, List<ZipArchiveEntry>>? _zipEntries;
        private readonly string? _directoryRoot;
        private string _sourceArchiveStatus;

        private MediaSourceReader(StoredArchiveMetadata metadata)
        {
            _sourceKind = metadata.SourceKind;
            if (_sourceKind == SourceKind.Directory && Directory.Exists(metadata.SourcePath))
            {
                _directoryRoot = Path.GetFullPath(metadata.SourcePath);
                _sourceArchiveStatus = "directory_source_content_unverified";
            }
            else
            {
                _sourceArchiveStatus = _sourceKind == SourceKind.Directory ? "source_missing" : "source_unverified";
            }
        }

        public static async Task<MediaSourceReader> CreateAsync(StoredArchiveMetadata metadata, CancellationToken cancellationToken)
        {
            var reader = new MediaSourceReader(metadata);
            if (metadata.SourceKind == SourceKind.ZipArchive)
            {
                await reader.InitializeZipAsync(metadata, cancellationToken).ConfigureAwait(false);
            }

            return reader;
        }

        public string SourceArchiveStatus => _sourceArchiveStatus;

        public async Task<string> GetEntryStatusAsync(StoredSourceFile sourceFile, CancellationToken cancellationToken)
        {
            var path = NormalizeRelativePath(sourceFile.RelativePath);
            if (path is null)
            {
                return "invalid_path";
            }

            if (_sourceKind == SourceKind.ZipArchive)
            {
                if (_sourceArchiveStatus != "verified")
                {
                    return _sourceArchiveStatus;
                }

                if (_zipEntries is null || !_zipEntries.TryGetValue(path, out var entries))
                {
                    return "missing";
                }

                return entries.Count == 1
                    ? "present_in_verified_archive_content_not_checked"
                    : "ambiguous";
            }

            if (_sourceArchiveStatus != "directory_source_content_unverified")
            {
                return _sourceArchiveStatus;
            }

            if (string.IsNullOrWhiteSpace(sourceFile.ContentSha256))
            {
                return "source_file_hash_unavailable";
            }

            if (!TryGetSafeDirectoryPath(path, out var pathOnDisk) || !File.Exists(pathOnDisk))
            {
                return "missing_or_unsafe";
            }

            try
            {
                await using var mediaStream = new FileStream(pathOnDisk, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var currentHash = await ComputeSha256Async(mediaStream, cancellationToken).ConfigureAwait(false);
                return string.Equals(currentHash, sourceFile.ContentSha256, StringComparison.OrdinalIgnoreCase)
                    ? "present_in_verified_directory_file"
                    : "source_file_hash_mismatch";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return "source_media_unreadable";
            }
        }

        public bool TryOpen(StoredSourceFile sourceFile, out Stream content, out string failureReason)
        {
            content = Stream.Null;
            failureReason = string.Empty;
            var path = NormalizeRelativePath(sourceFile.RelativePath);
            if (path is null)
            {
                failureReason = "invalid_path";
                return false;
            }

            if (_sourceKind == SourceKind.ZipArchive)
            {
                if (_sourceArchiveStatus != "verified")
                {
                    failureReason = _sourceArchiveStatus;
                    return false;
                }

                if (_zipEntries is null || !_zipEntries.TryGetValue(path, out var entries))
                {
                    failureReason = "missing";
                    return false;
                }

                if (entries.Count != 1)
                {
                    failureReason = "ambiguous";
                    return false;
                }

                try
                {
                    content = entries[0].Open();
                    return true;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failureReason = "source_media_unreadable";
                    return false;
                }
            }

            if (_sourceArchiveStatus != "directory_source_content_unverified")
            {
                failureReason = _sourceArchiveStatus;
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceFile.ContentSha256))
            {
                failureReason = "source_file_hash_unavailable";
                return false;
            }

            if (!TryGetSafeDirectoryPath(path, out var pathOnDisk) || !File.Exists(pathOnDisk))
            {
                failureReason = "missing_or_unsafe";
                return false;
            }

            try
            {
                content = new FileStream(pathOnDisk, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failureReason = "source_media_unreadable";
                return false;
            }
        }

        public void Dispose() => _archive?.Dispose();

        private async Task InitializeZipAsync(StoredArchiveMetadata metadata, CancellationToken cancellationToken)
        {
            if (!File.Exists(metadata.SourcePath))
            {
                _sourceArchiveStatus = "source_missing";
                return;
            }

            if (string.IsNullOrWhiteSpace(metadata.SourceSha256))
            {
                _sourceArchiveStatus = "source_hash_unavailable";
                return;
            }

            FileStream? stream = null;
            ZipArchive? archive = null;
            try
            {
                stream = new FileStream(metadata.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var currentHash = await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentHash, metadata.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _sourceArchiveStatus = "hash_mismatch";
                    await stream.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                stream.Position = 0;
                archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var zipEntries = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue;
                    }

                    var path = NormalizeRelativePath(entry.FullName);
                    if (path is null)
                    {
                        continue;
                    }

                    if (!zipEntries.TryGetValue(path, out var entries))
                    {
                        entries = [];
                        zipEntries.Add(path, entries);
                    }

                    entries.Add(entry);
                }

                _archive = archive;
                _zipEntries = zipEntries;
                _sourceArchiveStatus = "verified";
            }
            catch (OperationCanceledException)
            {
                archive?.Dispose();
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                archive?.Dispose();
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                _sourceArchiveStatus = "source_unreadable";
            }
        }

        private bool TryGetSafeDirectoryPath(string relativePath, out string pathOnDisk)
        {
            pathOnDisk = string.Empty;
            if (_directoryRoot is null || NormalizeRelativePath(relativePath) is not { } normalized)
            {
                return false;
            }

            var segments = normalized.Split('/');
            var current = _directoryRoot;
            try
            {
                foreach (var segment in segments)
                {
                    current = Path.Combine(current, segment);
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            var relative = Path.GetRelativePath(_directoryRoot, current);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return false;
            }

            pathOnDisk = current;
            return true;
        }
    }
}
