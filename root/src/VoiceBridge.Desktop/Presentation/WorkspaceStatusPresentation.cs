using VoiceBridge.Core.Importing;
using VoiceBridge.Core.Scanning;

namespace VoiceBridge.Desktop.Presentation;

internal sealed record WorkspaceStatusPresentation(
    string MessagesValue,
    string CallsValue,
    string VoicemailsValue,
    string MediaValue,
    string IssuesNavigationLabel,
    string IssuesCount,
    string IssuesHealthSummary)
{
    public static WorkspaceStatusPresentation Create(
        ScanReport? scanReport,
        ImportReport? importReport,
        long exportIssueCount)
    {
        var scanIssueCount = scanReport?.Warnings.Count ?? 0;
        var importIssueCount = importReport?.Issues.Count ?? 0;
        var issuesCount = scanReport is null
            ? "—"
            : (scanIssueCount + importIssueCount + exportIssueCount).ToString("N0");
        var issueNavigationLabel = scanReport is not null && importReport is null
            ? "Scan issues"
            : "Issues";
        var issuesHealthSummary = scanReport is null
            ? "Not scanned for issues"
            : importReport is null
                ? $"Scan issues: {scanIssueCount:N0}"
                : $"Scan issues: {scanIssueCount:N0} · Import issues: {importIssueCount:N0} · Export issues: {exportIssueCount:N0}";

        return new WorkspaceStatusPresentation(
            FormatContentValue(scanReport?.CandidateMessagePages, importReport?.RecordsParsed.Messages, "pages"),
            FormatContentValue(scanReport?.CandidateCallEventPages, importReport?.RecordsParsed.Calls, "pages"),
            FormatContentValue(scanReport?.CandidateVoicemailPages, importReport?.RecordsParsed.Voicemails, "pages"),
            importReport is not null
                ? $"{importReport.RecordsParsed.MediaReferences:N0} refs"
                : FormatContentValue(
                    scanReport is null
                        ? null
                        : scanReport.CandidateImageVideoMediaFiles + scanReport.CandidateAudioMediaFiles,
                    null,
                    "files"),
            issueNavigationLabel,
            issuesCount,
            issuesHealthSummary);
    }

    private static string FormatContentValue(long? scannedCount, long? importedCount, string scannedUnit) =>
        importedCount is long count
            ? count.ToString("N0")
            : scannedCount is long candidateCount
                ? $"{candidateCount:N0} {scannedUnit}"
                : "—";
}
