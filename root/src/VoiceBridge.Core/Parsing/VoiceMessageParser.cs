using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Parsing;

public sealed class VoiceMessageParser
{
    public MessagePageParseResult Parse(string html, string sourceRelativePath)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);

        var document = new HtmlParser().ParseDocument(html);
        var rows = document.QuerySelectorAll("div.message");
        if (rows.Length == 0)
        {
            return new MessagePageParseResult(
                false,
                [],
                [],
                [new ImportIssue(
                    "unsupported_message_page",
                    "The HTML page contains no observed div.message rows.",
                    sourceRelativePath)]);
        }

        var messages = new List<Message>(rows.Length);
        var pageParticipantEvidence = new List<Participant>();
        var issues = new List<ImportIssue>();

        foreach (var participantLink in document.QuerySelectorAll(".participants a.tel"))
        {
            var participant = ParsePageParticipant(participantLink);
            if (participant is not null)
            {
                pageParticipantEvidence.Add(participant);
            }
        }

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            var timestampNode = row.QuerySelector("abbr.dt");
            var rawTimestamp = timestampNode?.GetAttribute("title");
            DateTimeOffset? timestamp = null;

            if (string.IsNullOrWhiteSpace(rawTimestamp))
            {
                issues.Add(new ImportIssue(
                    "message_timestamp_missing",
                    "The message row has no abbr.dt title timestamp.",
                    sourceRelativePath,
                    rowIndex));
            }
            else if (TryParseTimestamp(rawTimestamp, out var parsedTimestamp))
            {
                timestamp = parsedTimestamp;
            }
            else
            {
                issues.Add(new ImportIssue(
                    "message_timestamp_invalid",
                    "The message row timestamp is not a supported offset timestamp.",
                    sourceRelativePath,
                    rowIndex));
            }

            var bodyNode = row.QuerySelector("q");
            var body = bodyNode is null ? null : ReadVisibleText(bodyNode);
            if (bodyNode is null)
            {
                issues.Add(new ImportIssue(
                    "message_body_missing",
                    "The message row has no q body element.",
                    sourceRelativePath,
                    rowIndex));
            }

            var sender = ParseSender(row, sourceRelativePath, rowIndex, issues);
            var attachmentReferences = ParseAttachmentReferences(row, sourceRelativePath, rowIndex, issues);

            messages.Add(new Message(
                sourceRelativePath,
                rawTimestamp,
                timestamp,
                body,
                sender,
                Direction: null,
                SourceRowIndex: rowIndex,
                AttachmentReferences: attachmentReferences));
        }

        return new MessagePageParseResult(true, messages, pageParticipantEvidence, issues);
    }

    private static Participant? ParseSender(IElement row, string sourceRelativePath, int rowIndex, List<ImportIssue> issues)
    {
        var senderLink = row.QuerySelector("a.tel");
        if (senderLink is null)
        {
            issues.Add(new ImportIssue(
                "message_sender_missing",
                "The message row has no a.tel sender link.",
                sourceRelativePath,
                rowIndex));
            return null;
        }

        var displayName = NormalizeOptionalText(senderLink.TextContent);
        var phoneNumber = GetRawPhoneNumber(senderLink.GetAttribute("href"));

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            phoneNumber = null;
            issues.Add(new ImportIssue(
                "message_sender_phone_missing",
                "The a.tel sender link has no tel: number.",
                sourceRelativePath,
                rowIndex));
        }

        if (displayName is null && string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        return new Participant(displayName, phoneNumber);
    }

    private static Participant? ParsePageParticipant(IElement participantLink)
    {
        var displayName = NormalizeOptionalText(participantLink.TextContent);
        var phoneNumber = GetRawPhoneNumber(participantLink.GetAttribute("href"));
        return displayName is null && phoneNumber is null
            ? null
            : new Participant(displayName, phoneNumber);
    }

    private static string? GetRawPhoneNumber(string? href)
    {
        if (href?.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var phoneNumber = href[4..];
        return string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber;
    }

    private static IReadOnlyList<Attachment> ParseAttachmentReferences(
        IElement row,
        string sourceRelativePath,
        int rowIndex,
        List<ImportIssue> issues)
    {
        var references = new List<Attachment>();

        foreach (var mediaElement in row.QuerySelectorAll("img, audio, video, source, a.video[href]"))
        {
            var isVideoLink = mediaElement.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase);
            var referenceAttribute = isVideoLink ? "href" : "src";
            var rawReference = mediaElement.GetAttribute(referenceAttribute);
            if (string.IsNullOrWhiteSpace(rawReference))
            {
                issues.Add(new ImportIssue(
                    "message_attachment_reference_missing",
                    "A media element or supported media link in the message row has no source reference.",
                    sourceRelativePath,
                    rowIndex));
                continue;
            }

            references.Add(new Attachment(
                rawReference,
                MatchedRelativePath: null,
                isVideoLink ? null : GetMediaType(mediaElement)));
        }

        return references;
    }

    private static string GetMediaType(IElement mediaElement)
    {
        if (mediaElement.LocalName.Equals("source", StringComparison.OrdinalIgnoreCase))
        {
            var parentName = mediaElement.ParentElement?.LocalName;
            if (parentName is not null
                && (parentName.Equals("audio", StringComparison.OrdinalIgnoreCase)
                    || parentName.Equals("video", StringComparison.OrdinalIgnoreCase)))
            {
                return parentName.ToLowerInvariant();
            }
        }

        return mediaElement.LocalName.ToLowerInvariant() switch
        {
            "img" => "image",
            "audio" => "audio",
            "video" => "video",
            _ => "unknown"
        };
    }

    private static string ReadVisibleText(IElement element)
    {
        var builder = new StringBuilder();
        foreach (var child in element.ChildNodes)
        {
            AppendVisibleText(child, builder);
        }

        return builder.ToString().Trim();
    }

    private static void AppendVisibleText(INode node, StringBuilder builder)
    {
        if (node is IText text)
        {
            builder.Append(text.Data);
            return;
        }

        if (node is not IElement childElement)
        {
            return;
        }

        if (childElement.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append('\n');
            return;
        }

        foreach (var child in childElement.ChildNodes)
        {
            AppendVisibleText(child, builder);
        }
    }

    private static string? NormalizeOptionalText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var trimmed = value.Trim();
        return HasExplicitOffset(trimmed)
            && DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        var timeSeparatorIndex = value.IndexOf('T');
        var offsetStart = Math.Max(value.LastIndexOf('+'), value.LastIndexOf('-'));
        if (timeSeparatorIndex < 0 || offsetStart <= timeSeparatorIndex)
        {
            return false;
        }

        var offset = value.AsSpan(offsetStart + 1);
        return offset.Length == 5
                && IsAsciiDigits(offset[0], offset[1], offset[3], offset[4])
                && offset[2] == ':'
            || offset.Length == 4
                && IsAsciiDigits(offset[0], offset[1], offset[2], offset[3]);
    }

    private static bool IsAsciiDigits(char first, char second, char third, char fourth) =>
        IsAsciiDigit(first) && IsAsciiDigit(second) && IsAsciiDigit(third) && IsAsciiDigit(fourth);

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
