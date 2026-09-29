using System.Globalization;
using Microsoft.Data.Sqlite;
using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Importing;
using VoiceBridge.Core.Scanning;

namespace VoiceBridge.Storage;

internal sealed class SqliteTakeoutImportStore(SqliteConnection connection, string databasePath) : ITakeoutImportStore
{
    private readonly Dictionary<string, long> _sourceFileIds = new(StringComparer.OrdinalIgnoreCase);
    private SqliteTransaction? _transaction;
    private long _importRunId;
    private bool _completed;

    public async ValueTask InitializeAsync(
        ImportInputIdentity inputIdentity,
        DateTimeOffset startedAt,
        ScanReport scanReport,
        IReadOnlyList<SourceFile> sourceFiles,
        CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(Schema, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync("PRAGMA user_version = 3;", cancellationToken).ConfigureAwait(false);

        var archiveId = await InsertAndGetIdAsync(
            "INSERT INTO source_archives(source_kind, source_path, source_sha256, files_scanned) VALUES ($kind, $path, $sha, $count);",
            command =>
            {
                command.Parameters.AddWithValue("$kind", inputIdentity.SourceKind.ToString());
                command.Parameters.AddWithValue("$path", inputIdentity.SourcePath);
                command.Parameters.AddWithValue("$sha", (object?)inputIdentity.SourceSha256 ?? DBNull.Value);
                command.Parameters.AddWithValue("$count", inputIdentity.FilesScanned);
            }, cancellationToken).ConfigureAwait(false);

        _importRunId = await InsertAndGetIdAsync(
            "INSERT INTO import_runs(source_archive_id, status, started_at, finished_at, files_scanned) VALUES ($archive, 'Running', $started, NULL, $files);",
            command =>
            {
                command.Parameters.AddWithValue("$archive", archiveId);
                command.Parameters.AddWithValue("$started", FormatDate(startedAt));
                command.Parameters.AddWithValue("$files", scanReport.FilesScanned);
            }, cancellationToken).ConfigureAwait(false);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceFile in sourceFiles)
        {
            counts[sourceFile.RelativePath] = counts.GetValueOrDefault(sourceFile.RelativePath) + 1;
        }

        for (var index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = sourceFiles[index];
            var id = await InsertAndGetIdAsync(
                "INSERT INTO source_files(source_archive_id, ordinal, relative_path, size_bytes, media_type, content_sha256) VALUES ($archive, $ordinal, $path, $size, $media, $sha);",
                command =>
                {
                    command.Parameters.AddWithValue("$archive", archiveId);
                    command.Parameters.AddWithValue("$ordinal", index);
                    command.Parameters.AddWithValue("$path", sourceFile.RelativePath);
                    command.Parameters.AddWithValue("$size", sourceFile.SizeBytes);
                    command.Parameters.AddWithValue("$media", (object?)sourceFile.MediaType ?? DBNull.Value);
                    command.Parameters.AddWithValue("$sha", (object?)sourceFile.ContentSha256 ?? DBNull.Value);
                }, cancellationToken).ConfigureAwait(false);

            if (counts[sourceFile.RelativePath] == 1)
            {
                _sourceFileIds[sourceFile.RelativePath] = id;
            }
        }
    }

    public async ValueTask WriteConversationAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var conversationId = await InsertAndGetIdAsync(
            "INSERT INTO conversations(import_run_id, source_file_id, source_relative_path, raw_label, kind) VALUES ($run, $source, $path, $label, $kind);",
            command =>
            {
                command.Parameters.AddWithValue("$run", _importRunId);
                command.Parameters.AddWithValue("$source", FindSourceFileId(conversation.SourceRelativePath));
                command.Parameters.AddWithValue("$path", conversation.SourceRelativePath);
                command.Parameters.AddWithValue("$label", (object?)conversation.RawLabel ?? DBNull.Value);
                command.Parameters.AddWithValue("$kind", conversation.Kind.ToString());
            }, cancellationToken).ConfigureAwait(false);

        foreach (var participant in conversation.Participants)
        {
            var participantId = await InsertAndGetIdAsync(
                "INSERT INTO conversation_participants(conversation_id, normalized_display_name, normalized_phone_number) VALUES ($conversation, $name, $phone);",
                command =>
                {
                    command.Parameters.AddWithValue("$conversation", conversationId);
                    command.Parameters.AddWithValue("$name", (object?)participant.NormalizedDisplayName ?? DBNull.Value);
                    command.Parameters.AddWithValue("$phone", (object?)participant.NormalizedPhoneNumber ?? DBNull.Value);
                }, cancellationToken).ConfigureAwait(false);

            foreach (var evidence in participant.Evidence)
            {
                await ExecuteAsync(
                    "INSERT INTO participant_evidence(participant_id, source_type, source_row_index, raw_display_name, raw_phone_number) VALUES ($participant, $source, $row, $name, $phone);",
                    command =>
                    {
                        command.Parameters.AddWithValue("$participant", participantId);
                        command.Parameters.AddWithValue("$source", evidence.Source.ToString());
                        command.Parameters.AddWithValue("$row", (object?)evidence.SourceRowIndex ?? DBNull.Value);
                        command.Parameters.AddWithValue("$name", (object?)evidence.Participant.RawDisplayName ?? DBNull.Value);
                        command.Parameters.AddWithValue("$phone", (object?)evidence.Participant.RawPhoneNumber ?? DBNull.Value);
                    }, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var message in conversation.Messages)
        {
            var messageId = await InsertAndGetIdAsync(
                "INSERT INTO messages(conversation_id, source_file_id, source_relative_path, source_row_index, raw_timestamp, timestamp_utc, body, sender_display_name, sender_phone_number, direction) VALUES ($conversation, $source, $path, $row, $raw, $timestamp, $body, $sender_name, $sender_phone, $direction);",
                command =>
                {
                    command.Parameters.AddWithValue("$conversation", conversationId);
                    command.Parameters.AddWithValue("$source", FindSourceFileId(message.SourceRelativePath));
                    command.Parameters.AddWithValue("$path", message.SourceRelativePath);
                    command.Parameters.AddWithValue("$row", (object?)message.SourceRowIndex ?? DBNull.Value);
                    command.Parameters.AddWithValue("$raw", (object?)message.RawTimestamp ?? DBNull.Value);
                    command.Parameters.AddWithValue("$timestamp", (object?)(message.Timestamp is null ? null : FormatDate(message.Timestamp.Value)) ?? DBNull.Value);
                    command.Parameters.AddWithValue("$body", (object?)message.Body ?? DBNull.Value);
                    command.Parameters.AddWithValue("$sender_name", (object?)message.Sender?.RawDisplayName ?? DBNull.Value);
                    command.Parameters.AddWithValue("$sender_phone", (object?)message.Sender?.RawPhoneNumber ?? DBNull.Value);
                    command.Parameters.AddWithValue("$direction", (object?)message.Direction?.ToString() ?? DBNull.Value);
                }, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                "INSERT INTO message_search(message_id, conversation_id, body) VALUES ($message, $conversation, $body);",
                command =>
                {
                    command.Parameters.AddWithValue("$message", messageId);
                    command.Parameters.AddWithValue("$conversation", conversationId);
                    command.Parameters.AddWithValue("$body", message.Body ?? string.Empty);
                }, cancellationToken).ConfigureAwait(false);

            if (message.AttachmentReferences is null)
            {
                continue;
            }

            foreach (var attachment in message.AttachmentReferences)
            {
                await ExecuteAsync(
                    "INSERT INTO attachments(message_id, raw_reference, matched_source_file_id, matched_relative_path, media_type) VALUES ($message, $raw, $source, $path, $media);",
                    command =>
                    {
                        command.Parameters.AddWithValue("$message", messageId);
                        command.Parameters.AddWithValue("$raw", attachment.RawReference);
                        command.Parameters.AddWithValue("$source", FindSourceFileId(attachment.MatchedRelativePath));
                        command.Parameters.AddWithValue("$path", (object?)attachment.MatchedRelativePath ?? DBNull.Value);
                        command.Parameters.AddWithValue("$media", (object?)attachment.MediaType ?? DBNull.Value);
                    }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask WriteCallAsync(CallRecord call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var callId = await InsertAndGetIdAsync(
            "INSERT INTO call_records(import_run_id, source_file_id, source_relative_path, raw_event_type, raw_timestamp, timestamp_utc, raw_contact, raw_filename_contact, raw_contact_source, raw_phone_number, raw_duration_title, duration_display_text, duration_seconds) VALUES ($run, $source, $path, $event, $raw_timestamp, $timestamp, $contact, $filename_contact, $contact_source, $phone, $duration_title, $duration_text, $duration_seconds);",
            command =>
            {
                command.Parameters.AddWithValue("$run", _importRunId);
                command.Parameters.AddWithValue("$source", FindSourceFileId(call.SourceRelativePath));
                command.Parameters.AddWithValue("$path", call.SourceRelativePath);
                command.Parameters.AddWithValue("$event", (object?)call.RawEventType ?? DBNull.Value);
                command.Parameters.AddWithValue("$raw_timestamp", (object?)call.RawTimestamp ?? DBNull.Value);
                command.Parameters.AddWithValue("$timestamp", (object?)(call.Timestamp is null ? null : FormatDate(call.Timestamp.Value)) ?? DBNull.Value);
                command.Parameters.AddWithValue("$contact", (object?)call.RawContact ?? DBNull.Value);
                command.Parameters.AddWithValue("$filename_contact", (object?)call.RawFilenameContact ?? DBNull.Value);
                command.Parameters.AddWithValue("$contact_source", (object?)call.RawContactSource ?? DBNull.Value);
                command.Parameters.AddWithValue("$phone", (object?)call.RawPhoneNumber ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration_title", (object?)call.RawDurationTitle ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration_text", (object?)call.DurationDisplayText ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration_seconds", (object?)(call.Duration?.TotalSeconds) ?? DBNull.Value);
            }, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            "INSERT INTO event_search(record_type, record_id, content) VALUES ('call', $id, $content);",
            command =>
            {
                command.Parameters.AddWithValue("$id", callId);
                command.Parameters.AddWithValue("$content", string.Join(' ', new[] { call.RawEventType, call.RawContact, call.RawFilenameContact, call.RawPhoneNumber, call.RawDurationTitle, call.DurationDisplayText }.Where(value => !string.IsNullOrWhiteSpace(value))));
            }, cancellationToken).ConfigureAwait(false);

        var references = call.MediaReferences ?? [];
        for (var index = 0; index < references.Count; index++)
        {
            await WriteCallMediaReferenceAsync(callId, index, references[index], cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteVoicemailAsync(Voicemail voicemail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(voicemail);
        var voicemailId = await InsertAndGetIdAsync(
            "INSERT INTO voicemails(import_run_id, source_file_id, source_relative_path, raw_timestamp, timestamp_utc, raw_contact, raw_filename_contact, raw_contact_source, raw_phone_number, transcript, raw_duration_title, duration_display_text, duration_seconds, audio_reference, matched_audio_source_file_id, matched_audio_relative_path, audio_match_status) VALUES ($run, $source, $path, $raw_timestamp, $timestamp, $contact, $filename_contact, $contact_source, $phone, $transcript, $duration_title, $duration_text, $duration_seconds, $audio_reference, $audio_source, $audio_path, $audio_status);",
            command =>
            {
                command.Parameters.AddWithValue("$run", _importRunId);
                command.Parameters.AddWithValue("$source", FindSourceFileId(voicemail.SourceRelativePath));
                command.Parameters.AddWithValue("$path", voicemail.SourceRelativePath);
                command.Parameters.AddWithValue("$raw_timestamp", (object?)voicemail.RawTimestamp ?? DBNull.Value);
                command.Parameters.AddWithValue("$timestamp", (object?)(voicemail.Timestamp is null ? null : FormatDate(voicemail.Timestamp.Value)) ?? DBNull.Value);
                command.Parameters.AddWithValue("$contact", (object?)voicemail.RawContact ?? DBNull.Value);
                command.Parameters.AddWithValue("$filename_contact", (object?)voicemail.RawFilenameContact ?? DBNull.Value);
                command.Parameters.AddWithValue("$contact_source", (object?)voicemail.RawContactSource ?? DBNull.Value);
                command.Parameters.AddWithValue("$phone", (object?)voicemail.RawPhoneNumber ?? DBNull.Value);
                command.Parameters.AddWithValue("$transcript", (object?)voicemail.Transcript ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration_title", (object?)voicemail.RawDurationTitle ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration_text", (object?)voicemail.DurationDisplayText ?? DBNull.Value);
                command.Parameters.AddWithValue("$duration_seconds", (object?)(voicemail.Duration?.TotalSeconds) ?? DBNull.Value);
                command.Parameters.AddWithValue("$audio_reference", (object?)voicemail.AudioReference ?? DBNull.Value);
                command.Parameters.AddWithValue("$audio_source", FindSourceFileId(voicemail.MatchedAudioRelativePath));
                command.Parameters.AddWithValue("$audio_path", (object?)voicemail.MatchedAudioRelativePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$audio_status", voicemail.AudioMatchStatus);
            }, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            "INSERT INTO event_search(record_type, record_id, content) VALUES ('voicemail', $id, $content);",
            command =>
            {
                command.Parameters.AddWithValue("$id", voicemailId);
                command.Parameters.AddWithValue("$content", string.Join(' ', new[] { voicemail.RawContact, voicemail.RawFilenameContact, voicemail.RawPhoneNumber, voicemail.Transcript, voicemail.RawDurationTitle, voicemail.DurationDisplayText }.Where(value => !string.IsNullOrWhiteSpace(value))));
            }, cancellationToken).ConfigureAwait(false);

        var references = voicemail.MediaReferences ?? [];
        for (var index = 0; index < references.Count; index++)
        {
            await WriteVoicemailMediaReferenceAsync(voicemailId, index, references[index], cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteCallMediaReferenceAsync(long callId, int ordinal, MediaReference reference, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "INSERT INTO call_media_references(call_record_id, ordinal, raw_reference, matched_source_file_id, matched_relative_path, media_type, match_status) VALUES ($call, $ordinal, $raw, $source, $path, $media, $status);",
            command =>
            {
                command.Parameters.AddWithValue("$call", callId);
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$raw", reference.RawReference);
                command.Parameters.AddWithValue("$source", FindSourceFileId(reference.MatchedRelativePath));
                command.Parameters.AddWithValue("$path", (object?)reference.MatchedRelativePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$media", (object?)reference.MediaType ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", reference.MatchStatus);
            }, cancellationToken);

    private async ValueTask WriteVoicemailMediaReferenceAsync(long voicemailId, int ordinal, MediaReference reference, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "INSERT INTO voicemail_media_references(voicemail_id, ordinal, raw_reference, matched_source_file_id, matched_relative_path, media_type, match_status) VALUES ($voicemail, $ordinal, $raw, $source, $path, $media, $status);",
            command =>
            {
                command.Parameters.AddWithValue("$voicemail", voicemailId);
                command.Parameters.AddWithValue("$ordinal", ordinal);
                command.Parameters.AddWithValue("$raw", reference.RawReference);
                command.Parameters.AddWithValue("$source", FindSourceFileId(reference.MatchedRelativePath));
                command.Parameters.AddWithValue("$path", (object?)reference.MatchedRelativePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$media", (object?)reference.MediaType ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", reference.MatchStatus);
            }, cancellationToken);

    public ValueTask WriteIssueAsync(ImportIssueRecord issue, CancellationToken cancellationToken) => ExecuteAsync(
        "INSERT INTO import_issues(import_run_id, code, message, severity, source_file_id, source_relative_path, source_row_index) VALUES ($run, $code, $message, $severity, $source, $path, $row);",
        command =>
        {
            command.Parameters.AddWithValue("$run", _importRunId);
            command.Parameters.AddWithValue("$code", issue.Code);
            command.Parameters.AddWithValue("$message", issue.Message);
            command.Parameters.AddWithValue("$severity", issue.Severity.ToString());
            command.Parameters.AddWithValue("$source", FindSourceFileId(issue.SourceRelativePath));
            command.Parameters.AddWithValue("$path", (object?)issue.SourceRelativePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$row", (object?)issue.SourceRowIndex ?? DBNull.Value);
        }, cancellationToken);

    public async ValueTask CompleteAsync(ImportReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await ExecuteAsync(
            "UPDATE import_runs SET status = 'Completed', finished_at = $finished, messages_parsed = $messages, conversations_parsed = $conversations, attachments_parsed = $attachments, calls_parsed = $calls, voicemails_parsed = $voicemails, media_references_parsed = $media, media_references_matched = $media_matched, media_references_unresolved = $media_unresolved, records_skipped = $skipped, warnings = $warnings, errors = $errors WHERE id = $run;",
            command =>
            {
                command.Parameters.AddWithValue("$finished", FormatDate(report.FinishedAt));
                command.Parameters.AddWithValue("$messages", report.RecordsParsed.Messages);
                command.Parameters.AddWithValue("$conversations", report.RecordsParsed.Conversations);
                command.Parameters.AddWithValue("$attachments", report.RecordsParsed.Attachments);
                command.Parameters.AddWithValue("$calls", report.RecordsParsed.Calls);
                command.Parameters.AddWithValue("$voicemails", report.RecordsParsed.Voicemails);
                command.Parameters.AddWithValue("$media", report.RecordsParsed.MediaReferences);
                command.Parameters.AddWithValue("$media_matched", report.RecordsParsed.MatchedMediaReferences);
                command.Parameters.AddWithValue("$media_unresolved", report.RecordsParsed.UnresolvedMediaReferences);
                command.Parameters.AddWithValue("$skipped", report.RecordsSkipped);
                command.Parameters.AddWithValue("$warnings", report.Warnings);
                command.Parameters.AddWithValue("$errors", report.Errors);
                command.Parameters.AddWithValue("$run", _importRunId);
            }, cancellationToken).ConfigureAwait(false);

        await _transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_transaction is not null)
            {
                if (!_completed)
                {
                    await _transaction.RollbackAsync().ConfigureAwait(false);
                }

                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!_completed && File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }
        }
    }

    private object FindSourceFileId(string? path) =>
        path is not null && _sourceFileIds.TryGetValue(path, out var id) ? id : DBNull.Value;

    private async Task<long> InsertAndGetIdAsync(string sql, Action<SqliteCommand> addParameters, CancellationToken cancellationToken)
    {
        var insertWithId = $"{sql.Trim().TrimEnd(';')} RETURNING id;";
        await using var command = CreateCommand(insertWithId);
        addParameters(command);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken) =>
        await ExecuteAsync(sql, _ => { }, cancellationToken).ConfigureAwait(false);

    private async ValueTask ExecuteAsync(string sql, Action<SqliteCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        addParameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteCommand CreateCommand(string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string Schema = """
        CREATE TABLE source_archives (
            id INTEGER PRIMARY KEY,
            source_kind TEXT NOT NULL,
            source_path TEXT NOT NULL,
            source_sha256 TEXT,
            files_scanned INTEGER NOT NULL
        );
        CREATE TABLE import_runs (
            id INTEGER PRIMARY KEY,
            source_archive_id INTEGER NOT NULL REFERENCES source_archives(id),
            status TEXT NOT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            files_scanned INTEGER NOT NULL,
            messages_parsed INTEGER NOT NULL DEFAULT 0,
            conversations_parsed INTEGER NOT NULL DEFAULT 0,
            attachments_parsed INTEGER NOT NULL DEFAULT 0,
            calls_parsed INTEGER NOT NULL DEFAULT 0,
            voicemails_parsed INTEGER NOT NULL DEFAULT 0,
            media_references_parsed INTEGER NOT NULL DEFAULT 0,
            media_references_matched INTEGER NOT NULL DEFAULT 0,
            media_references_unresolved INTEGER NOT NULL DEFAULT 0,
            records_skipped INTEGER NOT NULL DEFAULT 0,
            warnings INTEGER NOT NULL DEFAULT 0,
            errors INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE source_files (
            id INTEGER PRIMARY KEY,
            source_archive_id INTEGER NOT NULL REFERENCES source_archives(id),
            ordinal INTEGER NOT NULL,
            relative_path TEXT NOT NULL,
            size_bytes INTEGER NOT NULL,
            media_type TEXT,
            content_sha256 TEXT
        );
        CREATE TABLE conversations (
            id INTEGER PRIMARY KEY,
            import_run_id INTEGER NOT NULL REFERENCES import_runs(id),
            source_file_id INTEGER REFERENCES source_files(id),
            source_relative_path TEXT NOT NULL,
            raw_label TEXT,
            kind TEXT NOT NULL
        );
        CREATE TABLE conversation_participants (
            id INTEGER PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations(id),
            normalized_display_name TEXT,
            normalized_phone_number TEXT
        );
        CREATE TABLE participant_evidence (
            id INTEGER PRIMARY KEY,
            participant_id INTEGER NOT NULL REFERENCES conversation_participants(id),
            source_type TEXT NOT NULL,
            source_row_index INTEGER,
            raw_display_name TEXT,
            raw_phone_number TEXT
        );
        CREATE TABLE messages (
            id INTEGER PRIMARY KEY,
            conversation_id INTEGER NOT NULL REFERENCES conversations(id),
            source_file_id INTEGER REFERENCES source_files(id),
            source_relative_path TEXT NOT NULL,
            source_row_index INTEGER,
            raw_timestamp TEXT,
            timestamp_utc TEXT,
            body TEXT,
            sender_display_name TEXT,
            sender_phone_number TEXT,
            direction TEXT
        );
        CREATE TABLE attachments (
            id INTEGER PRIMARY KEY,
            message_id INTEGER NOT NULL REFERENCES messages(id),
            raw_reference TEXT NOT NULL,
            matched_source_file_id INTEGER REFERENCES source_files(id),
            matched_relative_path TEXT,
            media_type TEXT
        );
        CREATE TABLE call_records (
            id INTEGER PRIMARY KEY,
            import_run_id INTEGER NOT NULL REFERENCES import_runs(id),
            source_file_id INTEGER REFERENCES source_files(id),
            source_relative_path TEXT NOT NULL,
            raw_event_type TEXT,
            raw_timestamp TEXT,
            timestamp_utc TEXT,
            raw_contact TEXT,
            raw_filename_contact TEXT,
            raw_contact_source TEXT,
            raw_phone_number TEXT,
            raw_duration_title TEXT,
            duration_display_text TEXT,
            duration_seconds REAL
        );
        CREATE TABLE call_media_references (
            id INTEGER PRIMARY KEY,
            call_record_id INTEGER NOT NULL REFERENCES call_records(id),
            ordinal INTEGER NOT NULL,
            raw_reference TEXT NOT NULL,
            matched_source_file_id INTEGER REFERENCES source_files(id),
            matched_relative_path TEXT,
            media_type TEXT,
            match_status TEXT NOT NULL
        );
        CREATE TABLE voicemails (
            id INTEGER PRIMARY KEY,
            import_run_id INTEGER NOT NULL REFERENCES import_runs(id),
            source_file_id INTEGER REFERENCES source_files(id),
            source_relative_path TEXT NOT NULL,
            raw_timestamp TEXT,
            timestamp_utc TEXT,
            raw_contact TEXT,
            raw_filename_contact TEXT,
            raw_contact_source TEXT,
            raw_phone_number TEXT,
            transcript TEXT,
            raw_duration_title TEXT,
            duration_display_text TEXT,
            duration_seconds REAL,
            audio_reference TEXT,
            matched_audio_source_file_id INTEGER REFERENCES source_files(id),
            matched_audio_relative_path TEXT,
            audio_match_status TEXT NOT NULL
        );
        CREATE TABLE voicemail_media_references (
            id INTEGER PRIMARY KEY,
            voicemail_id INTEGER NOT NULL REFERENCES voicemails(id),
            ordinal INTEGER NOT NULL,
            raw_reference TEXT NOT NULL,
            matched_source_file_id INTEGER REFERENCES source_files(id),
            matched_relative_path TEXT,
            media_type TEXT,
            match_status TEXT NOT NULL
        );
        CREATE TABLE import_issues (
            id INTEGER PRIMARY KEY,
            import_run_id INTEGER NOT NULL REFERENCES import_runs(id),
            code TEXT NOT NULL,
            message TEXT NOT NULL,
            severity TEXT NOT NULL,
            source_file_id INTEGER REFERENCES source_files(id),
            source_relative_path TEXT,
            source_row_index INTEGER
        );
        CREATE INDEX ix_source_files_path ON source_files(source_archive_id, relative_path);
        CREATE INDEX ix_conversations_run_kind ON conversations(import_run_id, kind);
        CREATE INDEX ix_conversations_source_path ON conversations(source_relative_path);
        CREATE INDEX ix_participants_phone ON conversation_participants(normalized_phone_number);
        CREATE INDEX ix_messages_conversation_timestamp ON messages(conversation_id, timestamp_utc);
        CREATE INDEX ix_messages_source ON messages(source_file_id, source_row_index);
        CREATE INDEX ix_attachments_source ON attachments(matched_source_file_id);
        CREATE INDEX ix_calls_run_timestamp ON call_records(import_run_id, timestamp_utc);
        CREATE INDEX ix_calls_source ON call_records(source_file_id);
        CREATE INDEX ix_voicemails_run_timestamp ON voicemails(import_run_id, timestamp_utc);
        CREATE INDEX ix_voicemails_source ON voicemails(source_file_id);
        CREATE INDEX ix_call_media_source ON call_media_references(matched_source_file_id);
        CREATE INDEX ix_voicemail_media_source ON voicemail_media_references(matched_source_file_id);
        CREATE INDEX ix_issues_run_code ON import_issues(import_run_id, code);
        CREATE INDEX ix_issues_run_severity ON import_issues(import_run_id, severity);
        CREATE VIRTUAL TABLE message_search USING fts5(message_id UNINDEXED, conversation_id UNINDEXED, body);
        CREATE VIRTUAL TABLE event_search USING fts5(record_type UNINDEXED, record_id UNINDEXED, content);
        """;
}
