using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Parsing;

public sealed record VoiceEventParseResult(
    bool IsSupportedEventPage,
    CallRecord? CallRecord,
    Voicemail? Voicemail,
    IReadOnlyList<ImportIssue> Issues);
