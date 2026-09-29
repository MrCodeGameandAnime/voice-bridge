using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Parsing;

public sealed class VoiceEventParser
{
    private static readonly Regex EventPrefix = new("^\\s*(Placed|Received|Missed|Voicemail)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public VoiceEventParseResult Parse(string html, string sourceRelativePath)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);

        var document = new HtmlParser().ParseDocument(html);
        var eventTitle = document.QuerySelector(".haudio .fn");
        var eventMatch = EventPrefix.Match(eventTitle?.TextContent ?? string.Empty);
        if (!eventMatch.Success)
        {
            return new VoiceEventParseResult(
                false,
                null,
                null,
                [new ImportIssue(
                    "unsupported_voice_event_page",
                    "The Voice HTML page does not contain one of the observed call or voicemail event labels.",
                    sourceRelativePath)]);
        }

        var rawEventType = eventMatch.Groups[1].Value;
        var isVoicemail = rawEventType.Equals("Voicemail", StringComparison.OrdinalIgnoreCase);
        var issues = new List<ImportIssue>();
        var rawTimestamp = document.QuerySelector("abbr.published")?.GetAttribute("title");
        var timestamp = ParseTimestamp(rawTimestamp);
        if (string.IsNullOrWhiteSpace(rawTimestamp))
        {
            issues.Add(new ImportIssue(
                isVoicemail ? "voicemail_timestamp_missing" : "call_timestamp_missing",
                "The event page has no abbr.published title timestamp.",
                sourceRelativePath));
        }
        else if (timestamp is null)
        {
            issues.Add(new ImportIssue(
                isVoicemail ? "voicemail_timestamp_invalid" : "call_timestamp_invalid",
                "The event page timestamp could not be parsed as an offset date and time.",
                sourceRelativePath));
        }

        var tel = document.QuerySelector("a.tel");
        var rawLinkContact = NormalizeOptionalText(tel?.TextContent);
        var rawFilenameContact = ExtractFilenameContact(sourceRelativePath, rawEventType);
        var rawContact = rawLinkContact ?? NormalizeOptionalText(rawFilenameContact);
        var rawContactSource = rawLinkContact is not null
            ? "tel_link"
            : rawContact is not null ? "filename_label" : null;
        var rawPhoneNumber = GetRawPhoneNumber(tel?.GetAttribute("href"));
        if (rawContact is null)
        {
            issues.Add(new ImportIssue(
                isVoicemail ? "voicemail_contact_missing" : "call_contact_missing",
                "The event page has no visible a.tel contact label.",
                sourceRelativePath));
        }

        if (rawPhoneNumber is null)
        {
            issues.Add(new ImportIssue(
                isVoicemail ? "voicemail_phone_missing" : "call_phone_missing",
                "The event page a.tel link has no tel: number.",
                sourceRelativePath));
        }

        var durationNode = document.QuerySelector("abbr.duration");
        var rawDurationTitle = NormalizeOptionalText(durationNode?.GetAttribute("title"));
        var durationDisplayText = NormalizeOptionalText(durationNode?.TextContent);
        var duration = ParseDuration(rawDurationTitle) ?? ParseDuration(durationDisplayText);
        var mediaReferences = ParseMediaReferences(document, sourceRelativePath, isVoicemail, issues);

        if (isVoicemail)
        {
            var transcriptNode = document.QuerySelector("span.full-text");
            var transcript = transcriptNode is null ? null : transcriptNode.TextContent;
            var audioReference = mediaReferences.FirstOrDefault(reference => reference.MediaType == "audio")?.RawReference;
            return new VoiceEventParseResult(
                true,
                null,
                new Voicemail(
                    sourceRelativePath,
                    rawTimestamp,
                    timestamp,
                    transcript,
                    audioReference,
                    duration,
                    rawContact,
                    rawPhoneNumber,
                    rawDurationTitle,
                    durationDisplayText,
                    AudioMatchStatus: mediaReferences.Count == 0 ? "missing_reference" : "not_checked",
                    MediaReferences: mediaReferences,
                    RawFilenameContact: rawFilenameContact,
                    RawContactSource: rawContactSource),
                issues);
        }

        if (document.QuerySelector(".recording-error-message") is not null)
        {
            issues.Add(new ImportIssue(
                "call_recording_error_present",
                "The call event page contains a recording-error marker.",
                sourceRelativePath));
        }

        return new VoiceEventParseResult(
            true,
            new CallRecord(
                sourceRelativePath,
                rawEventType,
                rawTimestamp,
                timestamp,
                rawContact,
                rawPhoneNumber,
                duration,
                rawDurationTitle,
                durationDisplayText,
                mediaReferences,
                rawFilenameContact,
                rawContactSource),
            null,
            issues);
    }

    private static IReadOnlyList<MediaReference> ParseMediaReferences(
        IDocument document,
        string sourceRelativePath,
        bool isVoicemail,
        List<ImportIssue> issues)
    {
        var references = new List<MediaReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mediaElements = document.QuerySelectorAll(".haudio audio[src], .haudio audio source[src], .haudio video[src], .haudio video source[src], .haudio img[src]");
        foreach (var element in mediaElements)
        {
            var rawReference = NormalizeOptionalText(element.GetAttribute("src"));
            if (rawReference is null)
            {
                continue;
            }

            var mediaType = element.LocalName == "source"
                ? element.ParentElement?.LocalName
                : element.LocalName;
            mediaType = mediaType?.ToLowerInvariant() switch
            {
                "audio" => "audio",
                "video" => "video",
                "img" => "image",
                _ => null
            };

            if (seen.Add(rawReference))
            {
                references.Add(new MediaReference(rawReference, mediaType));
            }
        }

        if (isVoicemail && references.All(reference => reference.MediaType != "audio"))
        {
            foreach (var anchor in document.QuerySelectorAll(".haudio a[href]"))
            {
                var rawReference = NormalizeOptionalText(anchor.GetAttribute("href"));
                if (rawReference is null || !IsAudioReference(rawReference))
                {
                    continue;
                }

                if (seen.Add(rawReference))
                {
                    references.Add(new MediaReference(rawReference, "audio"));
                }
            }
        }

        var hasAudioElement = document.QuerySelector(".haudio audio, .haudio audio source") is not null;
        if (isVoicemail && references.All(reference => reference.MediaType != "audio"))
        {
            issues.Add(new ImportIssue(
                "voicemail_audio_reference_missing",
                hasAudioElement
                    ? "The voicemail audio element has no usable source reference."
                    : "The voicemail page has no audio element or explicit local audio link.",
                sourceRelativePath));
        }

        if (!isVoicemail && hasAudioElement && references.All(reference => reference.MediaType != "audio"))
        {
            issues.Add(new ImportIssue(
                "call_media_reference_missing",
                "The call event audio element has no usable source reference.",
                sourceRelativePath));
        }

        return references;
    }

    private static bool IsAudioReference(string reference)
    {
        var path = reference.Split(['?', '#'], 2)[0];
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".amr", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? ParseTimestamp(string? rawTimestamp)
    {
        if (string.IsNullOrWhiteSpace(rawTimestamp) || !HasExplicitTimestampZone(rawTimestamp))
        {
            return null;
        }

        return DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp
            : null;
    }

    private static bool HasExplicitTimestampZone(string rawTimestamp)
    {
        var timestamp = rawTimestamp.AsSpan().Trim();
        if (timestamp.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (timestamp.Length >= 6
            && timestamp[^6] is '+' or '-'
            && char.IsAsciiDigit(timestamp[^5])
            && char.IsAsciiDigit(timestamp[^4])
            && timestamp[^3] == ':'
            && char.IsAsciiDigit(timestamp[^2])
            && char.IsAsciiDigit(timestamp[^1]))
        {
            return true;
        }

        return timestamp.Length >= 5
            && timestamp[^5] is '+' or '-'
            && char.IsAsciiDigit(timestamp[^4])
            && char.IsAsciiDigit(timestamp[^3])
            && char.IsAsciiDigit(timestamp[^2])
            && char.IsAsciiDigit(timestamp[^1]);
    }

    private static string? ExtractFilenameContact(string sourceRelativePath, string rawEventType)
    {
        var normalizedPath = sourceRelativePath.Replace('\\', '/');
        var separatorIndex = normalizedPath.LastIndexOf('/');
        var fileName = separatorIndex < 0 ? normalizedPath : normalizedPath[(separatorIndex + 1)..];
        var extensionIndex = fileName.LastIndexOf('.');
        var stem = extensionIndex < 0 ? fileName : fileName[..extensionIndex];
        var marker = $" - {rawEventType} - ";
        var markerIndex = stem.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var filenameTimestamp = stem[(markerIndex + marker.Length)..];
        if (!DateTimeOffset.TryParseExact(
                filenameTimestamp,
                "yyyy-MM-dd'T'HH_mm_ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            return null;
        }

        return stem[..markerIndex];
    }

    private static TimeSpan? ParseDuration(string? rawDuration)
    {
        if (string.IsNullOrWhiteSpace(rawDuration))
        {
            return null;
        }

        if (rawDuration.StartsWith("P", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return XmlConvert.ToTimeSpan(rawDuration);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                return null;
            }
        }

        return TimeSpan.TryParseExact(
            rawDuration,
            [@"h\:mm\:ss", @"hh\:mm\:ss"],
            CultureInfo.InvariantCulture,
            TimeSpanStyles.None,
            out var duration)
                ? duration
                : null;
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

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
