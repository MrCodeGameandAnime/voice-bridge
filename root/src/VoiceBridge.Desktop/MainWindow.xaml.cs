using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoiceBridge.Core.Importing;
using VoiceBridge.Core.Scanning;
using VoiceBridge.Export;
using VoiceBridge.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VoiceBridge.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };
    private readonly DispatcherQueue _dispatcherQueue;
    private string? _sourcePath;
    private string? _outputDirectory;
    private string? _archiveIndexPath;
    private ImportReport? _importReport;
    private ScanReport? _scanReport;
    private CancellationTokenSource? _operationCancellation;

    public MainWindow()
    {
        InitializeComponent();
        Title = "VoiceBridge";
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The UI dispatcher is unavailable.");
    }

    private async void SelectZip_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                SetSource(file.Path);
            }
        }
        catch (Exception exception)
        {
            SetStatus(GetUserMessage(exception, "Windows couldn't open the file picker."), isError: true);
        }
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                SetSource(folder.Path);
            }
        }
        catch (Exception exception)
        {
            SetStatus(GetUserMessage(exception, "Windows couldn't open the folder picker."), isError: true);
        }
    }

    private async void SelectOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                _outputDirectory = folder.Path;
                OutputPathText.Text = folder.Path;
                ResultsCard.Visibility = Visibility.Collapsed;
                RefreshControls();
            }
        }
        catch (Exception exception)
        {
            SetStatus(GetUserMessage(exception, "Windows couldn't open the folder picker."), isError: true);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath))
        {
            SetStatus("Choose a ZIP file or extracted Takeout folder first.", isError: true);
            return;
        }

        _scanReport = null;
        RefreshControls();
        using var cancellation = BeginOperation("Scanning the Takeout source…");
        ScanCard.Visibility = Visibility.Collapsed;
        ResultsCard.Visibility = Visibility.Collapsed;
        try
        {
            var scanResult = await Task.Run(
                () => new TakeoutScanner().Scan(_sourcePath, cancellation.Token),
                cancellation.Token);
            if (!scanResult.IsSuccess)
            {
                throw new InvalidDataException(scanResult.Error!.Message);
            }

            _scanReport = scanResult.Value!;
            ScanSummaryText.Text = $"Files scanned: {_scanReport.FilesScanned:N0}\n"
                + $"Message pages: {_scanReport.CandidateMessagePages:N0}\n"
                + $"Call/event pages: {_scanReport.CandidateCallEventPages:N0}\n"
                + $"Voicemail pages: {_scanReport.CandidateVoicemailPages:N0}\n"
                + $"Image/video media files: {_scanReport.CandidateImageVideoMediaFiles:N0}\n"
                + $"Audio media files: {_scanReport.CandidateAudioMediaFiles:N0}\n"
                + $"Other Voice files: {_scanReport.OtherVoiceFiles:N0}\n"
                + $"Unclassified files: {_scanReport.UnknownFiles:N0}";
            ScanWarningText.Text = _scanReport.Warnings.Count == 0
                ? "No scan warnings."
                : $"{_scanReport.Warnings.Count:N0} scan warning(s). Unclassified files and warnings remain visible in this summary.";
            ScanCard.Visibility = Visibility.Visible;
            SetStatus(_scanReport.VoiceContentFound
                ? "Scan complete. Review the summary before starting the import."
                : "Scan complete. No Google Voice content was identified; the source is still available for review.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan canceled.");
        }
        catch (Exception exception)
        {
            SetStatus(GetUserMessage(exception, "VoiceBridge couldn't scan that source. Check that it is readable and try again."), isError: true);
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    private async void StartImport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath) || string.IsNullOrWhiteSpace(_outputDirectory) || _scanReport is null)
        {
            SetStatus("Choose a source and destination, then scan before importing.", isError: true);
            return;
        }

        var sourcePath = Path.GetFullPath(_sourcePath);
        var outputDirectory = Path.GetFullPath(_outputDirectory);
        var databasePath = Path.Combine(outputDirectory, "voicebridge.db");
        var reportPath = Path.Combine(outputDirectory, "import-report.json");
        var archiveDirectory = Path.Combine(outputDirectory, "archive");
        try
        {
            SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(sourcePath, outputDirectory);
            if (File.Exists(databasePath) || File.Exists(reportPath) || Directory.Exists(archiveDirectory))
            {
                SetStatus("This destination already contains VoiceBridge output. Choose an empty destination folder so existing files are preserved.", isError: true);
                return;
            }
        }
        catch (ArgumentException exception)
        {
            SetStatus(exception.Message, isError: true);
            return;
        }

        _archiveIndexPath = null;
        _importReport = null;
        OpenArchiveButton.IsEnabled = false;
        ViewIssuesButton.IsEnabled = false;
        using var cancellation = BeginOperation("Preparing the import…");
        ResultsCard.Visibility = Visibility.Collapsed;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var service = new TakeoutImportService(new SqliteTakeoutImportStoreFactory());
            service.ProgressChanged += (_, progress) => _dispatcherQueue.TryEnqueue(() => UpdateImportProgress(progress));

            SetStatus("Importing messages, calls, voicemails, and source evidence…");
            var report = await Task.Run(
                () => service.ImportAsync(sourcePath, databasePath, cancellation.Token),
                cancellation.Token);
            _importReport = report;
            ViewIssuesButton.IsEnabled = true;
            ResultsSummaryText.Text = FormatImportSummary(report);
            await WriteImportReportAsync(reportPath, report, CancellationToken.None);

            SetStatus("Building the offline HTML archive…");
            await Task.Run(
                () => TakeoutArchiveExporter.ExportHtmlAsync(databasePath, archiveDirectory, cancellation.Token),
                cancellation.Token);

            _archiveIndexPath = Path.Combine(archiveDirectory, "index.html");
            OpenArchiveButton.IsEnabled = File.Exists(_archiveIndexPath);
            ResultStatusText.Text = "The offline archive is ready. Nothing was uploaded.";
            ResultsCard.Visibility = Visibility.Visible;
            SetStatus("Import and archive export complete.");
        }
        catch (OperationCanceledException)
        {
            ResultStatusText.Text = "Operation canceled. Any completed database and import report remain in the destination.";
            ResultsSummaryText.Text = _importReport is null
                ? "The import did not finish. No imported records are available."
                : FormatImportSummary(_importReport);
            ResultsCard.Visibility = Visibility.Visible;
            ViewIssuesButton.IsEnabled = _importReport is not null;
            OpenArchiveButton.IsEnabled = _archiveIndexPath is not null && File.Exists(_archiveIndexPath);
            SetStatus("Operation canceled.");
        }
        catch (Exception exception)
        {
            ResultStatusText.Text = "The operation stopped before all outputs were ready. Existing files were preserved.";
            ResultsSummaryText.Text = _importReport is null
                ? "No completed import report is available."
                : FormatImportSummary(_importReport);
            ResultsCard.Visibility = Visibility.Visible;
            ViewIssuesButton.IsEnabled = _importReport is not null;
            SetStatus(GetUserMessage(exception, "VoiceBridge couldn't complete the import. Check the source and destination, then try again."), isError: true);
        }
        finally
        {
            EndOperation(cancellation);
            RefreshControls();
        }
    }

    private void UpdateImportProgress(ImportProgress progress)
    {
        ProgressStatusText.Text = progress.Message;
        if (progress.Stage == ImportProgressStage.ProcessingMessages && progress.Total > 0)
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = Math.Clamp(progress.Completed * 100d / progress.Total, 0, 100);
        }
        else
        {
            OperationProgressBar.IsIndeterminate = true;
        }
    }

    private static async Task WriteImportReportAsync(string reportPath, ImportReport report, CancellationToken cancellationToken)
    {
        await using var reportStream = new FileStream(
            reportPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(reportStream, report, ReportJsonOptions, cancellationToken);
    }

    private static string FormatImportSummary(ImportReport report) =>
        $"Messages: {report.RecordsParsed.Messages:N0}\n"
        + $"Conversations: {report.RecordsParsed.Conversations:N0}\n"
        + $"Calls: {report.RecordsParsed.Calls:N0}\n"
        + $"Voicemails: {report.RecordsParsed.Voicemails:N0}\n"
        + $"Media references: {report.RecordsParsed.MediaReferences:N0} ({report.RecordsParsed.MatchedMediaReferences:N0} matched, {report.RecordsParsed.UnresolvedMediaReferences:N0} unresolved)\n"
        + $"Warnings: {report.Warnings:N0}    Errors: {report.Errors:N0}";

    private async void ViewIssues_Click(object sender, RoutedEventArgs e)
    {
        if (_importReport is null)
        {
            return;
        }

        var issueText = _importReport.Issues.Count == 0
            ? "No import issues were recorded."
            : string.Join(Environment.NewLine + Environment.NewLine, _importReport.Issues.Select(FormatIssue));
        var content = new ScrollViewer
        {
            MaxHeight = 480,
            Content = new TextBlock
            {
                Text = issueText,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        };
        var dialog = new ContentDialog
        {
            Title = $"Import issues ({_importReport.Issues.Count:N0})",
            Content = content,
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static string FormatIssue(ImportIssueRecord issue)
    {
        var location = issue.SourceRelativePath is null ? string.Empty : $"\nSource: {issue.SourceRelativePath}";
        var row = issue.SourceRowIndex is null ? string.Empty : $"\nRow: {issue.SourceRowIndex.Value:N0}";
        return $"{issue.Severity}: {issue.Code}\n{issue.Message}{location}{row}";
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        await OpenPathAsync(_archiveIndexPath, "The offline archive isn't available yet.");
    }

    private async void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        await OpenPathAsync(_outputDirectory, "Choose an output folder first.");
    }

    private async Task OpenPathAsync(string? path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            SetStatus(missingMessage, isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(GetUserMessage(exception, "Windows couldn't open that location."));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        SetStatus("Canceling safely…");
        ProgressStatusText.Text = "Canceling safely…";
        _operationCancellation.Cancel();
    }

    private CancellationTokenSource BeginOperation(string initialStatus)
    {
        _operationCancellation = new CancellationTokenSource();
        ProgressCard.Visibility = Visibility.Visible;
        ProgressStatusText.Text = initialStatus;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.Value = 0;
        CancelButton.IsEnabled = true;
        RefreshControls();
        return _operationCancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_operationCancellation, cancellation))
        {
            _operationCancellation = null;
        }

        cancellation.Dispose();
        ProgressCard.Visibility = Visibility.Collapsed;
        RefreshControls();
    }

    private void SetSource(string sourcePath)
    {
        _sourcePath = sourcePath;
        SourcePathBox.Text = sourcePath;
        _scanReport = null;
        ScanCard.Visibility = Visibility.Collapsed;
        ResultsCard.Visibility = Visibility.Collapsed;
        SetStatus("Source selected. Choose a destination, then scan the Takeout.");
        RefreshControls();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(isError ? Colors.DarkRed : Colors.DarkSlateGray);
        if (_operationCancellation is not null)
        {
            ProgressStatusText.Text = message;
            ProgressStatusText.Foreground = new SolidColorBrush(isError ? Colors.DarkRed : Colors.DarkSlateGray);
        }
    }

    private async Task ShowMessageAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "VoiceBridge",
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void RefreshControls()
    {
        var busy = _operationCancellation is not null;
        SelectZipButton.IsEnabled = !busy;
        SelectFolderButton.IsEnabled = !busy;
        SelectOutputButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_sourcePath);
        StartImportButton.IsEnabled = !busy && _scanReport is not null && !string.IsNullOrWhiteSpace(_outputDirectory);
        OpenOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(_outputDirectory) && Directory.Exists(_outputDirectory);
    }

    private static string GetUserMessage(Exception exception, string fallback) => exception switch
    {
        UnauthorizedAccessException => "VoiceBridge doesn't have permission to read the source or write to the destination. Choose locations you can access.",
        IOException => "VoiceBridge couldn't read or write a file. Check that the source is available and the destination has space, then try again.",
        InvalidDataException => exception.Message,
        ArgumentException => exception.Message,
        _ => fallback
    };
}
