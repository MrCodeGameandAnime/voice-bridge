using Microsoft.Data.Sqlite;
using VoiceBridge.Core.Domain;

namespace VoiceBridge.Storage;

public sealed class SqliteArchiveReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _sourceFilesHaveContentHash;

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
        using var command = CreateCommand($"""
            SELECT DISTINCT sf.id, sf.ordinal, sf.relative_path, sf.size_bytes, sf.media_type, {(_sourceFilesHaveContentHash ? "sf.content_sha256" : "NULL")}
            FROM source_files sf
            INNER JOIN attachments a ON a.matched_source_file_id = sf.id
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

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
