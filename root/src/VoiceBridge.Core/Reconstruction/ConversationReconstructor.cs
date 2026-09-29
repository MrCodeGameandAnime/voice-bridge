using System.Globalization;
using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Parsing;

namespace VoiceBridge.Core.Reconstruction;

public sealed class ConversationReconstructor
{
    private readonly AttachmentReferenceMatcher _attachmentMatcher;

    public ConversationReconstructor(IEnumerable<SourceFile> sourceFiles)
    {
        _attachmentMatcher = new AttachmentReferenceMatcher(sourceFiles);
    }

    public ConversationReconstructionResult Reconstruct(MessagePageParseResult page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var issues = page.Issues.ToList();
        if (!page.IsSupportedMessagePage)
        {
            return new ConversationReconstructionResult(null, issues);
        }

        var messages = SortMessages(page.Messages)
            .Select(message => ResolveAttachments(message, page.SourceRelativePath, issues))
            .ToArray();
        var participants = BuildParticipants(page, messages);
        var filenameEvidence = ParseFilenameEvidence(page.SourceRelativePath);
        var kind = DetermineKind(filenameEvidence.Kind, page, participants);

        if (filenameEvidence.Kind == MessagePageFilenameKind.GroupConversation
            && kind != ConversationKind.Group)
        {
            issues.Add(new ImportIssue(
                "conversation_membership_ambiguous",
                "A group-style filename did not have two distinct explicit page participants; membership remains unknown.",
                page.SourceRelativePath));
        }

        var conversation = new Conversation(page.SourceRelativePath, filenameEvidence.RawLabel)
        {
            Kind = kind,
            Participants = participants,
            Messages = messages
        };

        return new ConversationReconstructionResult(conversation, issues);
    }

    private static IEnumerable<Message> SortMessages(IReadOnlyList<Message> messages) =>
        messages
            .Select((message, originalIndex) => new { Message = message, OriginalIndex = originalIndex })
            .OrderBy(item => item.Message.Timestamp is null)
            .ThenBy(item => item.Message.Timestamp?.UtcDateTime.Ticks ?? long.MaxValue)
            .ThenBy(item => item.Message.SourceRowIndex ?? item.OriginalIndex)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Message);

    private Message ResolveAttachments(Message message, string sourceRelativePath, List<ImportIssue> issues)
    {
        if (message.AttachmentReferences is null || message.AttachmentReferences.Count == 0)
        {
            return message;
        }

        var attachments = new Attachment[message.AttachmentReferences.Count];
        for (var index = 0; index < message.AttachmentReferences.Count; index++)
        {
            var (attachment, issue) = _attachmentMatcher.Match(
                message.AttachmentReferences[index],
                sourceRelativePath,
                message.SourceRowIndex);
            attachments[index] = attachment;
            if (issue is not null)
            {
                issues.Add(issue);
            }
        }

        return message with { AttachmentReferences = attachments };
    }

    private static IReadOnlyList<ConversationParticipant> BuildParticipants(
        MessagePageParseResult page,
        IReadOnlyList<Message> messages)
    {
        var participants = new List<ConversationParticipant>();
        var participantsByPhone = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var participant in page.PageParticipantEvidence)
        {
            AddParticipant(participant, ParticipantEvidenceSource.PageParticipant, null);
        }

        foreach (var message in messages)
        {
            if (message.Sender is not null)
            {
                AddParticipant(message.Sender, ParticipantEvidenceSource.MessageSender, message.SourceRowIndex);
            }
        }

        return participants;

        void AddParticipant(Participant participant, ParticipantEvidenceSource source, int? sourceRowIndex)
        {
            var normalizedPhone = ParticipantNormalizer.NormalizePhoneNumber(participant.RawPhoneNumber);
            var normalizedName = ParticipantNormalizer.NormalizeDisplayName(participant.RawDisplayName);
            var evidence = new ParticipantEvidence(participant, source, sourceRowIndex);

            if (normalizedPhone is null)
            {
                participants.Add(new ConversationParticipant(normalizedName, null, [evidence]));
                return;
            }

            if (participantsByPhone.TryGetValue(normalizedPhone, out var existingIndex))
            {
                var existing = participants[existingIndex];
                participants[existingIndex] = existing with
                {
                    NormalizedDisplayName = existing.NormalizedDisplayName ?? normalizedName,
                    Evidence = [.. existing.Evidence, evidence]
                };
                return;
            }

            participantsByPhone.Add(normalizedPhone, participants.Count);
            participants.Add(new ConversationParticipant(normalizedName, normalizedPhone, [evidence]));
        }
    }

    private static ConversationKind DetermineKind(
        MessagePageFilenameKind filenameKind,
        MessagePageParseResult page,
        IReadOnlyList<ConversationParticipant> participants)
    {
        var explicitPagePhoneCount = participants
            .Where(participant => participant.NormalizedPhoneNumber is not null
                && participant.Evidence.Any(evidence => evidence.Source == ParticipantEvidenceSource.PageParticipant))
            .Select(participant => participant.NormalizedPhoneNumber)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var senderPhoneCount = participants
            .Where(participant => participant.NormalizedPhoneNumber is not null
                && participant.Evidence.Any(evidence => evidence.Source == ParticipantEvidenceSource.MessageSender))
            .Select(participant => participant.NormalizedPhoneNumber)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (explicitPagePhoneCount >= 3 || senderPhoneCount >= 3)
        {
            return ConversationKind.Group;
        }

        if (filenameKind == MessagePageFilenameKind.GroupConversation && explicitPagePhoneCount >= 2)
        {
            return ConversationKind.Group;
        }

        if (filenameKind == MessagePageFilenameKind.Text
            && page.PageParticipantEvidence.Count == 0
            && senderPhoneCount is >= 1 and <= 2)
        {
            return ConversationKind.OneToOne;
        }

        return ConversationKind.Unknown;
    }

    private static MessagePageFilenameEvidence ParseFilenameEvidence(string sourceRelativePath)
    {
        var normalizedPath = sourceRelativePath.Replace('\\', '/');
        var separatorIndex = normalizedPath.LastIndexOf('/');
        var fileName = separatorIndex < 0 ? normalizedPath : normalizedPath[(separatorIndex + 1)..];
        var extensionIndex = fileName.LastIndexOf('.');
        var stem = extensionIndex < 0 ? fileName : fileName[..extensionIndex];

        const string groupMarker = " - ";
        var groupSeparator = stem.LastIndexOf(groupMarker, StringComparison.Ordinal);
        if (groupSeparator > 0
            && stem[..groupSeparator].Equals("Group Conversation", StringComparison.OrdinalIgnoreCase)
            && IsTakeoutFilenameTimestamp(stem[(groupSeparator + groupMarker.Length)..]))
        {
            return new MessagePageFilenameEvidence(stem[..groupSeparator], MessagePageFilenameKind.GroupConversation);
        }

        const string textMarker = " - Text - ";
        var textMarkerIndex = stem.LastIndexOf(textMarker, StringComparison.OrdinalIgnoreCase);
        if (textMarkerIndex >= 0 && IsTakeoutFilenameTimestamp(stem[(textMarkerIndex + textMarker.Length)..]))
        {
            return new MessagePageFilenameEvidence(stem[..textMarkerIndex], MessagePageFilenameKind.Text);
        }

        return new MessagePageFilenameEvidence(null, MessagePageFilenameKind.Unknown);
    }

    private static bool IsTakeoutFilenameTimestamp(string value)
    {
        return DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH_mm_ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
    }

    private readonly record struct MessagePageFilenameEvidence(string? RawLabel, MessagePageFilenameKind Kind);

    private enum MessagePageFilenameKind
    {
        Unknown,
        Text,
        GroupConversation
    }
}
