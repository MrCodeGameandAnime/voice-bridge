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

    public StoredPage<StoredConversationBrowserRow> ReadConversationPage(string? search, string? filter, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        search = NormalizeSearch(search);
        filter = NormalizeFilter(filter);
        const string predicate = """
            ($filter = 'All' OR ($filter = 'With attachments' AND EXISTS (
                SELECT 1 FROM messages fm JOIN attachments fa ON fa.message_id = fm.id WHERE fm.conversation_id = c.id
            )) OR ($filter = 'Groups' AND lower(c.kind) = 'group'))
            AND ($search IS NULL OR
                instr(lower(COALESCE(c.raw_label, '')), lower($search)) > 0 OR
                EXISTS (SELECT 1 FROM messages sm WHERE sm.conversation_id = c.id AND (
                    instr(lower(COALESCE(sm.body, '')), lower($search)) > 0 OR
                    instr(lower(COALESCE(sm.sender_display_name, '')), lower($search)) > 0 OR
                    instr(lower(COALESCE(sm.sender_phone_number, '')), lower($search)) > 0)) OR
                EXISTS (SELECT 1 FROM conversation_participants sp WHERE sp.conversation_id = c.id AND (
                    instr(lower(COALESCE(sp.normalized_display_name, '')), lower($search)) > 0 OR
                    instr(lower(COALESCE(sp.normalized_phone_number, '')), lower($search)) > 0)))
            """;
        using var countCommand = CreateCommand($"SELECT COUNT(*) FROM conversations c WHERE {predicate};");
        AddPageSearchParameters(countCommand, search, filter);
        var totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        using var command = CreateCommand($"""
            SELECT c.id, c.raw_label, c.kind, c.source_relative_path,
                   (SELECT COUNT(*) FROM messages cm WHERE cm.conversation_id = c.id),
                   (SELECT MIN(tm.timestamp_utc) FROM messages tm WHERE tm.conversation_id = c.id),
                   (SELECT MAX(tm.timestamp_utc) FROM messages tm WHERE tm.conversation_id = c.id),
                   (SELECT pm.body FROM messages pm WHERE pm.conversation_id = c.id
                    ORDER BY (pm.timestamp_utc IS NULL), pm.timestamp_utc DESC, pm.source_row_index DESC, pm.id DESC LIMIT 1),
                   COALESCE((SELECT group_concat(CASE
                       WHEN cp.normalized_display_name IS NOT NULL AND cp.normalized_phone_number IS NOT NULL THEN cp.normalized_display_name || ' · ' || cp.normalized_phone_number
                       ELSE COALESCE(cp.normalized_display_name, cp.normalized_phone_number, '(not recorded)') END, ', ')
                    FROM conversation_participants cp WHERE cp.conversation_id = c.id), '')
            FROM conversations c
            WHERE {predicate}
            ORDER BY c.id
            LIMIT $limit OFFSET $offset;
            """);
        AddPageSearchParameters(command, search, filter);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredConversationBrowserRow>();
        while (reader.Read())
        {
            rows.Add(new StoredConversationBrowserRow(
                reader.GetInt64(0), GetNullableString(reader, 1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                GetNullableString(reader, 5), GetNullableString(reader, 6), GetNullableString(reader, 7), reader.GetString(8)));
        }

        return new StoredPage<StoredConversationBrowserRow>(rows, totalCount);
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

    public IEnumerable<StoredMessage> ReadMessages(long? conversationId = null) => ReadMessagesCore(conversationId, null, null);

    public StoredPage<StoredMessage> ReadConversationMessagesPage(long conversationId, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        using var countCommand = CreateCommand("SELECT COUNT(*) FROM messages WHERE conversation_id = $conversation;");
        countCommand.Parameters.AddWithValue("$conversation", conversationId);
        var totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return new StoredPage<StoredMessage>(ReadMessagesCore(conversationId, offset, pageSize).ToArray(), totalCount);
    }

    private IEnumerable<StoredMessage> ReadMessagesCore(long? conversationId, int? offset, int? pageSize)
    {
        var sql = offset is not null && pageSize is not null
            ? """
            WITH selected_messages AS (
                SELECT id FROM messages WHERE conversation_id = $conversation
                ORDER BY (timestamp_utc IS NULL), timestamp_utc, source_row_index, id
                LIMIT $limit OFFSET $offset
            )
            SELECT m.id, m.conversation_id, m.source_file_id, m.source_relative_path, m.source_row_index,
                   m.raw_timestamp, m.timestamp_utc, m.body, m.sender_display_name, m.sender_phone_number, m.direction,
                   a.id, a.raw_reference, a.matched_source_file_id, a.matched_relative_path, a.media_type
            FROM selected_messages selected
            INNER JOIN messages m ON m.id = selected.id
            LEFT JOIN attachments a ON a.message_id = m.id
            ORDER BY (m.timestamp_utc IS NULL), m.timestamp_utc, m.source_row_index, m.id, a.id;
            """
            : conversationId is null
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
        if (offset is not null && pageSize is not null)
        {
            command.Parameters.AddWithValue("$limit", pageSize.Value);
            command.Parameters.AddWithValue("$offset", offset.Value);
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

    public IReadOnlyList<StoredCallRecord> ReadCalls() => ReadCallRecords(null, null, null, null);

    public StoredPage<StoredCallRecord> ReadCallPage(string? search, string? eventType, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        search = NormalizeSearch(search);
        eventType = NormalizeFilter(eventType);
        if (!_hasEventTables)
        {
            return new StoredPage<StoredCallRecord>([], 0);
        }

        const string predicate = "($eventType = 'All' OR c.raw_event_type = $eventType) AND ($search IS NULL OR instr(lower(COALESCE(c.raw_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(c.raw_filename_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(c.raw_phone_number, '')), lower($search)) > 0 OR instr(lower(COALESCE(c.raw_event_type, '')), lower($search)) > 0 OR instr(lower(c.source_relative_path), lower($search)) > 0)";
        using var countCommand = CreateCommand($"SELECT COUNT(*) FROM call_records c WHERE {predicate};");
        AddEventSearchParameters(countCommand, search, eventType);
        var totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return new StoredPage<StoredCallRecord>(ReadCallRecords(search, eventType, offset, pageSize), totalCount);
    }

    public IReadOnlyList<string> ReadCallEventTypes()
    {
        if (!_hasEventTables)
        {
            return [];
        }

        using var command = CreateCommand("SELECT DISTINCT raw_event_type FROM call_records WHERE raw_event_type IS NOT NULL ORDER BY raw_event_type COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private IReadOnlyList<StoredCallRecord> ReadCallRecords(string? search, string? eventFilter, int? offset, int? pageSize)
    {
        if (!_hasEventTables)
        {
            return [];
        }

        var predicate = "($eventType = 'All' OR c.raw_event_type = $eventType) AND ($search IS NULL OR instr(lower(COALESCE(c.raw_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(c.raw_filename_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(c.raw_phone_number, '')), lower($search)) > 0 OR instr(lower(COALESCE(c.raw_event_type, '')), lower($search)) > 0 OR instr(lower(c.source_relative_path), lower($search)) > 0)";
        var pageClause = offset is null || pageSize is null ? string.Empty : "LIMIT $limit OFFSET $offset";
        using var command = CreateCommand($"""
            WITH selected_calls AS (
                SELECT c.id FROM call_records c WHERE {predicate}
                ORDER BY (c.timestamp_utc IS NULL), c.timestamp_utc, c.id
                {pageClause}
            )
            SELECT c.id, c.source_file_id, c.source_relative_path, c.raw_event_type, c.raw_timestamp, c.timestamp_utc,
                   c.raw_contact, c.raw_filename_contact, c.raw_contact_source, c.raw_phone_number, c.raw_duration_title, c.duration_display_text, c.duration_seconds,
                   m.id, m.raw_reference, m.matched_source_file_id, m.matched_relative_path, m.media_type, m.match_status
            FROM selected_calls selected
            INNER JOIN call_records c ON c.id = selected.id
            LEFT JOIN call_media_references m ON m.call_record_id = c.id
            ORDER BY (c.timestamp_utc IS NULL), c.timestamp_utc, c.id, m.ordinal;
            """);
        AddEventSearchParameters(command, NormalizeSearch(search), NormalizeFilter(eventFilter));
        if (offset is not null && pageSize is not null)
        {
            command.Parameters.AddWithValue("$limit", pageSize.Value);
            command.Parameters.AddWithValue("$offset", offset.Value);
        }
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

    public IReadOnlyList<StoredVoicemail> ReadVoicemails() => ReadVoicemailRecords(null, null, null);

    public StoredPage<StoredVoicemail> ReadVoicemailPage(string? search, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        search = NormalizeSearch(search);
        if (!_hasEventTables)
        {
            return new StoredPage<StoredVoicemail>([], 0);
        }

        const string predicate = "($search IS NULL OR instr(lower(COALESCE(v.raw_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(v.raw_filename_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(v.raw_phone_number, '')), lower($search)) > 0 OR instr(lower(COALESCE(v.transcript, '')), lower($search)) > 0 OR instr(lower(v.source_relative_path), lower($search)) > 0)";
        using var countCommand = CreateCommand($"SELECT COUNT(*) FROM voicemails v WHERE {predicate};");
        AddSearchParameter(countCommand, search);
        var totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return new StoredPage<StoredVoicemail>(ReadVoicemailRecords(search, offset, pageSize), totalCount);
    }

    private IReadOnlyList<StoredVoicemail> ReadVoicemailRecords(string? search, int? offset, int? pageSize)
    {
        if (!_hasEventTables)
        {
            return [];
        }

        const string predicate = "($search IS NULL OR instr(lower(COALESCE(v.raw_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(v.raw_filename_contact, '')), lower($search)) > 0 OR instr(lower(COALESCE(v.raw_phone_number, '')), lower($search)) > 0 OR instr(lower(COALESCE(v.transcript, '')), lower($search)) > 0 OR instr(lower(v.source_relative_path), lower($search)) > 0)";
        var pageClause = offset is null || pageSize is null ? string.Empty : "LIMIT $limit OFFSET $offset";
        using var command = CreateCommand($"""
            WITH selected_voicemails AS (
                SELECT v.id FROM voicemails v WHERE {predicate}
                ORDER BY (v.timestamp_utc IS NULL), v.timestamp_utc, v.id
                {pageClause}
            )
            SELECT v.id, v.source_file_id, v.source_relative_path, v.raw_timestamp, v.timestamp_utc,
                   v.raw_contact, v.raw_filename_contact, v.raw_contact_source, v.raw_phone_number, v.transcript, v.raw_duration_title, v.duration_display_text,
                   v.duration_seconds, v.audio_reference, v.matched_audio_source_file_id, v.matched_audio_relative_path,
                   v.audio_match_status, m.id, m.raw_reference, m.matched_source_file_id, m.matched_relative_path, m.media_type, m.match_status
            FROM selected_voicemails selected
            INNER JOIN voicemails v ON v.id = selected.id
            LEFT JOIN voicemail_media_references m ON m.voicemail_id = v.id
            ORDER BY (v.timestamp_utc IS NULL), v.timestamp_utc, v.id, m.ordinal;
            """);
        AddSearchParameter(command, NormalizeSearch(search));
        if (offset is not null && pageSize is not null)
        {
            command.Parameters.AddWithValue("$limit", pageSize.Value);
            command.Parameters.AddWithValue("$offset", offset.Value);
        }
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

    public StoredPage<StoredMediaBrowserItem> ReadMediaPage(string? search, string? mediaType, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        search = NormalizeSearch(search);
        mediaType = NormalizeFilter(mediaType);
        var mediaRows = BuildMediaRowsSql();
        const string predicate = "($mediaType = 'All' OR (lower($mediaType) = 'unresolved' AND record_type <> 'source_file' AND matched_source_file_id IS NULL) OR lower(COALESCE(media_type, '')) = lower($mediaType)) AND ($search IS NULL OR instr(lower(COALESCE(raw_reference, '')), lower($search)) > 0 OR instr(lower(COALESCE(matched_relative_path, '')), lower($search)) > 0 OR instr(lower(COALESCE(source_relative_path, '')), lower($search)) > 0)";
        using var countCommand = CreateCommand($"WITH media_rows AS ({mediaRows}) SELECT COUNT(*) FROM media_rows WHERE {predicate};");
        AddMediaSearchParameters(countCommand, search, mediaType);
        var totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        using var command = CreateCommand($"""
            WITH media_rows AS ({mediaRows})
            SELECT record_type, record_id, parent_record_id, raw_reference, matched_source_file_id,
                   matched_relative_path, media_type, match_status, source_relative_path, size_bytes, content_sha256
            FROM media_rows
            WHERE {predicate}
            ORDER BY record_type, record_id
            LIMIT $limit OFFSET $offset;
            """);
        AddMediaSearchParameters(command, search, mediaType);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var items = new List<StoredMediaBrowserItem>();
        while (reader.Read())
        {
            items.Add(new StoredMediaBrowserItem(
                reader.GetString(0), reader.GetInt64(1), GetNullableInt64(reader, 2), GetNullableString(reader, 3),
                GetNullableInt64(reader, 4), GetNullableString(reader, 5), GetNullableString(reader, 6), reader.GetString(7),
                GetNullableString(reader, 8), GetNullableInt64(reader, 9), GetNullableString(reader, 10)));
        }

        return new StoredPage<StoredMediaBrowserItem>(items, totalCount);
    }

    private string BuildMediaRowsSql()
    {
        var sourceHash = _sourceFilesHaveContentHash ? "sf.content_sha256" : "NULL";
        var unions = new List<string>
        {
            """
            SELECT 'message_attachment' AS record_type, a.id AS record_id, a.message_id AS parent_record_id,
                   a.raw_reference, a.matched_source_file_id, a.matched_relative_path, a.media_type,
                   CASE WHEN a.matched_source_file_id IS NULL THEN 'unresolved' ELSE 'matched' END AS match_status,
                   m.source_relative_path, sf.size_bytes, sf.content_sha256
            FROM attachments a INNER JOIN messages m ON m.id = a.message_id
            LEFT JOIN source_files sf ON sf.id = a.matched_source_file_id
            """.Replace("sf.content_sha256", sourceHash, StringComparison.Ordinal)
        };
        if (_hasEventTables)
        {
            unions.Add("""
                SELECT 'call_media' AS record_type, mr.id AS record_id, mr.call_record_id AS parent_record_id,
                       mr.raw_reference, mr.matched_source_file_id, mr.matched_relative_path, mr.media_type,
                       mr.match_status, c.source_relative_path, sf.size_bytes, sf.content_sha256
                FROM call_media_references mr INNER JOIN call_records c ON c.id = mr.call_record_id
                LEFT JOIN source_files sf ON sf.id = mr.matched_source_file_id
                """.Replace("sf.content_sha256", sourceHash, StringComparison.Ordinal));
            unions.Add("""
                SELECT 'voicemail_media' AS record_type, mr.id AS record_id, mr.voicemail_id AS parent_record_id,
                       mr.raw_reference, mr.matched_source_file_id, mr.matched_relative_path, mr.media_type,
                       mr.match_status, v.source_relative_path, sf.size_bytes, sf.content_sha256
                FROM voicemail_media_references mr INNER JOIN voicemails v ON v.id = mr.voicemail_id
                LEFT JOIN source_files sf ON sf.id = mr.matched_source_file_id
                """.Replace("sf.content_sha256", sourceHash, StringComparison.Ordinal));
        }

        unions.Add($"""
            SELECT 'source_file' AS record_type, sf.id AS record_id, NULL AS parent_record_id,
                   NULL AS raw_reference, sf.id AS matched_source_file_id, sf.relative_path AS matched_relative_path,
                   sf.media_type, 'source_file' AS match_status, sf.relative_path AS source_relative_path,
                   sf.size_bytes, {sourceHash} AS content_sha256
            FROM source_files sf WHERE lower(COALESCE(sf.media_type, '')) IN ('audio', 'image', 'video')
            """);
        return string.Join("\nUNION ALL\n", unions);
    }

    public StoredPage<StoredImportIssue> ReadImportIssuePage(string? search, string? code, int offset, int pageSize)
    {
        ValidatePage(offset, pageSize);
        search = NormalizeSearch(search);
        code = NormalizeFilter(code);
        const string predicate = "($code = 'All' OR i.code = $code) AND ($search IS NULL OR instr(lower(i.code), lower($search)) > 0 OR instr(lower(i.message), lower($search)) > 0 OR instr(lower(i.severity), lower($search)) > 0 OR instr(lower(COALESCE(i.source_relative_path, '')), lower($search)) > 0)";
        using var countCommand = CreateCommand($"SELECT COUNT(*) FROM import_issues i WHERE {predicate};");
        AddIssueSearchParameters(countCommand, search, code);
        var totalCount = Convert.ToInt64(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        using var command = CreateCommand($"""
            SELECT i.id, i.code, i.message, i.severity, i.source_file_id, i.source_relative_path, i.source_row_index
            FROM import_issues i WHERE {predicate}
            ORDER BY i.id LIMIT $limit OFFSET $offset;
            """);
        AddIssueSearchParameters(command, search, code);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var items = new List<StoredImportIssue>();
        while (reader.Read())
        {
            items.Add(new StoredImportIssue(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                GetNullableInt64(reader, 4), GetNullableString(reader, 5), GetNullableInt32(reader, 6)));
        }

        return new StoredPage<StoredImportIssue>(items, totalCount);
    }

    public IReadOnlyList<string> ReadImportIssueCodes()
    {
        using var command = CreateCommand("SELECT DISTINCT code FROM import_issues ORDER BY code COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
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

    private static void ValidatePage(int offset, int pageSize)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Page offset cannot be negative.");
        }

        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 200.");
        }
    }

    private static string? NormalizeSearch(string? search) => string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    private static string NormalizeFilter(string? filter) => string.IsNullOrWhiteSpace(filter) ? "All" : filter.Trim();

    private static void AddSearchParameter(SqliteCommand command, string? search) =>
        command.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);

    private static void AddPageSearchParameters(SqliteCommand command, string? search, string filter)
    {
        AddSearchParameter(command, search);
        command.Parameters.AddWithValue("$filter", filter);
    }

    private static void AddEventSearchParameters(SqliteCommand command, string? search, string? eventType)
    {
        AddSearchParameter(command, search);
        command.Parameters.AddWithValue("$eventType", (object?)eventType ?? "All");
    }

    private static void AddMediaSearchParameters(SqliteCommand command, string? search, string mediaType)
    {
        AddSearchParameter(command, search);
        command.Parameters.AddWithValue("$mediaType", mediaType);
    }

    private static void AddIssueSearchParameters(SqliteCommand command, string? search, string code)
    {
        AddSearchParameter(command, search);
        command.Parameters.AddWithValue("$code", code);
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
