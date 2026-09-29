using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Parsing;
using VoiceBridge.Core.Reconstruction;

namespace VoiceBridge.Tests;

public sealed class ConversationReconstructorTests
{
    [Fact]
    public void GroupsTextRowsBySourcePageNormalizesSamePhoneAndSortsWithoutDroppingRepeatedBodies()
    {
        const string path = "Takeout/Voice/Calls/Alex - Text - 2024-02-01T10_05_00Z.html";
        const string html = """
            <html><body>
              <div class="message"><abbr class="dt" title="2024-02-01T10:05:00+00:00"></abbr><a class="tel" href="tel:+1-555-123-4567">Alex</a><q>Same body</q></div>
              <div class="message"><abbr class="dt" title="2024-02-01T10:00:00+00:00"></abbr><a class="tel" href="tel:+1 (555) 123-4567">Alex</a><q>Same body</q></div>
            </body></html>
            """;
        var parser = new VoiceMessageParser();
        var page = parser.Parse(html, path);
        var reconstructor = new ConversationReconstructor([]);

        var result = reconstructor.Reconstruct(page);

        Assert.Empty(result.Issues);
        var conversation = Assert.IsType<Conversation>(result.Conversation);
        Assert.Equal(ConversationKind.OneToOne, conversation.Kind);
        Assert.Equal(path, conversation.SourceRelativePath);
        Assert.Equal("Alex", conversation.RawLabel);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.All(conversation.Messages, message => Assert.Equal("Same body", message.Body));
        Assert.Equal(
            [new DateTimeOffset(2024, 2, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 2, 1, 10, 5, 0, TimeSpan.Zero)],
            conversation.Messages.Select(message => message.Timestamp).ToArray());
        var participant = Assert.Single(conversation.Participants);
        Assert.Equal("+15551234567", participant.NormalizedPhoneNumber);
        Assert.Equal(2, participant.Evidence.Count);
    }

    [Fact]
    public void ConfirmsGroupOnlyFromGroupFilenameAndDistinctExplicitPageParticipants()
    {
        var parser = new VoiceMessageParser();
        var page = parser.Parse(ReadFixture("group-message.html"), "Voice/Calls/Group Conversation - 2024-02-01T10_00_00Z.html");
        var reconstructor = new ConversationReconstructor([]);

        var result = reconstructor.Reconstruct(page);

        var conversation = Assert.IsType<Conversation>(result.Conversation);
        Assert.Equal(ConversationKind.Group, conversation.Kind);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(2, conversation.Participants.Count);
        Assert.All(conversation.Participants, participant => Assert.NotNull(participant.NormalizedPhoneNumber));
        Assert.All(conversation.Participants, participant => Assert.Contains(
            participant.Evidence,
            evidence => evidence.Source == ParticipantEvidenceSource.PageParticipant));
    }

    [Fact]
    public void KeepsDuplicateSourcePagesAndReportsExactContentCopies()
    {
        var parser = new VoiceMessageParser();
        var html = ReadFixture("text-message.html");
        var original = parser.Parse(html, "Voice/Calls/contact.html");
        var copy = parser.Parse(html, "Voice/Spam/contact-copy.html");
        var changed = parser.Parse(html.Replace("Hello", "Goodbye", StringComparison.Ordinal), "Voice/Calls/other.html");
        var duplicates = DuplicateSourcePageDetector.FindExactDuplicates([original, copy, changed]);
        var reconstructor = new ConversationReconstructor([]);

        var originalConversation = reconstructor.Reconstruct(original).Conversation;
        var copyConversation = reconstructor.Reconstruct(copy).Conversation;
        var changedConversation = reconstructor.Reconstruct(changed).Conversation;

        var duplicateGroup = Assert.Single(duplicates);
        Assert.Equal(2, duplicateGroup.SourceRelativePaths.Count);
        Assert.Contains("Voice/Calls/contact.html", duplicateGroup.SourceRelativePaths);
        Assert.Contains("Voice/Spam/contact-copy.html", duplicateGroup.SourceRelativePaths);
        Assert.NotNull(originalConversation);
        Assert.NotNull(copyConversation);
        Assert.NotNull(changedConversation);
        Assert.NotEqual(originalConversation.Messages[0].Body, changedConversation.Messages[0].Body);
    }

    [Fact]
    public void MatchesMultipleAttachmentReferencesConservativelyByUniqueBasename()
    {
        var parser = new VoiceMessageParser();
        var page = parser.Parse(ReadFixture("text-message.html"), "Takeout/Voice/Calls/contact.html");
        var mediaFiles = new[]
        {
            new SourceFile("Takeout/Voice/Calls/photo&item.jpg", 10, "image"),
            new SourceFile("Takeout/Voice/Calls/voice-note.mp3", 20, "audio")
        };
        var reconstructor = new ConversationReconstructor(mediaFiles);

        var result = reconstructor.Reconstruct(page);

        Assert.Empty(result.Issues);
        var conversation = Assert.IsType<Conversation>(result.Conversation);
        Assert.Collection(
            conversation.Messages[0].AttachmentReferences!,
            image =>
            {
                Assert.Equal("Takeout/Voice/Calls/photo&item.jpg", image.MatchedRelativePath);
                Assert.Equal("image", image.MediaType);
            },
            audio =>
            {
                Assert.Equal("Takeout/Voice/Calls/voice-note.mp3", audio.MatchedRelativePath);
                Assert.Equal("audio", audio.MediaType);
        });
    }

    [Fact]
    public void PreservesLiteralPercentSequencesInArchivePathsWhileDecodingReferences()
    {
        const string html = """
            <div class="message"><abbr class="dt" title="2024-02-01T10:00:00+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Photo</q><img src="literal%2520name.jpg"></div>
            """;
        const string mediaPath = "Takeout/Voice/Calls/literal%20name.jpg";
        var parser = new VoiceMessageParser();
        var page = parser.Parse(html, "Takeout/Voice/Calls/Alex - Text - 2024-02-01T10_00_00Z.html");
        var reconstructor = new ConversationReconstructor([new SourceFile(mediaPath, 10, "image")]);

        var result = reconstructor.Reconstruct(page);

        Assert.Empty(result.Issues);
        var conversation = Assert.IsType<Conversation>(result.Conversation);
        var attachment = Assert.Single(conversation.Messages[0].AttachmentReferences!);
        Assert.Equal("literal%2520name.jpg", attachment.RawReference);
        Assert.Equal(mediaPath, attachment.MatchedRelativePath);
    }

    [Fact]
    public void LeavesAmbiguousGroupMembershipUnknown()
    {
        const string html = """
            <div class="message"><abbr class="dt" title="2024-02-01T10:00:00+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Text</q></div>
            """;
        var parser = new VoiceMessageParser();
        var page = parser.Parse(html, "Voice/Calls/Group Conversation - 2024-02-01T10_00_00Z.html");
        var reconstructor = new ConversationReconstructor([]);

        var result = reconstructor.Reconstruct(page);

        var conversation = Assert.IsType<Conversation>(result.Conversation);
        Assert.Equal(ConversationKind.Unknown, conversation.Kind);
        Assert.Contains(result.Issues, issue => issue.Code == "conversation_membership_ambiguous");
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public void KeepsMessagesAndUnknownMembershipWhenParticipantDataIsMissing()
    {
        const string html = """
            <div class="message"><abbr class="dt" title="2024-02-01T10:00:00+00:00"></abbr><q>Text</q></div>
            """;
        var parser = new VoiceMessageParser();
        var page = parser.Parse(html, "Voice/Calls/Alex - Text - 2024-02-01T10_00_00Z.html");
        var reconstructor = new ConversationReconstructor([]);

        var result = reconstructor.Reconstruct(page);

        var conversation = Assert.IsType<Conversation>(result.Conversation);
        Assert.Equal(ConversationKind.Unknown, conversation.Kind);
        Assert.Empty(conversation.Participants);
        Assert.Single(conversation.Messages);
        Assert.Contains(result.Issues, issue => issue.Code == "message_sender_missing");
    }

    [Fact]
    public void DoesNotGuessWhenAnExtensionlessAttachmentStemMatchesMultipleFiles()
    {
        var parser = new VoiceMessageParser();
        var page = parser.Parse(ReadFixture("video-link-message.html"), "Takeout/Voice/Calls/video.html");
        var mediaFiles = new[]
        {
            new SourceFile("Takeout/Voice/Calls/media/video-reference.3gp", 10, "video"),
            new SourceFile("Takeout/Voice/Calls/archive/video-reference.mp4", 20, "video")
        };
        var reconstructor = new ConversationReconstructor(mediaFiles);

        var result = reconstructor.Reconstruct(page);

        var conversation = Assert.IsType<Conversation>(result.Conversation);
        var attachment = Assert.Single(conversation.Messages[0].AttachmentReferences!);
        Assert.Null(attachment.MatchedRelativePath);
        Assert.Null(attachment.MediaType);
        Assert.Contains(result.Issues, issue => issue.Code == "attachment_reference_ambiguous");
    }

    [Fact]
    public void DoesNotResolveRelativeAttachmentThatTraversesAboveArchiveRoot()
    {
        const string html = """
            <div class="message"><abbr class="dt" title="2024-02-01T10:00:00+00:00"></abbr><a class="tel" href="tel:+15551234567">Alex</a><q>Photo</q><img src="../../../../unrelated.jpg"></div>
            """;
        var parser = new VoiceMessageParser();
        var page = parser.Parse(html, "Takeout/Voice/Calls/Alex - Text - 2024-02-01T10_00_00Z.html");
        var reconstructor = new ConversationReconstructor([new SourceFile("unrelated.jpg", 10, "image")]);

        var result = reconstructor.Reconstruct(page);

        var conversation = Assert.IsType<Conversation>(result.Conversation);
        var attachment = Assert.Single(conversation.Messages[0].AttachmentReferences!);
        Assert.Equal("../../../../unrelated.jpg", attachment.RawReference);
        Assert.Null(attachment.MatchedRelativePath);
        Assert.Contains(result.Issues, issue => issue.Code == "attachment_reference_unresolved");
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Voice", name));
}
