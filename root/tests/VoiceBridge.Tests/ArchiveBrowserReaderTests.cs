using System.IO.Compression;
using System.Text;
using VoiceBridge.Core.Importing;
using VoiceBridge.Storage;

namespace VoiceBridge.Tests;

[Collection("CLI commands")]
public sealed class ArchiveBrowserReaderTests
{
    [Fact]
    public async Task ConversationPagesSearchAndPreserveMessageProvenanceAndAttachments()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = await ImportFixtureAsync(temporary.RootPath);
        using var reader = new SqliteArchiveReader(databasePath);

        var searchPage = reader.ReadConversationPage("second body", "All", 0, 10);
        var filteredPage = reader.ReadConversationPage(null, "With attachments", 0, 10);
        var groupPage = reader.ReadConversationPage(null, "Groups", 0, 10);

        var conversation = Assert.Single(searchPage.Items);
        Assert.Equal(1, searchPage.TotalCount);
        Assert.Equal(2, conversation.MessageCount);
        Assert.Contains("Alex", conversation.ParticipantSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(filteredPage.Items);
        Assert.Empty(groupPage.Items);

        var firstPage = reader.ReadConversationMessagesPage(conversation.Id, 0, 1);
        var secondPage = reader.ReadConversationMessagesPage(conversation.Id, 1, 1);
        Assert.Single(firstPage.Items);
        Assert.Equal(2, secondPage.TotalCount);
        Assert.Equal("Takeout/Voice/Calls/Alex - Text - 2024-01-02T03_04_05Z.html", firstPage.Items[0].SourceRelativePath);
        Assert.Single(firstPage.Items[0].Attachments);
        Assert.Equal("../media/photo.jpg", firstPage.Items[0].Attachments[0].RawReference);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadConversationPage(null, "All", 0, 201));
    }

    [Fact]
    public async Task EventMediaAndIssuePagesSearchWithinBoundedPagesAndKeepExactEvidence()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = await ImportFixtureAsync(temporary.RootPath);
        using var reader = new SqliteArchiveReader(databasePath);

        var calls = reader.ReadCallPage("Caller One", "Received", 0, 10);
        var voicemails = reader.ReadVoicemailPage("distinct transcript phrase", 0, 10);
        var media = reader.ReadMediaPage("missing-vm.mp3", "Unresolved", 0, 10);
        var issues = reader.ReadImportIssuePage("unsupported_voice_event_page", null, 0, 1);

        var call = Assert.Single(calls.Items);
        Assert.Equal("Received", call.RawEventType);
        Assert.Equal("Takeout/Voice/Calls/Caller One - Received - 2024-01-02T03_04_05Z.html", call.SourceRelativePath);
        Assert.Single(call.MediaReferences);
        var voicemail = Assert.Single(voicemails.Items);
        Assert.Contains("distinct transcript phrase", voicemail.Transcript, StringComparison.Ordinal);
        Assert.Equal("unresolved", voicemail.AudioMatchStatus);
        Assert.Equal("missing-vm.mp3", voicemail.AudioReference);
        Assert.Equal("missing-vm.mp3", Assert.Single(voicemail.MediaReferences).RawReference);
        var mediaReference = Assert.Single(media.Items);
        Assert.Equal("voicemail_media", mediaReference.RecordType);
        Assert.Equal("missing-vm.mp3", mediaReference.RawReference);
        Assert.Equal(1, media.TotalCount);
        var issue = Assert.Single(issues.Items);
        Assert.Equal("unsupported_voice_event_page", issue.Code);
        Assert.Equal("The Voice HTML page does not contain one of the observed call or voicemail event labels.", issue.Message);
        Assert.NotNull(issue.SourceRelativePath);
        Assert.Equal(1, issues.TotalCount);
    }

    [Fact]
    public async Task MediaSummarySeparatesReferencesFromSourceMediaFiles()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = await ImportFixtureAsync(temporary.RootPath);
        using var reader = new SqliteArchiveReader(databasePath);

        var summary = reader.ReadMediaBrowserSummary();
        var page = reader.ReadMediaPage(null, "All", 0, 20);

        Assert.Equal(3, summary.ReferenceCount);
        Assert.Equal(2, summary.SourceMediaFileCount);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(summary.ReferenceCount + summary.SourceMediaFileCount, page.TotalCount);
    }

    [Fact]
    public async Task RecordBrowsersSortNewestAndOldestByRecordedTimestamp()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = await ImportFixtureAsync(temporary.RootPath);
        using var reader = new SqliteArchiveReader(databasePath);

        var newestMessages = reader.ReadConversationPage(null, "All", 0, 10, newestFirst: true);
        var oldestMessages = reader.ReadConversationPage(null, "All", 0, 10, newestFirst: false);
        var newestCalls = reader.ReadCallPage(null, "All", 0, 10, newestFirst: true);
        var oldestCalls = reader.ReadCallPage(null, "All", 0, 10, newestFirst: false);
        var newestVoicemails = reader.ReadVoicemailPage(null, 0, 10, newestFirst: true);
        var oldestVoicemails = reader.ReadVoicemailPage(null, 0, 10, newestFirst: false);
        var newestMedia = reader.ReadMediaPage(null, "All", 0, 20, newestFirst: true);
        var oldestMedia = reader.ReadMediaPage(null, "All", 0, 20, newestFirst: false);

        Assert.Contains("Newest Message - Text", newestMessages.Items[0].SourceRelativePath, StringComparison.Ordinal);
        Assert.Contains("Oldest Message - Text", oldestMessages.Items[0].SourceRelativePath, StringComparison.Ordinal);
        Assert.Contains("Newest Caller - Received", newestCalls.Items[0].SourceRelativePath, StringComparison.Ordinal);
        Assert.Contains("Oldest Caller - Received", oldestCalls.Items[0].SourceRelativePath, StringComparison.Ordinal);
        Assert.Contains("Newest Caller - Voicemail", newestVoicemails.Items[0].SourceRelativePath, StringComparison.Ordinal);
        Assert.Contains("Oldest Caller - Voicemail", oldestVoicemails.Items[0].SourceRelativePath, StringComparison.Ordinal);
        Assert.Equal("voicemail_media", newestMedia.Items[0].RecordType);
        Assert.Equal("message_attachment", oldestMedia.Items[0].RecordType);
        Assert.Equal("source_file", newestMedia.Items[^1].RecordType);
        Assert.Equal("source_file", oldestMedia.Items[^1].RecordType);
    }

    private static async Task<string> ImportFixtureAsync(string root)
    {
        var zipPath = Path.Combine(root, "takeout.zip");
        var output = Path.Combine(root, "import");
        Directory.CreateDirectory(output);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "Takeout/Voice/Calls/Alex - Text - 2024-01-02T03_04_05Z.html", """
                <html><body>
                  <div class="message"><abbr class="dt" title="2024-01-02T03:04:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>First body</q><img src="../media/photo.jpg"></div>
                  <div class="message"><abbr class="dt" title="2024-01-02T03:05:05+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Second body with search marker</q></div>
                </body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Oldest Message - Text - 2024-01-01T00_00_00Z.html", """
                <html><body><div class="message"><abbr class="dt" title="2024-01-01T00:00:00Z"></abbr><a class="tel" href="tel:+15551234560">Oldest Sender</a><q>Oldest message</q></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Newest Message - Text - 2024-01-04T00_00_00Z.html", """
                <html><body><div class="message"><abbr class="dt" title="2024-01-04T00:00:00Z"></abbr><a class="tel" href="tel:+15551234561">Newest Sender</a><q>Newest message</q></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Caller One - Received - 2024-01-02T03_04_05Z.html", """
                <html><body><div class="haudio"><span class="fn">Received call from Caller One</span><a class="tel" href="tel:+15550001111">Caller One</a><abbr class="published" title="2024-01-02T03:04:05-05:00"></abbr><audio src="call-recording.mp3"></audio></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Oldest Caller - Received - 2024-01-01T00_00_00Z.html", """
                <html><body><div class="haudio"><span class="fn">Received call from Oldest Caller</span><a class="tel" href="tel:+15550001112">Oldest Caller</a><abbr class="published" title="2024-01-01T00:00:00Z"></abbr></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Newest Caller - Received - 2024-01-04T00_00_00Z.html", """
                <html><body><div class="haudio"><span class="fn">Received call from Newest Caller</span><a class="tel" href="tel:+15550001113">Newest Caller</a><abbr class="published" title="2024-01-04T00:00:00Z"></abbr></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Caller Two - Voicemail - 2024-01-03T03_04_05Z.html", """
                <html><body><div class="haudio"><span class="fn">Voicemail from Caller Two</span><a class="tel" href="tel:+15550002222">Caller Two</a><abbr class="published" title="2024-01-03T03:04:05-05:00"></abbr><span class="full-text">distinct transcript phrase</span><audio src="missing-vm.mp3"></audio></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Oldest Caller - Voicemail - 2024-01-01T00_00_00Z.html", """
                <html><body><div class="haudio"><span class="fn">Voicemail from Oldest Caller</span><a class="tel" href="tel:+15550002223">Oldest Caller</a><abbr class="published" title="2024-01-01T00:00:00Z"></abbr><span class="full-text">Oldest voicemail</span></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/Newest Caller - Voicemail - 2024-01-05T00_00_00Z.html", """
                <html><body><div class="haudio"><span class="fn">Voicemail from Newest Caller</span><a class="tel" href="tel:+15550002224">Newest Caller</a><abbr class="published" title="2024-01-05T00:00:00Z"></abbr><span class="full-text">Newest voicemail</span></div></body></html>
                """);
            WriteEntry(archive, "Takeout/Voice/Calls/unsupported.html", "<html><body><abbr class=\"published\"></abbr></body></html>");
            WriteEntry(archive, "Takeout/Voice/media/photo.jpg", "image");
            WriteEntry(archive, "Takeout/Voice/Calls/call-recording.mp3", "audio");
        }

        await new TakeoutImportService(new SqliteTakeoutImportStoreFactory())
            .ImportAsync(zipPath, Path.Combine(output, "voicebridge.db"));
        return Path.Combine(output, "voicebridge.db");
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
