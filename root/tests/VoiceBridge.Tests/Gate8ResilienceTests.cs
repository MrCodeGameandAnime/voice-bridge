using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using VoiceBridge.Core.Importing;
using VoiceBridge.Storage;

namespace VoiceBridge.Tests;

public sealed class Gate8ResilienceTests
{
    [Fact]
    public async Task ImportsLargeArchiveWithoutDroppingPagesOrRows()
    {
        const int pageCount = 1024;
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "large-takeout.zip");
        var databasePath = Path.Combine(temporary.RootPath, "result", "voicebridge.db");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            for (var index = 0; index < pageCount; index++)
            {
                var html = MessageHtml($"Contact {index}", $"+1555{index:D7}", $"Message {index}");
                WriteEntry(
                    archive,
                    $"Takeout/Voice/Calls/Contact {index} - Text - 2024-01-02T03_04_05Z.html",
                    html);
            }
        }

        var report = await new TakeoutImportService(new SqliteTakeoutImportStoreFactory())
            .ImportAsync(archivePath, databasePath);

        Assert.Equal(pageCount, report.FilesScanned);
        Assert.Equal(pageCount, report.RecordsParsed.Conversations);
        Assert.Equal(pageCount, report.RecordsParsed.Messages);
        Assert.Equal(0, report.RecordsSkipped);
        Assert.Equal(0, report.Errors);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        Assert.Equal(pageCount, await ReadCountAsync(connection, "SELECT COUNT(*) FROM source_files;"));
        Assert.Equal(pageCount, await ReadCountAsync(connection, "SELECT COUNT(*) FROM conversations;"));
        Assert.Equal(pageCount, await ReadCountAsync(connection, "SELECT COUNT(*) FROM messages;"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM import_runs WHERE status = 'Completed' AND messages_parsed = 1024 AND conversations_parsed = 1024 AND records_skipped = 0 AND errors = 0;"));
    }

    [Fact]
    public async Task OutOfRangeCallDurationDoesNotAbortImportOfRemainingPages()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "malformed-duration.zip");
        var databasePath = Path.Combine(temporary.RootPath, "result", "voicebridge.db");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "Takeout/Voice/Calls/Caller - Received - 2024-01-02T03_04_05Z.html",
                "<html><body><div class=haudio><span class=fn>Received call from Caller</span><a class=tel href=\"tel:+15551234567\">Caller</a><abbr class=published title=\"2024-01-02T03:04:05-05:00\"></abbr><abbr class=duration title=\"P999999999D\"></abbr></div></body></html>");
            WriteEntry(
                archive,
                "Takeout/Voice/Calls/Other - Text - 2024-01-02T03_05_05Z.html",
                MessageHtml("Other", "+15557654321", "Still imported"));
        }

        var report = await new TakeoutImportService(new SqliteTakeoutImportStoreFactory())
            .ImportAsync(archivePath, databasePath);

        Assert.Equal(1, report.RecordsParsed.Calls);
        Assert.Equal(1, report.RecordsParsed.Messages);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.RecordsSkipped);
    }

    [Fact]
    public async Task ImportsPartialTakeoutFromPathsContainingSpacesAndReportsMissingMedia()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "partial Takeout with spaces");
        var callsPath = Path.Combine(sourcePath, "Voice", "Calls");
        Directory.CreateDirectory(callsPath);
        File.WriteAllText(
            Path.Combine(callsPath, "Contact - Text - 2024-01-02T03_04_05Z.html"),
            MessageHtml("Contact", "+15551234567", "Partial export", "<img src=\"../media/missing photo.jpg\">"));
        var outputDirectory = Path.Combine(temporary.RootPath, "VoiceBridge output with spaces");
        var databasePath = Path.Combine(outputDirectory, "voicebridge.db");

        var report = await new TakeoutImportService(new SqliteTakeoutImportStoreFactory())
            .ImportAsync(sourcePath, databasePath);

        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(1, report.RecordsParsed.Messages);
        Assert.Equal(0, report.RecordsParsed.Calls);
        Assert.Equal(0, report.RecordsParsed.Voicemails);
        Assert.Equal(1, report.RecordsParsed.Attachments);
        Assert.Equal(1, report.RecordsParsed.UnresolvedMediaReferences);
        Assert.Equal(0, report.Errors);
        Assert.Contains(report.Issues, issue => issue.Code == "attachment_reference_unresolved");
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public async Task ImportsExtractedTakeoutAndDatabaseThroughLongWindowsPaths()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = BuildLongPath(temporary.RootPath, 's');
        var callsPath = Path.Combine(sourcePath, "Takeout", "Voice", "Calls");
        Directory.CreateDirectory(callsPath);
        var messagePath = Path.Combine(callsPath, "Contact - Text - 2024-01-02T03_04_05Z.html");
        File.WriteAllText(messagePath, MessageHtml("Contact", "+15551234567", "Long path import"));
        var outputDirectory = BuildLongPath(temporary.RootPath, 'o');
        var databasePath = Path.Combine(outputDirectory, "voicebridge.db");
        Assert.True(messagePath.Length > 260);
        Assert.True(databasePath.Length > 260);

        var report = await new TakeoutImportService(new SqliteTakeoutImportStoreFactory())
            .ImportAsync(Path.Combine(sourcePath, "Takeout"), databasePath);

        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(1, report.RecordsParsed.Messages);
        Assert.Equal(0, report.Errors);
        Assert.True(File.Exists(databasePath));
    }

    private static string BuildLongPath(string root, char character)
    {
        var path = root;
        for (var index = 0; index < 4; index++)
        {
            path = Path.Combine(path, new string((char)(character + index), 58));
        }

        return path;
    }

    private static string MessageHtml(string contact, string phone, string body, string extra = "") =>
        $"<html><body><div class=message><abbr class=dt title=\"2024-01-02T03:04:05+00:00\"></abbr><a class=tel href=\"tel:{phone}\">{contact}</a><q>{body}</q>{extra}</div></body></html>";

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static async Task<long> ReadCountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
