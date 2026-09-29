using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Importing;
using VoiceBridge.Core.Scanning;
using VoiceBridge.Desktop.Presentation;

namespace VoiceBridge.Tests;

public sealed class WorkspaceStatusPresentationTests
{
    [Fact]
    public void ScanOnlyCountsAreLabeledAsPagesAndFilesAndIssuesAreScanScoped()
    {
        var scan = CreateScanReport(warningCount: 0);

        var presentation = WorkspaceStatusPresentation.Create(scan, null, exportIssueCount: 0);

        Assert.Equal("2,443 pages", presentation.MessagesValue);
        Assert.Equal("4,837 pages", presentation.CallsValue);
        Assert.Equal("344 pages", presentation.VoicemailsValue);
        Assert.Equal("1,319 files", presentation.MediaValue);
        Assert.Equal("Scan issues", presentation.IssuesNavigationLabel);
        Assert.Equal("0", presentation.IssuesCount);
        Assert.Equal("Scan issues: 0", presentation.IssuesHealthSummary);
    }

    [Fact]
    public void ImportedCountsAreRecordCountsAndMediaReferencesWithIssueBreakdown()
    {
        var scan = CreateScanReport(warningCount: 1);
        var import = CreateImportReport(issueCount: 2);

        var presentation = WorkspaceStatusPresentation.Create(scan, import, exportIssueCount: 3);

        Assert.Equal("18,732", presentation.MessagesValue);
        Assert.Equal("4,837", presentation.CallsValue);
        Assert.Equal("344", presentation.VoicemailsValue);
        Assert.Equal("1,318 refs", presentation.MediaValue);
        Assert.Equal("Issues", presentation.IssuesNavigationLabel);
        Assert.Equal("6", presentation.IssuesCount);
        Assert.Equal("Scan issues: 1 · Import issues: 2 · Export issues: 3", presentation.IssuesHealthSummary);
    }

    [Fact]
    public void NoScanShowsUnknownCountsAndDoesNotClaimZeroIssues()
    {
        var presentation = WorkspaceStatusPresentation.Create(null, null, exportIssueCount: 0);

        Assert.Equal("—", presentation.MessagesValue);
        Assert.Equal("—", presentation.CallsValue);
        Assert.Equal("—", presentation.VoicemailsValue);
        Assert.Equal("—", presentation.MediaValue);
        Assert.Equal("Issues", presentation.IssuesNavigationLabel);
        Assert.Equal("—", presentation.IssuesCount);
        Assert.Equal("Not scanned for issues", presentation.IssuesHealthSummary);
    }

    private static ScanReport CreateScanReport(int warningCount) => new(
        "takeout.zip",
        SourceKind.ZipArchive,
        true,
        8_946,
        2_443,
        971,
        348,
        344,
        4_837,
        0,
        0,
        Enumerable.Range(0, warningCount).Select(index => new ScanWarning($"warning_{index}", "Scan warning")).ToArray(),
        205_940_118);

    private static ImportReport CreateImportReport(int issueCount) => new(
        new ImportInputIdentity(SourceKind.ZipArchive, "takeout.zip", null, 8_946),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        8_946,
        new ImportRecordCounts(18_732, 2_443, 973, 8_946, 4_837, 344, 1_318, 1_268, 50),
        0,
        issueCount,
        0,
        new Dictionary<string, long>(),
        Enumerable.Range(0, issueCount)
            .Select(index => new ImportIssueRecord($"issue_{index}", "Import issue", null, null, ImportIssueSeverity.Warning))
            .ToArray());
}
