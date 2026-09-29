using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Scanning;

public sealed record ScanReport(
    string SourcePath,
    SourceKind SourceKind,
    bool VoiceContentFound,
    long FilesScanned,
    long CandidateMessageFiles,
    long CandidateAttachments,
    long CandidateVoicemailMediaFiles,
    long VoicemailPages,
    long CallEventPages,
    long OtherVoiceFiles,
    long UnknownFiles,
    IReadOnlyList<ScanWarning> Warnings);
