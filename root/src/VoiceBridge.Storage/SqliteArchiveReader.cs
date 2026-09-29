using Microsoft.Data.Sqlite;
using VoiceBridge.Core.Domain;

namespace VoiceBridge.Storage;

public sealed class SqliteArchiveReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _sourceFilesHaveContentHash;
    private readonly bool _hasEventTables;

    public SqliteArchiveReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        _connection.Open();
        _sourceFilesHaveContentHash = HasColumn("source_files", "content_sha256");
        _hasEventTables = HasTable("call_records") && HasTable("voicemails");
    }

    public StoredArchiveMetadata ReadArchiveMetadata()
    {
        using var command = CreateCommand("SELECT source_kind, source_path, source_sha256 FROM source_archives ORDER BY id LIMIT 1;");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The database does not contain an imported source archive.");
        }

        if (!Enum.TryParse<SourceKind>(reader.GetString(0), out var sourceKind))
        {
            throw new InvalidDataException("The database contains an unknown source archive kind.");
        }

        return new StoredArchiveMetadata(sourceKind, reader.GetString(1), GetNullableString(reader, 2));
    }

    public IReadOnlyList<StoredConversationSummary> ReadConversations()
    {
        using var command = CreateCommand("SELECT id, source_file_id, source_relative_path, raw_label, kind FROM conversations ORDER BY id;");
        using var reader = command.ExecuteReader();
        var records = new List<StoredConversationSummary>();
        while (reader.Read())
        {
            records.Add(new StoredConversationSummary(
                reader.GetInt64(0),
                GetNullableInt64(reader, 1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                reader.GetString(4)));
        }

        return records;
    }

    public IReadOnlyList<StoredParticipant> ReadParticipants(long conversationId)
    {
        using var command = CreateCommand("""
            SELECT cp.id, cp.conversation_id, cp.normalized_display_name, cp.normalized_phone_number,
                   pe.id, pe.source_type, pe.source_row_index, pe.raw_display_name, pe.raw_phone_number
            FROM conversation_participants cp
            LEFT JOIN participant_evidence pe ON pe.participant_id = cp.id
            WHERE cp.conversation_id = $conversation
            ORDER BY cp.id, pe.id;
            """);
        command.Parameters.AddWithValue("$conversation", conversationId);
        using var reader = command.ExecuteReader();
        var participants = new List<StoredParticipant>();
        long? currentId = null;
        List<StoredParticipantEvidence>? evidence = null;
        long currentConversationId = 0;
        string? displayName = null;
        string? phone = null;

        void FlushParticipant()
        {
            if (currentId is not null)
            {
                participants.Add(new StoredParticipant(currentId.Value, currentConversationId, displayName, phone, evidence!.ToArray()));
            }
        }

        while (reader.Read())
        {
            var participantId = reader.GetInt64(0);
            if (currentId != participantId)
            {
                FlushParticipant();
                currentId = participantId;
                currentConversationId = reader.GetInt64(1);
                displayName = GetNullableString(reader, 2);
                phone = GetNullableString(reader, 3);
                evidence = [];
            }

            if (!reader.IsDBNull(4))
            {
                evidence!.Add(new StoredParticipantEvidence(
                    reader.GetInt64(4),
                    reader.GetString(5),
                    GetNullableInt32(reader, 6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8)));
            }
        }

        FlushParticipant();
        return participants;
    }

    public IEnumerable<StoredMessage> ReadMessages(long? conversationId = null)
    {
        var sql = conversationId is null
            ? """
            SELECT m.id, m.conversation_id, m.source_file_id, m.source_relative_path, m.source_row_index,
                   m.raw_timestamp, m.timestamp_utc, m.body, m.sender_display_name, m.sender_phone_number, m.direction,
                   a.id, a.raw_reference, a.matched_source_file_id, a.matched_relative_path, a.media_type
            FROM messages m
            LEFT JOIN attachments a ON a.message_id = m.id
            ORDER BY m.conversation_id, (m.timestamp_utc IS NULL), m.timestamp_utc, m.source_row_index, m.id, a.id;
            """
            : """
            SELECT m.id, m.conversation_id, m.source_file_id, m.source_relative_path, m.source_row_index,
                   m.raw_timestamp, m.timestamp_utc, m.body, m.sender_display_name, m.sender_phone_number, m.direction,
                   a.id, a.raw_reference, a.matched_source_file_id, a.matched_relative_path, a.media_type
            FROM messages m
            LEFT JOIN attachments a ON a.message_id = m.id
            WHERE m.conversation_id = $conversation
            ORDER BY (m.timestamp_utc IS NULL), m.timestamp_utc, m.source_row_index, m.id, a.id;
            """;
        using var command = CreateCommand(sql);
        if (conversationId is not null)
        {
            command.Parameters.AddWithValue("$conversation", conversationId.Value);
        }
        using var reader = command.ExecuteReader();

        long? currentId = null;
        long currentConversation = 0;
        long? sourceFileId = null;
        string sourceRelativePath = string.Empty;
        int? sourceRowIndex = null;
        string? rawTimestamp = null;
        string? timestampUtc = null;
        string? body = null;
        string? senderDisplayName = null;
        string? senderPhoneNumber = null;
        string? direction = null;
        List<StoredAttachment>? attachments = null;

        StoredMessage FlushMessage() => new(
            currentId!.Value,
            currentConversation,
            sourceFileId,
            sourceRelativePath,
            sourceRowIndex,
            rawTimestamp,
            timestampUtc,
            body,
            senderDisplayName,
            senderPhoneNumber,
            direction,
            attachments!.ToArray());

        while (reader.Read())
        {
            var messageId = reader.GetInt64(0);
            if (currentId != messageId)
            {
                if (currentId is not null)
                {
                    yield return FlushMessage();
                }

                currentId = messageId;
                currentConversation = reader.GetInt64(1);
                sourceFileId = GetNullableInt64(reader, 2);
                sourceRelativePath = reader.GetString(3);
                sourceRowIndex = GetNullableInt32(reader, 4);
                rawTimestamp = GetNullableString(reader, 5);
                timestampUtc = GetNullableString(reader, 6);
                body = GetNullableString(reader, 7);
                senderDisplayName = GetNullableString(reader, 8);
                senderPhoneNumber = GetNullableString(reader, 9);
                direction = GetNullableString(reader, 10);
                attachments = [];
            }

            if (!reader.IsDBNull(11))
            {
                attachments!.Add(new StoredAttachment(
                    reader.GetInt64(11),
                    messageId,
                    reader.GetString(12),
                    GetNullableInt64(reader, 13),
                    GetNullableString(reader, 14),
                    GetNullableString(reader, 15)));
            }
        }

        if (currentId is not null)
        {
            yield return FlushMessage();
        }
    }

    public IEnumerable<StoredSourceFile> ReadSourceFiles()
    {
        var contentHashColumn = _sourceFilesHaveContentHash ? "content_sha256" : "NULL";
        using var command = CreateCommand($"SELECT id, ordinal, relative_path, size_bytes, media_type, {contentHashColumn} FROM source_files ORDER BY id;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new StoredSourceFile(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5));
        }
    }

    public IReadOnlyList<StoredSourceFile> ReadMatchedMediaFiles()
    {
        var references = _hasEventTables
            ? "SELECT matched_source_file_id FROM attachments UNION SELECT matched_source_file_id FROM call_media_references UNION SELECT matched_source_file_id FROM voicemail_media_references"
            : "SELECT matched_source_file_id FROM attachments";
        using var command = CreateCommand($"""
            SELECT DISTINCT sf.id, sf.ordinal, sf.relative_path, sf.size_bytes, sf.media_type, {(_sourceFilesHaveContentHash ? "sf.content_sha256" : "NULL")}
            FROM source_files sf
            WHERE sf.id IN ({references})
            ORDER BY sf.id;
            """);
        using var reader = command.ExecuteReader();
        var records = new List<StoredSourceFile>();
        while (reader.Read())
        {
            records.Add(new StoredSourceFile(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5)));
        }

        return records;
    }

    public IReadOnlyList<StoredCallRecord> ReadCalls()
    {
        if (!_hasEventTables)
        {
            return [];
        }

        using var command = CreateCommand("""
            SELECT c.id, c.source_file_id, c.source_relative_path, c.raw_event_type, c.raw_timestamp, c.timestamp_utc,
                   c.raw_contact, c.raw_filename_contact, c.raw_contact_source, c.raw_phone_number, c.raw_duration_title, c.duration_display_text, c.duration_seconds,
                   m.id, m.raw_reference, m.matched_source_file_id, m.matched_relative_path, m.media_type, m.match_status
            FROM call_records c
            LEFT JOIN call_media_references m ON m.call_record_id = c.id
            ORDER BY (c.timestamp_utc IS NULL), c.timestamp_utc, c.id, m.ordinal;
            """);
        using var reader = command.ExecuteReader();
        var calls = new List<StoredCallRecord>();
        long? currentId = null;
        long? sourceFileId = null;
        string sourcePath = string.Empty;
        string? eventType = null;
        string? rawTimestamp = null;
        string? timestampUtc = null;
        string? contact = null;
        string? filenameContact = null;
        string? contactSource = null;
        string? phone = null;
        string? rawDurationTitle = null;
        string? durationText = null;
        double? durationSeconds = null;
        List<StoredMediaReference>? mediaReferences = null;

        void Flush()
        {
            if (currentId is not null)
            {
                calls.Add(new StoredCallRecord(
                    currentId.Value,
                    sourceFileId,
                    sourcePath,
                    eventType,
                    rawTimestamp,
                    timestampUtc,
                    contact,
                    filenameContact,
                    contactSource,
                    phone,
                    rawDurationTitle,
                    durationText,
                    durationSeconds,
                    mediaReferences!.ToArray()));
            }
        }

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (currentId != id)
            {
                Flush();
                currentId = id;
                sourceFileId = GetNullableInt64(reader, 1);
                sourcePath = reader.GetString(2);
                eventType = GetNullableString(reader, 3);
                rawTimestamp = GetNullableString(reader, 4);
                timestampUtc = GetNullableString(reader, 5);
                contact = GetNullableString(reader, 6);
                filenameContact = GetNullableString(reader, 7);
                contactSource = GetNullableString(reader, 8);
                phone = GetNullableString(reader, 9);
                rawDurationTitle = GetNullableString(reader, 10);
                durationText = GetNullableString(reader, 11);
                durationSeconds = GetNullableDouble(reader, 12);
                mediaReferences = [];
            }

            if (!reader.IsDBNull(13))
            {
                mediaReferences!.Add(new StoredMediaReference(
                    reader.GetInt64(13), id, reader.GetString(14), GetNullableInt64(reader, 15),
                    GetNullableString(reader, 16), GetNullableString(reader, 17), reader.GetString(18)));
            }
        }

        Flush();
        return calls;
    }

    public IReadOnlyList<StoredVoicemail> ReadVoicemails()
    {
        if (!_hasEventTables)
        {
            return [];
        }

        using var command = CreateCommand("""
            SELECT v.id, v.source_file_id, v.source_relative_path, v.raw_timestamp, v.timestamp_utc,
                   v.raw_contact, v.raw_filename_contact, v.raw_contact_source, v.raw_phone_number, v.transcript, v.raw_duration_title, v.duration_display_text,
                   v.duration_seconds, v.audio_reference, v.matched_audio_source_file_id, v.matched_audio_relative_path,
                   v.audio_match_status, m.id, m.raw_reference, m.matched_source_file_id, m.matched_relative_path, m.media_type, m.match_status
            FROM voicemails v
            LEFT JOIN voicemail_media_references m ON m.voicemail_id = v.id
            ORDER BY (v.timestamp_utc IS NULL), v.timestamp_utc, v.id, m.ordinal;
            """);
        using var reader = command.ExecuteReader();
        var voicemails = new List<StoredVoicemail>();
        long? currentId = null;
        long? sourceFileId = null;
        string sourcePath = string.Empty;
        string? rawTimestamp = null;
        string? timestampUtc = null;
        string? contact = null;
        string? filenameContact = null;
        string? contactSource = null;
        string? phone = null;
        string? transcript = null;
        string? rawDurationTitle = null;
        string? durationText = null;
        double? durationSeconds = null;
        string? audioReference = null;
        long? matchedAudioSourceFileId = null;
        string? matchedAudioRelativePath = null;
        string audioMatchStatus = "missing_reference";
        List<StoredMediaReference>? mediaReferences = null;

        void Flush()
        {
            if (currentId is not null)
            {
                voicemails.Add(new StoredVoicemail(
                    currentId.Value,
                    sourceFileId,
                    sourcePath,
                    rawTimestamp,
                    timestampUtc,
                    contact,
                    filenameContact,
                    contactSource,
                    phone,
                    transcript,
                    rawDurationTitle,
                    durationText,
                    durationSeconds,
                    audioReference,
                    matchedAudioSourceFileId,
                    matchedAudioRelativePath,
                    audioMatchStatus,
                    mediaReferences!.ToArray()));
            }
        }

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (currentId != id)
            {
                Flush();
                currentId = id;
                sourceFileId = GetNullableInt64(reader, 1);
                sourcePath = reader.GetString(2);
                rawTimestamp = GetNullableString(reader, 3);
                timestampUtc = GetNullableString(reader, 4);
                contact = GetNullableString(reader, 5);
                filenameContact = GetNullableString(reader, 6);
                contactSource = GetNullableString(reader, 7);
                phone = GetNullableString(reader, 8);
                transcript = GetNullableString(reader, 9);
                rawDurationTitle = GetNullableString(reader, 10);
                durationText = GetNullableString(reader, 11);
                durationSeconds = GetNullableDouble(reader, 12);
                audioReference = GetNullableString(reader, 13);
                matchedAudioSourceFileId = GetNullableInt64(reader, 14);
                matchedAudioRelativePath = GetNullableString(reader, 15);
                audioMatchStatus = reader.GetString(16);
                mediaReferences = [];
            }

            if (!reader.IsDBNull(17))
            {
                mediaReferences!.Add(new StoredMediaReference(
                    reader.GetInt64(17), id, reader.GetString(18), GetNullableInt64(reader, 19),
                    GetNullableString(reader, 20), GetNullableString(reader, 21), reader.GetString(22)));
            }
        }

        Flush();
        return voicemails;
    }

    public IEnumerable<StoredSearchEvent> ReadSearchEvents()
    {
        if (!_hasEventTables)
        {
            yield break;
        }

        using var command = CreateCommand("""
            SELECT 'call', id, timestamp_utc, raw_event_type, raw_contact, raw_filename_contact, raw_phone_number,
                   NULL
            FROM call_records
            UNION ALL
            SELECT 'voicemail', id, timestamp_utc, 'Voicemail', raw_contact, raw_filename_contact, raw_phone_number, transcript
            FROM voicemails
            ORDER BY 3, 1, 2;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new StoredSearchEvent(
                reader.GetString(0),
                reader.GetInt64(1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7));
        }
    }

    public IEnumerable<StoredImportIssue> ReadImportIssues()
    {
        using var command = CreateCommand("""
            SELECT id, code, message, severity, source_file_id, source_relative_path, source_row_index
            FROM import_issues
            ORDER BY id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new StoredImportIssue(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                GetNullableInt64(reader, 4),
                GetNullableString(reader, 5),
                GetNullableInt32(reader, 6));
        }
    }

    public IEnumerable<StoredSearchMessage> ReadSearchMessages()
    {
        using var command = CreateCommand("""
            SELECT m.id, m.conversation_id, m.timestamp_utc, m.body, c.raw_label, m.sender_display_name, m.sender_phone_number
            FROM messages m
            INNER JOIN conversations c ON c.id = m.conversation_id
            ORDER BY m.id;
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new StoredSearchMessage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6));
        }
    }

    public void Dispose() => _connection.Dispose();

    private SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private bool HasColumn(string tableName, string columnName)
    {
        using var command = CreateCommand($"PRAGMA table_info({tableName});");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasTable(string tableName)
    {
        using var command = CreateCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;");
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
