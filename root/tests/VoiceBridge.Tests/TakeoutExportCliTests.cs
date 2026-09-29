using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VoiceBridge.Tests;

[Collection("CLI commands")]
public sealed class TakeoutExportCliTests
{
    [Fact]
    public async Task HtmlExportCreatesOfflineConversationPagesSearchAndMatchedAssets()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "archive");
        CreateArchive(archivePath);
        var import = CliTestHost.Run("import", archivePath, "--output", importOutput);
        Assert.Equal(0, import.ExitCode);
        var databasePath = Path.Combine(importOutput, "voicebridge.db");
        var conversationId = await ReadSingleLongAsync(databasePath, "SELECT id FROM conversations;");

        var export = CliTestHost.Run("export", "html", databasePath, "--output", exportOutput);

        Assert.Equal(0, export.ExitCode);
        Assert.Contains("HTML archive export complete", export.StandardOutput, StringComparison.OrdinalIgnoreCase);
        var indexPath = Path.Combine(exportOutput, "index.html");
        var searchPath = Path.Combine(exportOutput, "search.html");
        var conversationPath = Path.Combine(exportOutput, "conversations", $"{conversationId}.html");
        Assert.True(File.Exists(indexPath));
        Assert.True(File.Exists(searchPath));
        Assert.True(File.Exists(conversationPath));

        var index = File.ReadAllText(indexPath);
        var conversation = File.ReadAllText(conversationPath);
        var search = File.ReadAllText(searchPath);
        Assert.Contains($"conversations/{conversationId}.html", index, StringComparison.Ordinal);
        Assert.Contains("Say &quot;hi&quot;, &lt;world&gt; &amp; everyone", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("<world>", conversation, StringComparison.Ordinal);
        Assert.Contains("search-index", search, StringComparison.Ordinal);
        Assert.Contains("Say", search, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", conversation, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(exportOutput, "assets")));
        var copiedMedia = Directory.GetFiles(Path.Combine(exportOutput, "assets"), "sourcefile-*");
        Assert.Single(copiedMedia);
        Assert.Equal(Encoding.UTF8.GetBytes("fixture image bytes"), File.ReadAllBytes(copiedMedia[0]));
    }

    [Fact]
    public async Task CsvExportPreservesDatabaseIdentifiersAndRelatedRecords()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "csv");
        CreateArchive(archivePath);
        Assert.Equal(0, CliTestHost.Run("import", archivePath, "--output", importOutput).ExitCode);
        var databasePath = Path.Combine(importOutput, "voicebridge.db");
        var conversationId = await ReadSingleLongAsync(databasePath, "SELECT id FROM conversations;");
        var messageId = await ReadSingleLongAsync(databasePath, "SELECT id FROM messages ORDER BY id LIMIT 1;");

        var export = CliTestHost.Run("export", "csv", databasePath, "--output", exportOutput);

        Assert.Equal(0, export.ExitCode);
        Assert.Contains("CSV export complete", export.StandardOutput, StringComparison.OrdinalIgnoreCase);
        var messages = File.ReadAllText(Path.Combine(exportOutput, "messages.csv"));
        var conversations = File.ReadAllText(Path.Combine(exportOutput, "conversations.csv"));
        var attachments = File.ReadAllText(Path.Combine(exportOutput, "attachments.csv"));
        Assert.Contains("message_id,conversation_id", messages, StringComparison.Ordinal);
        Assert.Contains($"{messageId},{conversationId},", messages, StringComparison.Ordinal);
        Assert.Contains("Say \"\"hi\"\", <world> & everyone", messages, StringComparison.Ordinal);
        Assert.Contains($"{conversationId},", conversations, StringComparison.Ordinal);
        Assert.Contains($"{messageId},", attachments, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(exportOutput, "participant_evidence.csv")));
        Assert.True(File.Exists(Path.Combine(exportOutput, "import_issues.csv")));
        Assert.True(File.Exists(Path.Combine(exportOutput, "source_files.csv")));
    }

    [Fact]
    public async Task HtmlAndCsvExportsExposeCallsVoicemailsTranscriptsAndMatchedAudio()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var htmlOutput = Path.Combine(temporary.RootPath, "archive");
        var csvOutput = Path.Combine(temporary.RootPath, "csv");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Takeout/Voice/Calls/call.html", """
                <html><body><div class="haudio"><span class="fn">Missed call from <script>unsafe</script></span><a class="tel" href="tel:+15551234567">Caller &amp; contact</a><abbr class="published" title="2024-01-02T03:04:05-05:00"></abbr><audio src="call-recording.mp3"></audio></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/voicemail.html", """
                <html><body><div class="haudio"><span class="fn">Voicemail from Caller</span><a class="tel" href="tel:+15550001111">Caller</a><abbr class="published" title="2024-01-03T04:05:06-05:00"></abbr><span class="full-text">Hello &lt;script&gt;do not run&lt;/script&gt;</span><audio src="voicemail.mp3"></audio></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/call-recording.mp3", "call audio");
            WriteEntry(archive, "Takeout/Voice/Calls/voicemail.mp3", "voicemail audio");
        }

        Assert.Equal(0, CliTestHost.Run("import", archivePath, "--output", importOutput).ExitCode);
        var databasePath = Path.Combine(importOutput, "voicebridge.db");
        Assert.Equal(0, CliTestHost.Run("export", "html", databasePath, "--output", htmlOutput).ExitCode);
        Assert.Equal(0, CliTestHost.Run("export", "csv", databasePath, "--output", csvOutput).ExitCode);

        var index = File.ReadAllText(Path.Combine(htmlOutput, "index.html"));
        var calls = File.ReadAllText(Path.Combine(htmlOutput, "calls.html"));
        var voicemails = File.ReadAllText(Path.Combine(htmlOutput, "voicemails.html"));
        var search = File.ReadAllText(Path.Combine(htmlOutput, "search.html"));
        Assert.Contains("calls.html", index, StringComparison.Ordinal);
        Assert.Contains("voicemails.html", index, StringComparison.Ordinal);
        Assert.Contains("Caller &amp; contact", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>unsafe</script>", calls, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;do not run&lt;/script&gt;", voicemails, StringComparison.Ordinal);
        Assert.Contains("<audio controls", voicemails, StringComparison.Ordinal);
        Assert.Matches("assets/sourcefile-[0-9]+\\.mp3", voicemails);
        Assert.Contains("Hello", search, StringComparison.Ordinal);
        Assert.Contains("voicemails.html#voicemail-", search, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", voicemails, StringComparison.OrdinalIgnoreCase);

        var callsCsv = File.ReadAllText(Path.Combine(csvOutput, "calls.csv"));
        var callMediaCsv = File.ReadAllText(Path.Combine(csvOutput, "call_media_references.csv"));
        var voicemailsCsv = File.ReadAllText(Path.Combine(csvOutput, "voicemails.csv"));
        var voicemailMediaCsv = File.ReadAllText(Path.Combine(csvOutput, "voicemail_media_references.csv"));
        Assert.Contains("call_record_id,source_file_id", callsCsv, StringComparison.Ordinal);
        Assert.Contains("recording.mp3", callMediaCsv, StringComparison.Ordinal);
        Assert.Contains("voicemail_id,source_file_id", voicemailsCsv, StringComparison.Ordinal);
        Assert.Contains("voicemail.mp3", voicemailMediaCsv, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRefusesToReplaceAnExistingDestination()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "archive");
        CreateArchive(archivePath);
        Assert.Equal(0, CliTestHost.Run("import", archivePath, "--output", importOutput).ExitCode);
        Directory.CreateDirectory(exportOutput);
        var sentinel = Path.Combine(exportOutput, "existing.txt");
        File.WriteAllText(sentinel, "preserve existing output");

        var export = CliTestHost.Run("export", "html", Path.Combine(importOutput, "voicebridge.db"), "--output", exportOutput);

        Assert.NotEqual(0, export.ExitCode);
        Assert.Contains("already exists", export.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve existing output", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(exportOutput));
    }

    [Fact]
    public void HtmlExportDoesNotCopyMediaFromAChangedSourceArchive()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "archive");
        CreateArchive(archivePath);
        Assert.Equal(0, CliTestHost.Run("import", archivePath, "--output", importOutput).ExitCode);

        ReplaceMedia(archivePath, "replacement media bytes");
        var export = CliTestHost.Run("export", "html", Path.Combine(importOutput, "voicebridge.db"), "--output", exportOutput);

        Assert.Equal(0, export.ExitCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(exportOutput, "assets"), "sourcefile-*"));
        var conversation = File.ReadAllText(Path.Combine(exportOutput, "conversations", "1.html"));
        Assert.Contains("Matched media unavailable", conversation, StringComparison.Ordinal);
        var report = File.ReadAllText(Path.Combine(exportOutput, "export-report.json"));
        Assert.Contains("sourceArchiveStatus", report, StringComparison.Ordinal);
        Assert.Contains("hash_mismatch", report, StringComparison.Ordinal);
    }

    [Fact]
    public void CsvExportEscapesFormulaLikeCellsForSpreadsheetSafety()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "takeout.zip");
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "csv");
        CreateArchive(archivePath, messageBodyHtml: "=1+1");
        Assert.Equal(0, CliTestHost.Run("import", archivePath, "--output", importOutput).ExitCode);

        var export = CliTestHost.Run("export", "csv", Path.Combine(importOutput, "voicebridge.db"), "--output", exportOutput);

        Assert.Equal(0, export.ExitCode);
        var messages = File.ReadAllText(Path.Combine(exportOutput, "messages.csv"));
        Assert.Contains(",'=1+1", messages, StringComparison.Ordinal);
        var report = File.ReadAllText(Path.Combine(exportOutput, "export-report.json"));
        Assert.Contains("formulaEscapingPolicy", report, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlExportContinuesWhenOneMatchedDirectoryMediaFileCannotBeRead()
    {
        using var temporary = new TemporaryDirectory();
        var sourceRoot = Path.Combine(temporary.RootPath, "Takeout");
        var calls = Path.Combine(sourceRoot, "Voice", "Calls");
        var media = Path.Combine(sourceRoot, "Voice", "media");
        Directory.CreateDirectory(calls);
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(calls, "Alex - Text - 2024-01-02T03_04_05Z.html"), """
            <html><body><div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Keep the message</q><img src="../media/photo.jpg"></div></body></html>
            """);
        var mediaPath = Path.Combine(media, "photo.jpg");
        File.WriteAllBytes(mediaPath, Encoding.UTF8.GetBytes("fixture image bytes"));
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "archive");
        Assert.Equal(0, CliTestHost.Run("import", sourceRoot, "--output", importOutput).ExitCode);

        using var lockedMedia = new FileStream(mediaPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var export = CliTestHost.Run("export", "html", Path.Combine(importOutput, "voicebridge.db"), "--output", exportOutput);

        Assert.Equal(0, export.ExitCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(exportOutput, "assets"), "sourcefile-*"));
        var conversation = File.ReadAllText(Path.Combine(exportOutput, "conversations", "1.html"));
        Assert.Contains("Keep the message", conversation, StringComparison.Ordinal);
        Assert.Contains("Matched media unavailable", conversation, StringComparison.Ordinal);
        var report = File.ReadAllText(Path.Combine(exportOutput, "export-report.json"));
        Assert.Contains("unavailableMediaCount\": 1", report, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlExportDoesNotCopyChangedDirectoryMedia()
    {
        using var temporary = new TemporaryDirectory();
        var sourceRoot = Path.Combine(temporary.RootPath, "Takeout");
        var calls = Path.Combine(sourceRoot, "Voice", "Calls");
        var mediaDirectory = Path.Combine(sourceRoot, "Voice", "media");
        Directory.CreateDirectory(calls);
        Directory.CreateDirectory(mediaDirectory);
        File.WriteAllText(Path.Combine(calls, "Alex - Text - 2024-01-02T03_04_05Z.html"), """
            <html><body><div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Keep the imported media identity</q><img src="../media/photo.jpg"></div></body></html>
            """);
        var mediaPath = Path.Combine(mediaDirectory, "photo.jpg");
        File.WriteAllBytes(mediaPath, Encoding.UTF8.GetBytes("fixture image bytes"));
        var importOutput = Path.Combine(temporary.RootPath, "import");
        var exportOutput = Path.Combine(temporary.RootPath, "archive");
        Assert.Equal(0, CliTestHost.Run("import", sourceRoot, "--output", importOutput).ExitCode);
        File.WriteAllBytes(mediaPath, Encoding.UTF8.GetBytes("changed image bytes"));

        var export = CliTestHost.Run("export", "html", Path.Combine(importOutput, "voicebridge.db"), "--output", exportOutput);

        Assert.Equal(0, export.ExitCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(exportOutput, "assets"), "sourcefile-*"));
        var conversation = File.ReadAllText(Path.Combine(exportOutput, "conversations", "1.html"));
        Assert.Contains("Keep the imported media identity", conversation, StringComparison.Ordinal);
        Assert.Contains("Matched media unavailable", conversation, StringComparison.Ordinal);
        var report = File.ReadAllText(Path.Combine(exportOutput, "export-report.json"));
        Assert.Contains("source_file_hash_mismatch", report, StringComparison.Ordinal);
    }

    private static void CreateArchive(
        string path,
        string messageBodyHtml = "Say &quot;hi&quot;, &lt;world&gt; &amp; everyone",
        string mediaContent = "fixture image bytes")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var messagePage = """
            <html><body>
              <div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>MESSAGE_BODY_PLACEHOLDER</q><img src="../media/photo.jpg"></div>
            </body></html>
            """.Replace("MESSAGE_BODY_PLACEHOLDER", messageBodyHtml, StringComparison.Ordinal);
        WriteEntry(archive, "Takeout/Voice/Calls/Alex - Text - 2024-01-02T03_04_05Z.html", messagePage);
        WriteEntry(archive, "Takeout/Voice/media/photo.jpg", mediaContent);
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void ReplaceMedia(string archivePath, string content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry("Takeout/Voice/media/photo.jpg")!.Delete();
        WriteEntry(archive, "Takeout/Voice/media/photo.jpg", content);
    }

    private static async Task<long> ReadSingleLongAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
