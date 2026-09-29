using VoiceBridge.Core.Parsing;

namespace VoiceBridge.Tests;

public sealed class MessagePageParserTests
{
    [Fact]
    public void ParsesObservedMessageStructureWithoutDependingOnFilenameTokens()
    {
        var parser = new VoiceMessageParser();
        var relativePath = "Takeout/Voice/Calls/not-a-text-file.html";

        var result = parser.Parse(ReadFixture("text-message.html"), relativePath);

        Assert.True(result.IsSupportedMessagePage);
        Assert.Empty(result.Issues);
        var message = Assert.Single(result.Messages);
        Assert.Equal(relativePath, message.SourceRelativePath);
        Assert.Equal(0, message.SourceRowIndex);
        Assert.Equal("2024-01-02T03:04:05-05:00", message.RawTimestamp);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)), message.Timestamp);
        Assert.Equal("Hello & welcome\nSecond line friend", message.Body);
        Assert.Equal("Me", message.Sender?.RawDisplayName);
        Assert.Equal("+15551234567", message.Sender?.RawPhoneNumber);
        Assert.Null(message.Direction);
        Assert.NotNull(message.AttachmentReferences);
        Assert.Collection(
            message.AttachmentReferences!,
            image =>
            {
                Assert.Equal("../images/photo&item.jpg", image.RawReference);
                Assert.Equal("image", image.MediaType);
                Assert.Null(image.MatchedRelativePath);
            },
            audio =>
            {
                Assert.Equal("../audio/voice-note.mp3", audio.RawReference);
                Assert.Equal("audio", audio.MediaType);
                Assert.Null(audio.MatchedRelativePath);
            });
    }

    [Fact]
    public void ParsesRowsFromGroupPageAndKeepsEachRowSenderSeparate()
    {
        var parser = new VoiceMessageParser();
        var result = parser.Parse(ReadFixture("group-message.html"), "Voice/Calls/Group Conversation - sample.html");

        Assert.True(result.IsSupportedMessagePage);
        Assert.Empty(result.Issues);
        Assert.Collection(
            result.PageParticipantEvidence,
            first =>
            {
                Assert.Equal("Alex", first.RawDisplayName);
                Assert.Equal("+15550000001", first.RawPhoneNumber);
            },
            second =>
            {
                Assert.Equal("Jordan", second.RawDisplayName);
                Assert.Equal("+15550000002", second.RawPhoneNumber);
            });
        Assert.Collection(
            result.Messages,
            first =>
            {
                Assert.Equal(0, first.SourceRowIndex);
                Assert.Equal("Alex", first.Sender?.RawDisplayName);
                Assert.Equal("+15550000001", first.Sender?.RawPhoneNumber);
                Assert.Equal("First group note", first.Body);
            },
            second =>
            {
                Assert.Equal(1, second.SourceRowIndex);
                Assert.Equal("Jordan", second.Sender?.RawDisplayName);
                Assert.Equal("+15550000002", second.Sender?.RawPhoneNumber);
                Assert.Equal("Second group note", second.Body);
            });
    }

    [Fact]
    public void MalformedRowsProduceIssuesAndRemainAvailableAsPartialMessages()
    {
        var parser = new VoiceMessageParser();
        const string relativePath = "Voice/Calls/malformed.html";

        var result = parser.Parse(ReadFixture("malformed-message-rows.html"), relativePath);

        Assert.True(result.IsSupportedMessagePage);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("not-a-timestamp", result.Messages[0].RawTimestamp);
        Assert.Null(result.Messages[0].Timestamp);
        Assert.Null(result.Messages[0].Body);
        Assert.Equal("Kept body", result.Messages[1].Body);
        Assert.Null(result.Messages[1].RawTimestamp);
        Assert.Null(result.Messages[1].Sender);
        Assert.All(result.Issues, issue => Assert.Equal(relativePath, issue.SourceRelativePath));
        Assert.Contains(result.Issues, issue => issue.Code == "message_timestamp_invalid" && issue.SourceRowIndex == 0);
        Assert.Contains(result.Issues, issue => issue.Code == "message_body_missing" && issue.SourceRowIndex == 0);
        Assert.Contains(result.Issues, issue => issue.Code == "message_timestamp_missing" && issue.SourceRowIndex == 1);
        Assert.Contains(result.Issues, issue => issue.Code == "message_sender_missing" && issue.SourceRowIndex == 1);
    }

    [Fact]
    public void EventLikeHtmlWithoutMessageRowsIsReportedAsUnsupported()
    {
        var parser = new VoiceMessageParser();
        const string relativePath = "Voice/Calls/Contact - Text - sample.html";

        var result = parser.Parse(ReadFixture("call-event-page.html"), relativePath);

        Assert.False(result.IsSupportedMessagePage);
        Assert.Empty(result.Messages);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("unsupported_message_page", issue.Code);
        Assert.Equal(relativePath, issue.SourceRelativePath);
        Assert.Null(issue.SourceRowIndex);
    }

    [Fact]
    public void EmptyTelUriLeavesSenderPhoneUnknownAndReportsIssue()
    {
        var parser = new VoiceMessageParser();

        var result = parser.Parse(ReadFixture("empty-sender-phone.html"), "Voice/Calls/contact.html");

        var message = Assert.Single(result.Messages);
        Assert.Equal("Me", message.Sender?.RawDisplayName);
        Assert.Null(message.Sender?.RawPhoneNumber);
        Assert.Contains(result.Issues, issue => issue.Code == "message_sender_phone_missing");
    }

    [Fact]
    public void RetainsObservedVideoAnchorReferenceWithoutInferringMediaType()
    {
        var parser = new VoiceMessageParser();

        var result = parser.Parse(ReadFixture("video-link-message.html"), "Voice/Calls/video-message.html");

        Assert.Empty(result.Issues);
        var message = Assert.Single(result.Messages);
        var attachment = Assert.Single(message.AttachmentReferences!);
        Assert.Equal("video-reference", attachment.RawReference);
        Assert.Null(attachment.MediaType);
        Assert.Null(attachment.MatchedRelativePath);
    }

    [Fact]
    public void ParseResultKeepsSourcePathAndStableContentFingerprint()
    {
        var parser = new VoiceMessageParser();
        var html = ReadFixture("text-message.html");

        var first = parser.Parse(html, "Voice/Calls/first.html");
        var duplicate = parser.Parse(html, "Voice/Calls/copy.html");
        var changed = parser.Parse(html + " ", "Voice/Calls/changed.html");

        Assert.Equal("Voice/Calls/first.html", first.SourceRelativePath);
        Assert.Matches("^[A-F0-9]{64}$", first.SourceContentSha256);
        Assert.Equal(first.SourceContentSha256, duplicate.SourceContentSha256);
        Assert.NotEqual(first.SourceContentSha256, changed.SourceContentSha256);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Voice", name));
}
