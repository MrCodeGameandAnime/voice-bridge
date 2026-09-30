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
    string IssuesHealthSummary,
    string HealthParsedState,
    string HealthIssuesState,
    string HealthSourceState,
    string HealthExportState)
{
    public static WorkspaceStatusPresentation Create(
        ScanReport? scanReport,
        ImportReport? importReport,
        long exportIssueCount,
        bool archiveBuilt = false)
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
        var totalIssueCount = scanIssueCount + importIssueCount + exportIssueCount;
        var healthIssuesState = scanReport is null
            ? "Not scanned for issues"
            : importReport is null
                ? $"Scan issues: {scanIssueCount:N0}"
                : totalIssueCount > 0
                    ? $"⚠ {totalIssueCount:N0} issues"
                    : "✓ No issues";

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
            issuesHealthSummary,
            importReport is not null ? "✓ Imported" : scanReport is not null ? "✓ Scanned" : "Not scanned",
            healthIssuesState,
            scanReport is null
                ? "Source not verified"
                : scanReport.SourceKind == VoiceBridge.Core.Domain.SourceKind.ZipArchive
                    ? "✓ Source verified"
                    : "Folder scanned · no checksum available",
            archiveBuilt ? "✓ Archive built" : "Archive not built");
    }

    private static string FormatContentValue(long? scannedCount, long? importedCount, string scannedUnit) =>
        importedCount is long count
            ? count.ToString("N0")
            : scannedCount is long candidateCount
                ? $"{candidateCount:N0} {scannedUnit}"
                : "—";
}
