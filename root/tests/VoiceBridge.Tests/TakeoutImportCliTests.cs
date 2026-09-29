using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VoiceBridge.Core.Importing;
using VoiceBridge.Storage;

namespace VoiceBridge.Tests;

[Collection("CLI commands")]
public sealed class TakeoutImportCliTests
{
    [Fact]
    public async Task ImportCommandCreatesLocalDatabaseAndAuditReportWithoutChangingZip()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var outputPath = Path.Combine(temporary.RootPath, "result");
        Directory.CreateDirectory(outputPath);
        CreateArchive(archivePath);
        var originalHash = SHA256.HashData(File.ReadAllBytes(archivePath));

        var result = CliTestHost.Run("import", archivePath, "--output", outputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Import complete", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.StandardError);
        var databasePath = Path.Combine(outputPath, "voicebridge.db");
        var reportPath = Path.Combine(outputPath, "import-report.json");
        Assert.True(File.Exists(databasePath));
        Assert.True(File.Exists(reportPath));
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(File.ReadAllBytes(databasePath)[..16]));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(archivePath)));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        Assert.Equal(4, await ReadCountAsync(connection, "SELECT COUNT(*) FROM source_files;"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM conversations;"));
        Assert.Equal(2, await ReadCountAsync(connection, "SELECT COUNT(*) FROM messages;"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM conversation_participants;"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM attachments WHERE matched_source_file_id IS NOT NULL;"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM import_issues WHERE code = 'unsupported_message_page';"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM message_search WHERE message_search MATCH 'First';"));

        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = report.RootElement;
        Assert.Equal("zipArchive", root.GetProperty("inputIdentity").GetProperty("sourceKind").GetString());
        Assert.Equal(4, root.GetProperty("filesScanned").GetInt64());
        Assert.Equal(2, root.GetProperty("recordsParsed").GetProperty("messages").GetInt64());
        Assert.Equal(1, root.GetProperty("recordsParsed").GetProperty("conversations").GetInt64());
        Assert.Equal(1, root.GetProperty("recordsParsed").GetProperty("attachments").GetInt64());
        Assert.Equal(1, root.GetProperty("recordsSkipped").GetInt64());
        Assert.Equal(0, root.GetProperty("errors").GetInt64());
        Assert.Equal(2, root.GetProperty("warnings").GetInt64());
        Assert.Equal(1, root.GetProperty("unsupportedStructures").GetProperty("unsupported_message_page").GetInt64());
        Assert.Contains(
            root.GetProperty("issues").EnumerateArray(),
            issue => issue.GetProperty("sourceRelativePath").GetString() == "Takeout/Voice/Calls/Caller - Received - 2024-01-02T03_04_05Z.html");
        Assert.DoesNotContain("First body", File.ReadAllText(reportPath), StringComparison.Ordinal);
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ImportCommandRefusesToReplaceAnExistingDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var outputPath = Path.Combine(temporary.RootPath, "result");
        Directory.CreateDirectory(outputPath);
        CreateArchive(archivePath);
        var databasePath = Path.Combine(outputPath, "voicebridge.db");
        var existingBytes = Encoding.UTF8.GetBytes("existing user data");
        File.WriteAllBytes(databasePath, existingBytes);

        var result = CliTestHost.Run("import", archivePath, "--output", outputPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existingBytes, File.ReadAllBytes(databasePath));
        Assert.False(File.Exists(Path.Combine(outputPath, "import-report.json")));
    }

    [Fact]
    public async Task ImportServiceAcceptsExtractedDirectoryAndEmitsCoreProgress()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "Takeout");
        var callsPath = Path.Combine(sourcePath, "Voice", "Calls");
        Directory.CreateDirectory(callsPath);
        File.WriteAllText(Path.Combine(callsPath, "Alex - Text - 2024-01-02T03_04_05Z.html"), """
            <html><body><div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Folder import</q></div></body></html>
            """);
        var databasePath = Path.Combine(temporary.RootPath, "result", "voicebridge.db");
        var service = new TakeoutImportService(new SqliteTakeoutImportStoreFactory());
        var progress = new List<ImportProgress>();
        service.ProgressChanged += (_, update) => progress.Add(update);

        var report = await service.ImportAsync(sourcePath, databasePath);

        Assert.Equal("Directory", report.InputIdentity.SourceKind.ToString());
        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(1, report.RecordsParsed.Messages);
        Assert.Equal(1, report.RecordsParsed.Conversations);
        Assert.Equal(
            [ImportProgressStage.Scanning, ImportProgressStage.ProcessingMessages, ImportProgressStage.Finalizing, ImportProgressStage.Completed],
            progress.Select(update => update.Stage));
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void ImportCommandRefusesAnOutputDirectoryInsideTheExtractedSource()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "Takeout");
        Directory.CreateDirectory(Path.Combine(sourcePath, "Voice", "Calls"));
        File.WriteAllText(
            Path.Combine(sourcePath, "Voice", "Calls", "Alex - Text - 2024-01-02T03_04_05Z.html"),
            "<html><body><div class=\"message\"><abbr class=\"dt\" title=\"2024-01-02T03:04:05+00:00\"></abbr><a class=\"tel\" href=\"tel:+15551234567\">Alex</a><q>Source stays untouched</q></div></body></html>");
        var outputPath = Path.Combine(sourcePath, "VoiceBridgeOutput");

        var result = CliTestHost.Run("import", sourcePath, "--output", outputPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("outside the extracted source directory", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outputPath));
        Assert.Single(Directory.GetFiles(Path.Combine(sourcePath, "Voice", "Calls")));
    }

    [Fact]
    public async Task ImportDoesNotMatchVoiceAttachmentToUnrelatedTakeoutMedia()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var outputPath = Path.Combine(temporary.RootPath, "result");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Takeout/Voice/Calls/Alex - Text - 2024-01-02T03_04_05Z.html", """
                <html><body><div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Unrelated media test</q><img src="photo.jpg"></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Contacts/photo.jpg", "unrelated image bytes");
        }

        var result = CliTestHost.Run("import", archivePath, "--output", outputPath);

        Assert.Equal(0, result.ExitCode);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(outputPath, "voicebridge.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        Assert.Equal(0, await ReadCountAsync(connection, "SELECT COUNT(*) FROM attachments WHERE matched_source_file_id IS NOT NULL;"));
        Assert.Equal(1, await ReadCountAsync(connection, "SELECT COUNT(*) FROM import_issues WHERE code = 'attachment_reference_unresolved';"));
        await connection.CloseAsync();
    }

    [Fact]
    public async Task ImportServiceRefusesOutputInsideExtractedSource()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "Takeout");
        Directory.CreateDirectory(Path.Combine(sourcePath, "Voice", "Calls"));
        var databasePath = Path.Combine(sourcePath, "VoiceBridgeOutput", "voicebridge.db");
        var service = new TakeoutImportService(new SqliteTakeoutImportStoreFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync(sourcePath, databasePath));

        Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
    }

    [Fact]
    public async Task CancelledImportRemovesOnlyItsUncommittedDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "Takeout");
        var callsPath = Path.Combine(sourcePath, "Voice", "Calls");
        Directory.CreateDirectory(callsPath);
        File.WriteAllText(Path.Combine(callsPath, "Alex - Text - 2024-01-02T03_04_05Z.html"), """
            <html><body><div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Cancellation test</q></div></body></html>
            """);
        var databasePath = Path.Combine(temporary.RootPath, "result", "voicebridge.db");
        using var cancellation = new CancellationTokenSource();
        var service = new TakeoutImportService(new SqliteTakeoutImportStoreFactory());
        service.ProgressChanged += (_, progress) =>
        {
            if (progress.Stage == ImportProgressStage.ProcessingMessages)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ImportAsync(sourcePath, databasePath, cancellation.Token));

        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public void ImportCommandRejectsOutputReachedThroughDirectoryLink()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.RootPath, "Takeout");
        var callsPath = Path.Combine(sourcePath, "Voice", "Calls");
        Directory.CreateDirectory(callsPath);
        File.WriteAllText(
            Path.Combine(callsPath, "Alex - Text - 2024-01-02T03_04_05Z.html"),
            "<html><body><div class=\"message\"><abbr class=\"dt\" title=\"2024-01-02T03:04:05+00:00\"></abbr><a class=\"tel\" href=\"tel:+15551234567\">Alex</a><q>Link guard test</q></div></body></html>");
        var sourceAlias = Path.Combine(temporary.RootPath, "source-link");
        try
        {
            Directory.CreateSymbolicLink(sourceAlias, sourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some Windows test hosts do not grant symbolic-link creation; the direct containment case remains covered above.
            return;
        }

        var outputPath = Path.Combine(sourceAlias, "VoiceBridgeOutput");

        var result = CliTestHost.Run("import", sourcePath, "--output", outputPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("outside the extracted source directory", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(sourcePath, "VoiceBridgeOutput")));
    }

    private static void CreateArchive(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "Takeout/Voice/Calls/Alex - Text - 2024-01-02T03_04_05Z.html", """
            <html><body>
              <div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>First body</q><img src="../media/photo.jpg"></div>
              <div class="message"><abbr class="dt" title="2024-01-02T03:05:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Second body</q></div>
            </body></html>
            """);
        WriteEntry(archive, "Takeout/Voice/Calls/Caller - Received - 2024-01-02T03_04_05Z.html", "<html><body><abbr class=\"published\"></abbr></body></html>");
        WriteEntry(archive, "Takeout/Voice/media/photo.jpg", "fixture image bytes");
        WriteEntry(archive, "Takeout/archive_browser.html", "<html>Unrelated Takeout index</html>");
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static async Task<long> ReadCountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
