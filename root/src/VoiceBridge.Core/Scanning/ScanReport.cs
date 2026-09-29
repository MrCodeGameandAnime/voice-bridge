using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Scanning;

public sealed record ScanReport(
    string SourcePath,
    SourceKind SourceKind,
    bool VoiceContentFound,
    long FilesScanned,
    long CandidateMessagePages,
    long CandidateImageVideoMediaFiles,
    long CandidateAudioMediaFiles,
    long CandidateVoicemailPages,
    long CandidateCallEventPages,
    long OtherVoiceFiles,
    long UnknownFiles,
    IReadOnlyList<ScanWarning> Warnings);
