using VoiceBridge.Core.Parsing;

namespace VoiceBridge.Tests;

public sealed class VoiceEventParserTests
{
    [Theory]
    [InlineData("Placed", "Placed")]
    [InlineData("Received", "Received")]
    [InlineData("Missed", "Missed")]
    public void ParsesObservedCallEventAndKeepsRawEvidence(string label, string expectedEventType)
    {
        var path = "Voice/Calls/call-page.html";
        var parser = new VoiceEventParser();

        var result = parser.Parse(CallHtml(label, "2024-01-02T03:04:05-05:00", "A Contact", "tel:+15551234567", "not a duration"), path);

        Assert.True(result.IsSupportedEventPage);
        Assert.Empty(result.Issues);
        Assert.NotNull(result.CallRecord);
        Assert.Null(result.Voicemail);
        Assert.Equal(path, result.CallRecord.SourceRelativePath);
        Assert.Equal(expectedEventType, result.CallRecord.RawEventType);
        Assert.Equal("2024-01-02T03:04:05-05:00", result.CallRecord.RawTimestamp);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)), result.CallRecord.Timestamp);
        Assert.Equal("A Contact", result.CallRecord.RawContact);
        Assert.Equal("+15551234567", result.CallRecord.RawPhoneNumber);
        Assert.Equal("not a duration", result.CallRecord.RawDurationTitle);
        Assert.Null(result.CallRecord.Duration);
    }

    [Fact]
    public void ParsesVoicemailTranscriptAndDirectAudioReferenceWithoutInventingDuration()
    {
        const string path = "Voice/Calls/voicemail-page.html";
        var parser = new VoiceEventParser();

        var result = parser.Parse(VoicemailHtml("2024-02-03T04:05:06-05:00", "Caller", "tel:+15550001111", "Transcript line one\nline two", "../Calls/voice-file.mp3", "duration label"), path);

        Assert.True(result.IsSupportedEventPage);
        Assert.Empty(result.Issues);
        Assert.Null(result.CallRecord);
        Assert.NotNull(result.Voicemail);
        Assert.Equal(path, result.Voicemail.SourceRelativePath);
        Assert.Equal("2024-02-03T04:05:06-05:00", result.Voicemail.RawTimestamp);
        Assert.Equal("Transcript line one\nline two", result.Voicemail.Transcript);
        Assert.Equal("../Calls/voice-file.mp3", result.Voicemail.AudioReference);
        Assert.Equal("Caller", result.Voicemail.RawContact);
        Assert.Equal("+15550001111", result.Voicemail.RawPhoneNumber);
        Assert.Equal("duration label", result.Voicemail.RawDurationTitle);
        Assert.Null(result.Voicemail.Duration);
        Assert.Equal("not_checked", result.Voicemail.AudioMatchStatus);
        Assert.Null(result.Voicemail.MatchedAudioRelativePath);
    }

    [Fact]
    public void MissingOptionalVoicemailAudioRetainsRecordAndReportsRelationshipIssue()
    {
        var parser = new VoiceEventParser();

        var result = parser.Parse(VoicemailHtml("not-a-time", "Caller", "tel:+15550001111", transcript: null, audioReference: null, durationTitle: null), "Voice/Spam/voicemail.html");

        Assert.NotNull(result.Voicemail);
        Assert.Null(result.Voicemail.Timestamp);
        Assert.Null(result.Voicemail.AudioReference);
        Assert.Null(result.Voicemail.Transcript);
        Assert.Contains(result.Issues, issue => issue.Code == "voicemail_timestamp_invalid");
        Assert.Contains(result.Issues, issue => issue.Code == "voicemail_audio_reference_missing");
        Assert.DoesNotContain(result.Issues, issue => issue.Code.Contains("transcript", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TimestampWithoutExplicitOffsetRemainsUnknownAndIsReported()
    {
        var result = new VoiceEventParser().Parse(
            CallHtml("Placed", "2024-01-02T03:04:05", "A Contact", "tel:+15551234567", null),
            "Voice/Calls/call-page.html");

        Assert.NotNull(result.CallRecord);
        Assert.Equal("2024-01-02T03:04:05", result.CallRecord.RawTimestamp);
        Assert.Null(result.CallRecord.Timestamp);
        Assert.Contains(result.Issues, issue => issue.Code == "call_timestamp_invalid");
    }

    [Fact]
    public void OutOfRangeIsoDurationRetainsCallAndRawDurationWithoutThrowing()
    {
        const string duration = "P999999999D";
        var result = new VoiceEventParser().Parse(
            CallHtml("Placed", "2024-01-02T03:04:05-05:00", "A Contact", "tel:+15551234567", duration),
            "Voice/Calls/call-page.html");

        Assert.NotNull(result.CallRecord);
        Assert.Equal(duration, result.CallRecord.RawDurationTitle);
        Assert.Null(result.CallRecord.Duration);
    }

    [Fact]
    public void UsesObservedFilenameContactOnlyWhenTelLabelIsEmptyAndKeepsItsProvenance()
    {
        const string path = "Voice/Calls/Saved Contact - Missed - 2024-01-02T03_04_05Z.html";
        var html = CallHtml("Missed", "2024-01-02T03:04:05-05:00", string.Empty, "tel:+15551234567", null);

        var result = new VoiceEventParser().Parse(html, path);

        Assert.NotNull(result.CallRecord);
        Assert.Equal("Saved Contact", result.CallRecord.RawContact);
        Assert.Equal("Saved Contact", result.CallRecord.RawFilenameContact);
        Assert.Equal("filename_label", result.CallRecord.RawContactSource);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "call_contact_missing");
    }

    [Fact]
    public void ParsesOnlyTheObservedUnambiguousHourMinuteSecondDurationShape()
    {
        var html = CallHtml("Placed", "2024-01-02T03:04:05-05:00", "Contact", "tel:+15551234567", "display label")
            .Replace("duration evidence", "00:02:13", StringComparison.Ordinal);

        var result = new VoiceEventParser().Parse(html, "Voice/Calls/Contact - Placed - 2024-01-02T03_04_05Z.html");

        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(13), result.CallRecord?.Duration);
        Assert.Equal("display label", result.CallRecord?.RawDurationTitle);
        Assert.Equal("00:02:13", result.CallRecord?.DurationDisplayText);
    }

    [Fact]
    public void RecordingErrorMarkerIsReportedWithoutDroppingCallRecord()
    {
        var html = CallHtml("Received", "2024-03-04T05:06:07-05:00", "Caller", "tel:+15550002222", null)
            .Replace("</body>", "<div class=\"recording-error-message\">Recording unavailable</div></body>", StringComparison.Ordinal);

        var result = new VoiceEventParser().Parse(html, "Voice/Calls/received.html");

        Assert.NotNull(result.CallRecord);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("call_recording_error_present", issue.Code);
        Assert.DoesNotContain("Recording unavailable", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEventLabelRemainsUnsupportedWithSourceIssue()
    {
        const string path = "Voice/Calls/unrecognized.html";
        var html = CallHtml("Changed", "2024-04-05T06:07:08-05:00", "Contact", "tel:+15550003333", null);

        var result = new VoiceEventParser().Parse(html, path);

        Assert.False(result.IsSupportedEventPage);
        Assert.Null(result.CallRecord);
        Assert.Null(result.Voicemail);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("unsupported_voice_event_page", issue.Code);
        Assert.Equal(path, issue.SourceRelativePath);
    }

    private static string CallHtml(string label, string timestamp, string contact, string telHref, string? durationTitle) =>
        $"""<!doctype html><html><body><div class="haudio"><span class="fn">{label} call from {contact}</span><a class="tel" href="{telHref}">{contact}</a><abbr class="published" title="{timestamp}">visible time</abbr>{(durationTitle is null ? string.Empty : $"<abbr class=\"duration\" title=\"{durationTitle}\">duration evidence</abbr>")}</div></body></html>""";

    private static string VoicemailHtml(string timestamp, string contact, string telHref, string? transcript, string? audioReference, string? durationTitle) =>
        $"""<!doctype html><html><body><div class="haudio"><span class="fn">Voicemail from {contact}</span><a class="tel" href="{telHref}">{contact}</a><abbr class="published" title="{timestamp}">visible time</abbr>{(durationTitle is null ? string.Empty : $"<abbr class=\"duration\" title=\"{durationTitle}\">duration evidence</abbr>")}{(transcript is null ? string.Empty : $"<span class=\"full-text\">{transcript}</span>")}{(audioReference is null ? string.Empty : $"<audio src=\"{audioReference}\"></audio>")}</div></body></html>""";
}
