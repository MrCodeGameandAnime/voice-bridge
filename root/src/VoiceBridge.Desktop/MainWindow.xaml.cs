using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Importing;
using VoiceBridge.Core.Scanning;
using VoiceBridge.Desktop.Diagnostics;
using VoiceBridge.Desktop.Presentation;
using VoiceBridge.Export;
using VoiceBridge.Storage;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VoiceBridge.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };
    private const int BrowserPageSize = 60;
    private const int MessagePageSize = 50;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<WorkspaceEntry> _exportIssues = [];
    private readonly ObservableCollection<BrowserItem> _browserItems = [];
    private List<BrowserItem> _localBrowserItems = [];
    private DispatcherQueueTimer? _searchDebounceTimer;
    private string? _sourcePath;
    private string? _outputDirectory;
    private string? _databasePath;
    private string? _archiveIndexPath;
    private string? _csvExportDirectory;
    private ImportReport? _importReport;
    private ScanReport? _scanReport;
    private ExportSummary? _htmlExportSummary;
    private ExportSummary? _csvExportSummary;
    private CancellationTokenSource? _operationCancellation;
    private string _currentViewName = "Overview";
    private long _browserGeneration;
    private long _detailGeneration;
    private long _browserTotalCount;
    private double _browserListWidth = 380;
    private double _browserResizeStartX;
    private double _browserResizeStartWidth;
    private int _databaseOffset;
    private int _localBrowserOffset;
    private long? _selectedConversationId;
    private int _detailMessageOffset;
    private List<WorkspaceEntry> _selectedMessageEntries = [];
    private bool _suppressBrowserEvents;
    private bool _isResizingBrowserPane;

    public MainWindow()
    {
        InitializeComponent();
        Title = "VoiceBridge";
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionAttributionText.Text = $"404 Builds · VoiceBridge {version}";
        var windowHandle = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(windowHandle));
        appWindow.Resize(new SizeInt32(1320, 900));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The UI dispatcher is unavailable.");
        BrowserListView.ItemsSource = _browserItems;
        _searchDebounceTimer = _dispatcherQueue.CreateTimer();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(350);
        _searchDebounceTimer.IsRepeating = false;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer?.Stop();
            _ = LoadBrowserPageAsync(reset: true);
        };
        WorkspaceNavigation.SelectedItem = OverviewNavigationItem;
        ShowWorkspaceView("Overview");
        RefreshWorkspace();
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
                _outputDirectory = Path.GetFullPath(folder.Path);
                ClearImportedState();
                UpdateCrashLogDirectory();
                SetStatus("Destination selected. VoiceBridge will write generated files only after you choose Build local archive.");
                RefreshWorkspace();
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
            SetStatus("Select a ZIP file or extracted Takeout folder first.", isError: true);
            return;
        }

        using var cancellation = BeginOperation("Scanning and verifying the source…");
        try
        {
            var sourcePath = _sourcePath;
            var scanResult = await Task.Run(
                () => new TakeoutScanner().Scan(sourcePath, cancellation.Token),
                cancellation.Token);
            if (!scanResult.IsSuccess)
            {
                throw new InvalidDataException(scanResult.Error!.Message);
            }

            _scanReport = scanResult.Value!;
            SetStatus(_scanReport.VoiceContentFound
                ? $"Scan complete. {_scanReport.FilesScanned:N0} files inventoried; choose Export when you are ready to build the local archive."
                : "Scan complete. No Google Voice content was identified; the inventory and warnings remain available for review.");
            RefreshWorkspace();
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

    private async void BuildArchive_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath) || _scanReport is null || string.IsNullOrWhiteSpace(_outputDirectory))
        {
            SetStatus("Select and scan a source, then choose a destination in Export.", isError: true);
            return;
        }

        var sourcePath = Path.GetFullPath(_sourcePath);
        var outputDirectory = Path.GetFullPath(_outputDirectory);
        var databasePath = Path.Combine(outputDirectory, "voicebridge.db");
        var reportPath = Path.Combine(outputDirectory, "import-report.json");
        var archiveDirectory = Path.Combine(outputDirectory, "archive");
        _databasePath = null;
        _archiveIndexPath = null;
        _htmlExportSummary = null;
        _csvExportSummary = null;
        _csvExportDirectory = null;
        _exportIssues.Clear();
        _importReport = null;

        try
        {
            SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(sourcePath, outputDirectory);
            if (File.Exists(databasePath) || File.Exists(reportPath) || Directory.Exists(archiveDirectory))
            {
                SetStatus("This destination already contains VoiceBridge output. Choose a destination without voicebridge.db, import-report.json, or archive so existing files are preserved.", isError: true);
                RefreshWorkspace();
                return;
            }
        }
        catch (ArgumentException exception)
        {
            SetStatus(exception.Message, isError: true);
            return;
        }

        var cancellation = BeginOperation("Preparing the local import…");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var service = new TakeoutImportService(new SqliteTakeoutImportStoreFactory());
            service.ProgressChanged += (_, progress) => _dispatcherQueue.TryEnqueue(() => UpdateImportProgress(progress));

            SetStatus("Importing messages, calls, voicemails, media references, and source evidence…");
            _importReport = await Task.Run(
                () => service.ImportAsync(sourcePath, databasePath, cancellation.Token),
                cancellation.Token);
            _databasePath = databasePath;
            await WriteImportReportAsync(reportPath, _importReport, cancellation.Token);

            SetStatus("The local archive is ready. Opening records now reads only the page you request.");
            RefreshWorkspace();

            SetStatus("Building the offline HTML archive…");
            _htmlExportSummary = await Task.Run(
                () => TakeoutArchiveExporter.ExportHtmlAsync(databasePath, archiveDirectory, cancellation.Token),
                cancellation.Token);
            _archiveIndexPath = Path.Combine(archiveDirectory, "index.html");
            _exportIssues.AddRange(ReadUnavailableMediaIssues(Path.Combine(archiveDirectory, "export-report.json"), "HTML export"));
            SetStatus("Import complete. The offline archive is ready on this PC; nothing was uploaded.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation canceled. Any completed database, report, or archive files remain in the selected destination.");
            await LoadCompletedImportIfAvailableAsync(databasePath);
        }
        catch (Exception exception)
        {
            _exportIssues.Add(CreateOperationIssue("Build operation stopped", exception));
            SetStatus(GetUserMessage(exception, "VoiceBridge couldn't complete the build. Check the source and destination, then try again."), isError: true);
            await LoadCompletedImportIfAvailableAsync(databasePath);
        }
        finally
        {
            EndOperation(cancellation);
            RefreshWorkspace();
        }
    }

    private Task LoadCompletedImportIfAvailableAsync(string databasePath)
    {
        if (_importReport is null || !File.Exists(databasePath))
        {
            return Task.CompletedTask;
        }

        _databasePath = databasePath;
        return Task.CompletedTask;
    }

    private void UpdateImportProgress(ImportProgress progress)
    {
        StatusText.Text = progress.Message;
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

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_databasePath) || string.IsNullOrWhiteSpace(_outputDirectory))
        {
            SetStatus("Build the local archive before creating a CSV export.", isError: true);
            return;
        }

        var csvDirectory = Path.Combine(_outputDirectory, "csv-export");
        if (Directory.Exists(csvDirectory) || File.Exists(csvDirectory))
        {
            SetStatus("The csv-export folder already exists. Choose a new destination in Export to preserve the existing files.", isError: true);
            return;
        }

        var cancellation = BeginOperation("Creating relational CSV tables…");
        try
        {
            _csvExportDirectory = csvDirectory;
            _csvExportSummary = await Task.Run(
                () => TakeoutArchiveExporter.ExportCsvAsync(_databasePath, csvDirectory, cancellation.Token),
                cancellation.Token);
            _exportIssues.AddRange(ReadUnavailableMediaIssues(Path.Combine(csvDirectory, "export-report.json"), "CSV export"));
            SetStatus("CSV export complete. Source media was not copied or modified.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("CSV export canceled. Any completed files remain in the selected destination.");
        }
        catch (Exception exception)
        {
            _exportIssues.Add(CreateOperationIssue("CSV export stopped", exception));
            SetStatus(GetUserMessage(exception, "VoiceBridge couldn't create the CSV export."), isError: true);
        }
        finally
        {
            EndOperation(cancellation);
            RefreshWorkspace();
        }
    }

    private async Task WriteImportReportAsync(string reportPath, ImportReport report, CancellationToken cancellationToken)
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

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var selectedItem = args.SelectedItemContainer;
        if (selectedItem?.Tag is string viewName)
        {
            ShowWorkspaceView(viewName);
        }
    }

    private void OpenExport_Click(object sender, RoutedEventArgs e) => WorkspaceNavigation.SelectedItem = ExportNavigationItem;

    private void ShowWorkspaceView(string viewName)
    {
        _currentViewName = viewName;
        OverviewView.Visibility = viewName == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ContentBrowserView.Visibility = viewName is "Messages" or "Calls" or "Voicemails" or "Media" or "Issues" ? Visibility.Visible : Visibility.Collapsed;
        ExportView.Visibility = viewName == "Export" ? Visibility.Visible : Visibility.Collapsed;
        if (ContentBrowserView.Visibility == Visibility.Visible)
        {
            ConfigureBrowserView();
            _ = LoadBrowserPageAsync(reset: true);
        }
    }

    private void RefreshWorkspace()
    {
        RefreshSidebar();
        RefreshOverview();
        RefreshExportView();
        RefreshControls();
        if (ContentBrowserView.Visibility == Visibility.Visible)
        {
            _ = LoadBrowserPageAsync(reset: true);
        }
    }

    private void RefreshSidebar()
    {
        if (_sourcePath is null)
        {
            SidebarSourceText.Text = "○ No archive loaded";
            ToolTipService.SetToolTip(SidebarSourceText, "No archive loaded");
        }
        else
        {
            SidebarSourceText.Text = $"● {Path.GetFileName(Path.TrimEndingDirectorySeparator(_sourcePath))}";
            ToolTipService.SetToolTip(SidebarSourceText, _sourcePath);
        }

        var presentation = GetWorkspaceStatusPresentation();
        MessagesCountText.Text = presentation.MessagesValue;
        CallsCountText.Text = presentation.CallsValue;
        VoicemailsCountText.Text = presentation.VoicemailsValue;
        MediaCountText.Text = presentation.MediaValue;
        IssuesNavigationLabelText.Text = presentation.IssuesNavigationLabel;
        IssuesCountText.Text = presentation.IssuesCount;

        HealthParsedText.Text = presentation.HealthParsedState;
        HealthIssuesText.Text = presentation.HealthIssuesState;
        HealthSourceText.Text = presentation.HealthSourceState;
        HealthExportText.Text = presentation.HealthExportState;
    }

    private void RefreshOverview()
    {
        if (_sourcePath is null)
        {
            OverviewTitleText.Text = "Open your Google Voice Takeout";
            OverviewSubtitleText.Text = "Select the original ZIP or extracted folder. VoiceBridge will inspect it without modifying it.";
            OverviewSourcePathText.Text = "No archive selected";
            OverviewSourceSummaryText.Text = "Choose a Google Voice Takeout ZIP or extracted folder to inspect it locally.";
            ToolTipService.SetToolTip(OverviewSourcePathText, "No archive selected");
            ScanSummaryText.Text = "Scanning checks the archive and classifies its files. It does not require an output destination.";
        }
        else
        {
            OverviewTitleText.Text = _scanReport is null ? "Review your Google Voice Takeout" : "Archive overview";
            OverviewSubtitleText.Text = "The original source stays read-only. Scan it here, then choose a destination under Export when you are ready to build.";
            OverviewSourcePathText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_sourcePath));
            ToolTipService.SetToolTip(OverviewSourcePathText, _sourcePath);
            OverviewSourceSummaryText.Text = FormatSourceSummary();
            ScanSummaryText.Text = _scanReport is null
                ? "Source selected. Scan it to inventory files and verify ZIP member checksums; no output destination is needed."
                : FormatScanSummary(_scanReport);
        }

        ScanButton.Content = _scanReport is null ? "Scan archive" : "Scan again";

        var presentation = GetWorkspaceStatusPresentation();
        OverviewMessagesText.Text = $"Messages  {presentation.MessagesValue}";
        OverviewCallsText.Text = $"Calls  {presentation.CallsValue}";
        OverviewVoicemailsText.Text = $"Voicemails  {presentation.VoicemailsValue}";
        OverviewMediaText.Text = $"Media  {presentation.MediaValue}";
        OverviewHealthParsedText.Text = _importReport is not null
            ? $"✓ Imported {_importReport.RecordsParsed.Messages:N0} messages"
            : _scanReport is not null
                ? $"✓ Scan complete · {_scanReport.FilesScanned:N0} files"
                : "Not scanned";
        OverviewHealthIssuesText.Text = presentation.IssuesHealthSummary;
        OverviewHealthSourceText.Text = GetSourceVerificationText();
        OverviewHealthExportText.Text = _htmlExportSummary is not null
            ? $"✓ Offline HTML archive ready · {_htmlExportSummary.MediaCopied:N0} media copied, {_htmlExportSummary.MediaUnavailable:N0} unavailable"
            : _importReport is not null
                ? "Imported data is ready; HTML archive has not finished building."
                : "Archive not built";
        NextActionText.Text = _sourcePath is null
            ? "Select a Takeout ZIP or extracted folder, then scan it."
            : _scanReport is null
                ? "Scan the selected source. You do not need to choose an output folder yet."
                : _importReport is null
                    ? "Review the inventory, then open Export to choose where the local database and offline archive will be built."
                    : _htmlExportSummary is not null
                        ? "Inspect messages, calls, voicemails, media, and issues from the sidebar, or create the optional CSV export."
                        : "Imported records are available to inspect. Review the build status in Export.";
        OpenExportButton.IsEnabled = _scanReport is not null;
    }

    private string FormatSourceSummary()
    {
        if (_sourcePath is null)
        {
            return "Choose a source to see its type, size, file count, and verification state.";
        }

        var kind = Directory.Exists(_sourcePath) ? "Extracted folder" : "ZIP archive";
        if (_scanReport is null)
        {
            try
            {
                var size = File.Exists(_sourcePath) ? new FileInfo(_sourcePath).Length : (long?)null;
                return size is null ? $"{kind} · not scanned" : $"{kind} · {FormatBytes(size.Value)} on disk · not scanned";
            }
            catch (IOException)
            {
                return $"{kind} · size unavailable · not scanned";
            }
        }

        if (_scanReport.SourceKind == SourceKind.ZipArchive)
        {
            var archiveSize = File.Exists(_sourcePath) ? new FileInfo(_sourcePath).Length : 0;
            return $"ZIP archive · {_scanReport.FilesScanned:N0} files · {FormatBytes(archiveSize)} on disk · about {FormatBytes(_scanReport.TotalBytes)} of file contents";
        }

        return $"Extracted folder · {_scanReport.FilesScanned:N0} files · about {FormatBytes(_scanReport.TotalBytes)} total file contents";
    }

    private static string FormatScanSummary(ScanReport report) =>
        $"{(report.SourceKind == SourceKind.ZipArchive ? "ZIP archive" : "Extracted folder")} · {report.FilesScanned:N0} files · about {FormatBytes(report.TotalBytes)} of file contents\n"
        + $"Message pages: {report.CandidateMessagePages:N0} · Call/event pages: {report.CandidateCallEventPages:N0} · Voicemail pages: {report.CandidateVoicemailPages:N0}\n"
        + $"Image/video media files: {report.CandidateImageVideoMediaFiles:N0} · Audio media files: {report.CandidateAudioMediaFiles:N0} · Other Voice files: {report.OtherVoiceFiles:N0} · Unclassified: {report.UnknownFiles:N0}\n"
        + (report.Warnings.Count == 0 ? "No scan warnings." : $"{report.Warnings.Count:N0} scan warning(s). Exact warning records are listed in Issues.");

    private string GetSourceVerificationText()
    {
        if (_scanReport is null)
        {
            return "Source not verified";
        }

        if (_scanReport.SourceKind == SourceKind.ZipArchive)
        {
            return _importReport?.InputIdentity.SourceSha256 is { Length: > 0 }
                ? "✓ ZIP member CRC checks passed · archive SHA-256 fingerprint recorded"
                : "✓ ZIP member CRC checks passed";
        }

        return "Folder scan completed · no container checksum available";
    }

    private void ConfigureBrowserView()
    {
        _suppressBrowserEvents = true;
        BrowserTitleText.Text = _currentViewName;
        BrowserSearchBox.Text = string.Empty;
        BrowserSearchBox.PlaceholderText = _currentViewName == "Issues" ? "Search issue code, text, or source path" : $"Search {_currentViewName.ToLowerInvariant()}";
        BrowserFilterBox.ItemsSource = _currentViewName switch
        {
            "Messages" => new[] { "All", "With attachments", "Groups" },
            "Media" => new[] { "All", "Image", "Video", "Audio", "Unresolved" },
            _ => new[] { "All" }
        };
        BrowserFilterBox.SelectedIndex = 0;
        BrowserFilterBox.Visibility = _currentViewName == "Voicemails" ? Visibility.Collapsed : Visibility.Visible;
        BrowserSearchBox.Visibility = _currentViewName == "Issues" || (_databasePath is not null && File.Exists(_databasePath)) ? Visibility.Visible : Visibility.Collapsed;
        BrowserLoadMoreButton.Visibility = Visibility.Collapsed;
        BrowserListPanel.Visibility = Visibility.Visible;
        BrowserDetailPanel.Visibility = Visibility.Collapsed;
        BrowserBuildRequiredPanel.Visibility = Visibility.Collapsed;
        BrowserMediaPreviewImage.Visibility = Visibility.Collapsed;
        BrowserMediaPlayer.Visibility = Visibility.Collapsed;
        BrowserDetailLoadMoreButton.Visibility = Visibility.Collapsed;
        BrowserDetailItemsControl.ItemsSource = null;
        BrowserListFooterText.Text = string.Empty;
        _localBrowserItems = [];
        _localBrowserOffset = 0;
        _suppressBrowserEvents = false;
    }

    private void BrowserSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressBrowserEvents || BrowserSearchBox.Visibility != Visibility.Visible)
        {
            return;
        }

        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Start();
    }

    private void BrowserFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressBrowserEvents && ContentBrowserView.Visibility == Visibility.Visible)
        {
            _ = LoadBrowserPageAsync(reset: true);
        }
    }

    private void BrowserLoadMore_Click(object sender, RoutedEventArgs e) => _ = LoadBrowserPageAsync(reset: false);

    private async Task LoadBrowserPageAsync(bool reset)
    {
        if (reset)
        {
            _browserGeneration++;
            _browserItems.Clear();
            _databaseOffset = 0;
            _localBrowserOffset = 0;
            _browserTotalCount = 0;
            _selectedConversationId = null;
            BrowserDetailPanel.Visibility = Visibility.Collapsed;
            BrowserMediaPreviewImage.Visibility = Visibility.Collapsed;
            BrowserMediaPlayer.Visibility = Visibility.Collapsed;
        }

        var generation = _browserGeneration;
        var databasePath = _databasePath;
        var search = BrowserSearchBox.Text;
        var filter = BrowserFilterBox.SelectedItem as string ?? "All";
        var view = _currentViewName;
        var isImported = databasePath is not null && File.Exists(databasePath);
        if (!isImported && view is ("Messages" or "Calls" or "Voicemails" or "Media"))
        {
            BrowserTitleText.Text = view;
            BrowserSummaryText.Text = GetPreBuildSummary(view);
            BrowserListPanel.Visibility = Visibility.Collapsed;
            BrowserDetailPanel.Visibility = Visibility.Collapsed;
            BrowserBuildRequiredPanel.Visibility = Visibility.Visible;
            BrowserBuildRequiredTitleText.Text = GetPreBuildSummary(view);
            BrowserBuildRequiredText.Text = _scanReport is null
                ? "Select and scan a Takeout source first. Scanning inventories it without changing the original archive."
                : "Build the local archive to browse reconstructed records. Choose a destination under Export. This count describes source pages or files, not imported records.";
            BrowserSearchBox.Visibility = Visibility.Collapsed;
            BrowserFilterBox.Visibility = Visibility.Collapsed;
            BrowserLoadMoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        BrowserListPanel.Visibility = Visibility.Visible;
        BrowserBuildRequiredPanel.Visibility = Visibility.Collapsed;
        BrowserSearchBox.Visibility = Visibility.Visible;
        BrowserFilterBox.Visibility = view == "Voicemails" ? Visibility.Collapsed : Visibility.Visible;
        BrowserTitleText.Text = view;
        BrowserDetailPanel.Visibility = Visibility.Visible;
        BrowserDetailTitleText.Text = $"Select a {view switch { "Messages" => "conversation", "Calls" => "call", "Voicemails" => "voicemail", "Media" => "media item", _ => "finding" }}";
        BrowserDetailSummaryText.Text = "Choose an item in the list to inspect its recorded fields and source evidence.";
        BrowserDetailItemsControl.ItemsSource = Array.Empty<WorkspaceEntry>();
        if (view == "Issues")
        {
            _localBrowserItems = BuildLocalIssueItems(search, filter);
        }

        if (!isImported && view == "Issues")
        {
            var localPage = _localBrowserItems.Skip(_localBrowserOffset).Take(BrowserPageSize).ToArray();
            foreach (var item in localPage)
            {
                _browserItems.Add(item);
            }

            _localBrowserOffset += localPage.Length;
            _browserTotalCount = _localBrowserItems.Count;
            BrowserSummaryText.Text = $"{_localBrowserItems.Count:N0} scan or export findings. Exact warning and issue text is preserved.";
            BrowserListFooterText.Text = $"Showing {_browserItems.Count:N0} of {_browserTotalCount:N0}";
            BrowserLoadMoreButton.Visibility = _browserItems.Count < _browserTotalCount ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (!isImported)
        {
            return;
        }

        BrowserLoadMoreButton.IsEnabled = false;
        try
        {
            var localCount = view == "Issues" ? _localBrowserItems.Count : 0;
            var localRemaining = Math.Max(0, localCount - _localBrowserOffset);
            var localTake = Math.Min(BrowserPageSize, localRemaining);
            var remainingPageSize = Math.Max(1, BrowserPageSize - localTake);
            var page = await Task.Run(() => ReadBrowserPage(databasePath!, view, search, filter, _databaseOffset, remainingPageSize));
            if (generation != _browserGeneration || view != _currentViewName)
            {
                return;
            }

            if (page.FilterOptions is not null)
            {
                var filterOptions = page.FilterOptions;
                if (view == "Issues")
                {
                    filterOptions = filterOptions.Concat(_localBrowserItems.Select(GetIssueCode).Where(code => code is not null).Cast<string>())
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (!filterOptions.Contains("All", StringComparer.Ordinal))
                    {
                        filterOptions = new[] { "All" }.Concat(filterOptions).ToArray();
                    }
                }

                _suppressBrowserEvents = true;
                BrowserFilterBox.ItemsSource = filterOptions;
                BrowserFilterBox.SelectedItem = filterOptions.Contains(filter, StringComparer.Ordinal) ? filter : "All";
                _suppressBrowserEvents = false;
                filter = BrowserFilterBox.SelectedItem as string ?? "All";
                if (filter != page.AppliedFilter)
                {
                    _ = LoadBrowserPageAsync(reset: true);
                    return;
                }

                if (view == "Issues")
                {
                    _localBrowserItems = BuildLocalIssueItems(search, filter);
                    localCount = _localBrowserItems.Count;
                    localRemaining = Math.Max(0, localCount - _localBrowserOffset);
                    localTake = Math.Min(BrowserPageSize, localRemaining);
                }
            }

            foreach (var item in _localBrowserItems.Skip(_localBrowserOffset).Take(localTake))
            {
                _browserItems.Add(item);
            }

            _localBrowserOffset += localTake;
            var databaseItemsToAdd = Math.Min(page.Items.Count, BrowserPageSize - localTake);
            foreach (var item in page.Items.Take(databaseItemsToAdd))
            {
                _browserItems.Add(item);
            }

            _databaseOffset += databaseItemsToAdd;
            _browserTotalCount = page.DatabaseTotalCount + localCount;
            BrowserSummaryText.Text = view == "Media" && page.MediaSummary is { } mediaSummary
                ? MediaBrowserSummaryPresentation.Format(mediaSummary.ReferenceCount, mediaSummary.SourceMediaFileCount)
                : $"{_browserTotalCount:N0} {GetRecordNoun(view)} · search and filters run locally against the imported database.";
            BrowserListFooterText.Text = $"Showing {_browserItems.Count:N0} of {_browserTotalCount:N0}";
            BrowserLoadMoreButton.Visibility = _localBrowserOffset < localCount || _databaseOffset < page.DatabaseTotalCount
                ? Visibility.Visible : Visibility.Collapsed;
            if (_browserItems.Count == 0)
            {
                BrowserDetailPanel.Visibility = Visibility.Visible;
                BrowserDetailTitleText.Text = "No matching records";
                BrowserDetailSummaryText.Text = "Try a different search or filter.";
                BrowserDetailItemsControl.ItemsSource = Array.Empty<WorkspaceEntry>();
            }
        }
        catch (Exception exception)
        {
            if (generation == _browserGeneration)
            {
                BrowserDetailPanel.Visibility = Visibility.Visible;
                BrowserDetailTitleText.Text = "Records could not be loaded";
                BrowserDetailSummaryText.Text = GetUserMessage(exception, "VoiceBridge couldn't read this page from the local archive.");
            }
        }
        finally
        {
            if (generation == _browserGeneration)
            {
                BrowserLoadMoreButton.IsEnabled = true;
            }
        }
    }

    private static BrowserPageResult ReadBrowserPage(string databasePath, string view, string? search, string filter, int offset, int pageSize)
    {
        using var reader = new SqliteArchiveReader(databasePath);
        switch (view)
        {
            case "Messages":
            {
                var page = reader.ReadConversationPage(search, filter, offset, pageSize);
                return new BrowserPageResult(page.Items.Select(CreateConversationBrowserItem).ToArray(), page.TotalCount, page.TotalCount, page.Items.Count, filter, null);
            }
            case "Calls":
            {
                var filters = new[] { "All" }.Concat(reader.ReadCallEventTypes()).ToArray();
                var appliedFilter = filters.Contains(filter, StringComparer.Ordinal) ? filter : "All";
                var page = reader.ReadCallPage(search, appliedFilter, offset, pageSize);
                return new BrowserPageResult(page.Items.Select(CreateCallBrowserItem).ToArray(), page.TotalCount, page.TotalCount, page.Items.Count, appliedFilter, filters);
            }
            case "Voicemails":
            {
                var page = reader.ReadVoicemailPage(search, offset, pageSize);
                return new BrowserPageResult(page.Items.Select(CreateVoicemailBrowserItem).ToArray(), page.TotalCount, page.TotalCount, page.Items.Count, "All", null);
            }
            case "Media":
            {
                var page = reader.ReadMediaPage(search, filter, offset, pageSize);
                var summary = reader.ReadMediaBrowserSummary();
                return new BrowserPageResult(page.Items.Select(CreateMediaBrowserItem).ToArray(), page.TotalCount, page.TotalCount, page.Items.Count, filter, null, summary);
            }
            case "Issues":
            {
                var filters = new[] { "All" }.Concat(reader.ReadImportIssueCodes()).Distinct(StringComparer.Ordinal).ToArray();
                var appliedFilter = filters.Contains(filter, StringComparer.Ordinal) ? filter : "All";
                var page = reader.ReadImportIssuePage(search, appliedFilter, offset, pageSize);
                return new BrowserPageResult(page.Items.Select(CreateIssueBrowserItem).ToArray(), page.TotalCount, page.TotalCount, page.Items.Count, appliedFilter, filters);
            }
            default:
                return new BrowserPageResult([], 0, 0, 0, filter, null);
        }
    }

    private string GetPreBuildSummary(string view)
    {
        if (_scanReport is null)
        {
            return "Select and scan a Takeout archive";
        }

        return view switch
        {
            "Messages" => $"{_scanReport.CandidateMessagePages:N0} message pages discovered",
            "Calls" => $"{_scanReport.CandidateCallEventPages:N0} call/event pages discovered",
            "Voicemails" => $"{_scanReport.CandidateVoicemailPages:N0} voicemail pages discovered",
            "Media" => $"{_scanReport.CandidateImageVideoMediaFiles + _scanReport.CandidateAudioMediaFiles:N0} media files discovered",
            _ => "Scan the source archive to see discovered content"
        };
    }

    private static string GetRecordNoun(string view) => view switch
    {
        "Messages" => "conversations",
        "Calls" => "calls",
        "Voicemails" => "voicemails",
        "Media" => "media records",
        "Issues" => "findings",
        _ => "records"
    };

    private List<BrowserItem> BuildLocalIssueItems(string? search, string filter)
    {
        var entries = new List<WorkspaceEntry>();
        if (_scanReport is not null)
        {
            entries.AddRange(_scanReport.Warnings.Select(warning => new WorkspaceEntry(
                $"Scan warning · {warning.Code}", warning.RelativePath is null ? "Scan" : $"Source: {warning.RelativePath}", warning.Message,
                FormatProvenance(warning.RelativePath, null))));
        }

        entries.AddRange(_exportIssues);
        var filtered = entries.Where(entry =>
            (filter == "All" || entry.Heading.EndsWith($"· {filter}", StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(search) || string.Join("\n", entry.Heading, entry.Metadata, entry.Body, entry.Evidence).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)));
        return filtered.Select((entry, index) => new BrowserItem("local_issue", -(index + 1), entry.Heading, entry.Metadata, entry.Body, entry.Evidence, entry)).ToList();
    }

    private static BrowserItem CreateConversationBrowserItem(StoredConversationBrowserRow conversation) => new(
        "conversation", conversation.Id,
        string.IsNullOrWhiteSpace(conversation.RawLabel) ? $"Conversation {conversation.Id}" : conversation.RawLabel,
        $"{conversation.Kind} · {conversation.MessageCount:N0} messages · last {ShowRaw(conversation.LastTimestampUtc)}",
        string.IsNullOrWhiteSpace(conversation.Preview) ? "No message preview recorded." : conversation.Preview,
        $"Participants: {ShowRaw(conversation.ParticipantSummary)}\nSource: {conversation.SourceRelativePath}", conversation);

    private static BrowserItem CreateCallBrowserItem(StoredCallRecord call)
    {
        var label = FirstRecorded(call.RawContact, call.RawFilenameContact, call.RawPhoneNumber) ?? $"Call record {call.Id}";
        return new BrowserItem("call", call.Id, label,
            $"{ShowRaw(call.RawEventType)} · {ShowRaw(call.TimestampUtc ?? call.RawTimestamp)} · {ShowRaw(call.DurationDisplayText)}",
            $"Phone: {ShowRaw(call.RawPhoneNumber)}", $"Source: {call.SourceRelativePath}", call);
    }

    private static BrowserItem CreateVoicemailBrowserItem(StoredVoicemail voicemail)
    {
        var label = FirstRecorded(voicemail.RawContact, voicemail.RawFilenameContact, voicemail.RawPhoneNumber) ?? $"Voicemail {voicemail.Id}";
        var preview = string.IsNullOrWhiteSpace(voicemail.Transcript) ? "No transcript available." : voicemail.Transcript;
        return new BrowserItem("voicemail", voicemail.Id, label,
            $"{ShowRaw(voicemail.TimestampUtc ?? voicemail.RawTimestamp)} · Audio: {voicemail.AudioMatchStatus}",
            preview, $"Source: {voicemail.SourceRelativePath}", voicemail);
    }

    private static BrowserItem CreateMediaBrowserItem(StoredMediaBrowserItem media)
    {
        var heading = media.RecordType == "source_file"
            ? media.MatchedRelativePath ?? $"Source media file {media.RecordId}"
            : media.RawReference ?? $"{media.RecordType} {media.RecordId}";
        var status = media.RecordType == "source_file" ? "Source file" : media.MatchStatus;
        return new BrowserItem("media", media.RecordId,
            heading,
            $"{media.RecordType} · {ShowRaw(media.MediaType)} · {status}{(media.SizeBytes is null ? string.Empty : $" · {FormatBytes(media.SizeBytes.Value)}")}",
            media.MatchedRelativePath ?? media.SourceRelativePath ?? "Path not recorded",
            media.SourceRelativePath is null ? "Source path not recorded" : $"Source: {media.SourceRelativePath}", media);
    }

    private static BrowserItem CreateIssueBrowserItem(StoredImportIssue issue) => new(
        "issue", issue.Id, $"{issue.Severity} · {issue.Code}",
        $"Issue ID: {issue.Id} · Source file ID: {ShowRaw(issue.SourceFileId?.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
        issue.Message,
        FormatProvenance(issue.SourceRelativePath, issue.SourceRowIndex), issue);

    private static string? FirstRecorded(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? GetIssueCode(BrowserItem item)
    {
        const string scanPrefix = "Scan warning · ";
        if (item.Heading.StartsWith(scanPrefix, StringComparison.Ordinal))
        {
            return item.Heading[scanPrefix.Length..];
        }

        return item.Data is StoredImportIssue issue ? issue.Code : null;
    }

    private async void BrowserList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrowserListView.SelectedItem is not BrowserItem item)
        {
            return;
        }

        var selection = ++_detailGeneration;
        BrowserDetailPanel.Visibility = Visibility.Visible;
        BrowserDetailLoadMoreButton.Visibility = Visibility.Collapsed;
        BrowserMediaPreviewImage.Visibility = Visibility.Collapsed;
        BrowserMediaPlayer.Source = null;
        BrowserMediaPlayer.Visibility = Visibility.Collapsed;
        BrowserDetailTitleText.Text = item.Heading;
        BrowserDetailSummaryText.Text = item.Metadata;

        switch (item.RecordType)
        {
            case "conversation" when item.Data is StoredConversationBrowserRow conversation:
                await ShowConversationDetailsAsync(conversation, selection);
                break;
            case "call" when item.Data is StoredCallRecord call:
                BrowserDetailItemsControl.ItemsSource = new[] { CreateCallEntry(call) };
                break;
            case "voicemail" when item.Data is StoredVoicemail voicemail:
                BrowserDetailItemsControl.ItemsSource = new[] { CreateVoicemailEntry(voicemail) };
                if (voicemail.MatchedAudioSourceFileId is long voicemailAudioId)
                {
                    await ShowMediaPreviewAsync(new StoredMediaBrowserItem(
                        "voicemail_audio", voicemail.Id, voicemail.Id, voicemail.AudioReference,
                        voicemailAudioId, voicemail.MatchedAudioRelativePath, "audio", voicemail.AudioMatchStatus,
                        voicemail.SourceRelativePath, null, null));
                }
                break;
            case "media" when item.Data is StoredMediaBrowserItem media:
                BrowserDetailItemsControl.ItemsSource = new[] { CreateMediaDetail(media) };
                await ShowMediaPreviewAsync(media);
                break;
            case "issue" when item.Data is StoredImportIssue issue:
                BrowserDetailItemsControl.ItemsSource = new[] { CreateIssueDetail(issue) };
                break;
            case "local_issue" when item.Data is WorkspaceEntry localIssue:
                BrowserDetailItemsControl.ItemsSource = new[] { localIssue };
                break;
            default:
                BrowserDetailItemsControl.ItemsSource = Array.Empty<WorkspaceEntry>();
                break;
        }
    }

    private async Task ShowConversationDetailsAsync(StoredConversationBrowserRow conversation, long selection)
    {
        if (_databasePath is null)
        {
            return;
        }

        _selectedConversationId = conversation.Id;
        _detailMessageOffset = 0;
        _selectedMessageEntries = [];
        var databasePath = _databasePath;
        var details = await Task.Run(() =>
        {
            using var reader = new SqliteArchiveReader(databasePath);
            return (Participants: reader.ReadParticipants(conversation.Id), Messages: reader.ReadConversationMessagesPage(conversation.Id, 0, MessagePageSize));
        });
        if (selection != _detailGeneration || _selectedConversationId != conversation.Id)
        {
            return;
        }

        var participantEntries = details.Participants.Select(participant => new WorkspaceEntry(
            FirstRecorded(participant.NormalizedDisplayName, participant.NormalizedPhoneNumber) ?? "Participant not recorded",
            $"Display name: {ShowRaw(participant.NormalizedDisplayName)} · Phone: {ShowRaw(participant.NormalizedPhoneNumber)}",
            participant.Evidence.Count == 0 ? "No participant evidence rows recorded." : string.Join("\n", participant.Evidence.Select(evidence =>
                $"{evidence.SourceType} · row {ShowRaw(evidence.SourceRowIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture))} · name {ShowRaw(evidence.RawDisplayName)} · phone {ShowRaw(evidence.RawPhoneNumber)}")),
            $"Participant ID: {participant.Id}"));
        _selectedMessageEntries.Add(new WorkspaceEntry(
            "Conversation details",
            $"Kind: {conversation.Kind} · Messages: {conversation.MessageCount:N0}\nFirst timestamp: {ShowRaw(conversation.FirstTimestampUtc)} · Last timestamp: {ShowRaw(conversation.LastTimestampUtc)}",
            $"Source page: {conversation.SourceRelativePath}\nParticipants: {ShowRaw(conversation.ParticipantSummary)}",
            "Conversation IDs and participants remain separate as recorded by the import."));
        _selectedMessageEntries.AddRange(participantEntries);
        _selectedMessageEntries.AddRange(details.Messages.Items.Select(CreateMessageEntry));
        _detailMessageOffset = details.Messages.Items.Count;
        BrowserDetailItemsControl.ItemsSource = _selectedMessageEntries.ToArray();
        BrowserDetailLoadMoreButton.Visibility = _detailMessageOffset < details.Messages.TotalCount ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowserDetailLoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedConversationId is not long conversationId || _databasePath is null)
        {
            return;
        }

        BrowserDetailLoadMoreButton.IsEnabled = false;
        var selection = _detailGeneration;
        try
        {
            var databasePath = _databasePath;
            var page = await Task.Run(() =>
            {
                using var reader = new SqliteArchiveReader(databasePath);
                return reader.ReadConversationMessagesPage(conversationId, _detailMessageOffset, MessagePageSize);
            });
            if (selection != _detailGeneration || _selectedConversationId != conversationId)
            {
                return;
            }

            _selectedMessageEntries.AddRange(page.Items.Select(CreateMessageEntry));
            _detailMessageOffset += page.Items.Count;
            BrowserDetailItemsControl.ItemsSource = _selectedMessageEntries.ToArray();
            BrowserDetailLoadMoreButton.Visibility = _detailMessageOffset < page.TotalCount ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            SetStatus(GetUserMessage(exception, "VoiceBridge couldn't load more messages for this conversation."), isError: true);
        }
        finally
        {
            BrowserDetailLoadMoreButton.IsEnabled = true;
        }
    }

    private static WorkspaceEntry CreateMediaDetail(StoredMediaBrowserItem media) => new(
        media.RecordType == "source_file" ? "Source media file" : media.RecordType,
        $"Media type: {ShowRaw(media.MediaType)} · Match status: {media.MatchStatus}\nRaw reference: {ShowRaw(media.RawReference)}",
        $"Matched source path: {ShowRaw(media.MatchedRelativePath)}\nSource record path: {ShowRaw(media.SourceRelativePath)}\nSize: {(media.SizeBytes is null ? "(not recorded)" : FormatBytes(media.SizeBytes.Value))}",
        $"Record ID: {media.RecordId} · Parent record ID: {ShowRaw(media.ParentRecordId?.ToString(System.Globalization.CultureInfo.InvariantCulture))} · Matched source file ID: {ShowRaw(media.MatchedSourceFileId?.ToString(System.Globalization.CultureInfo.InvariantCulture))}\nSHA-256: {ShowRaw(media.ContentSha256)}");

    private static WorkspaceEntry CreateIssueDetail(StoredImportIssue issue) => new(
        $"{issue.Severity} · {issue.Code}",
        $"Issue ID: {issue.Id} · Source file ID: {ShowRaw(issue.SourceFileId?.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
        issue.Message,
        FormatProvenance(issue.SourceRelativePath, issue.SourceRowIndex));

    private async Task ShowMediaPreviewAsync(StoredMediaBrowserItem media)
    {
        var sourceFileId = media.RecordType == "source_file" ? media.RecordId : media.MatchedSourceFileId;
        if (sourceFileId is null || _outputDirectory is null)
        {
            return;
        }

        var assetsDirectory = Path.Combine(_outputDirectory, "archive", "assets");
        if (!Directory.Exists(assetsDirectory))
        {
            return;
        }

        var exactStem = $"sourcefile-{sourceFileId.Value}";
        var localPath = Directory.EnumerateFiles(assetsDirectory, exactStem + ".*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), exactStem, StringComparison.Ordinal));
        if (localPath is null || !File.Exists(localPath))
        {
            BrowserDetailSummaryText.Text += "\nNo exported local media preview is available for this source file.";
            return;
        }

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(localPath);
            if (string.Equals(media.MediaType, "image", StringComparison.OrdinalIgnoreCase))
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                await bitmap.SetSourceAsync(stream);
                BrowserMediaPreviewImage.Source = bitmap;
                BrowserMediaPreviewImage.Visibility = Visibility.Visible;
            }
            else if (string.Equals(media.MediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(media.MediaType, "video", StringComparison.OrdinalIgnoreCase))
            {
                BrowserMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
                BrowserMediaPlayer.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            BrowserDetailSummaryText.Text += $"\nLocal preview unavailable: {exception.Message}";
        }
    }

    private void RefreshExportView()
    {
        ExportSourceSummaryText.Text = _scanReport is null || _sourcePath is null
            ? "Select and scan a Takeout source in Overview first."
            : $"Source: {_sourcePath}\n{FormatSourceSummary()}\nVerification: {GetSourceVerificationText()}";
        OutputPathText.Text = _outputDirectory is null ? "No destination selected" : FormatPathForDisplay(_outputDirectory);
        ToolTipService.SetToolTip(OutputPathText, _outputDirectory ?? "No destination selected");

        if (_htmlExportSummary is not null && _outputDirectory is not null)
        {
            BuildResultText.Text = $"Database: {Path.Combine(_outputDirectory, "voicebridge.db")}\n"
                + $"Import report: {Path.Combine(_outputDirectory, "import-report.json")}\n"
                + $"Offline HTML archive: {Path.Combine(_outputDirectory, "archive")}\n"
                + $"{_htmlExportSummary.Conversations:N0} conversations · {_htmlExportSummary.Messages:N0} messages · {_htmlExportSummary.Calls:N0} calls · {_htmlExportSummary.Voicemails:N0} voicemails · {_htmlExportSummary.MediaCopied:N0} media copied · {_htmlExportSummary.MediaUnavailable:N0} unavailable";
        }
        else if (_importReport is not null && _outputDirectory is not null)
        {
            BuildResultText.Text = $"Import completed; the HTML archive did not finish. Database: {Path.Combine(_outputDirectory, "voicebridge.db")} · Import report: {Path.Combine(_outputDirectory, "import-report.json")}";
        }
        else if (_scanReport is not null)
        {
            BuildResultText.Text = "No files are written during scanning. Choose a destination and build when ready.";
        }
        else
        {
            BuildResultText.Text = "Select and scan a source before building.";
        }

        CsvResultText.Text = _csvExportSummary is not null && _csvExportDirectory is not null
            ? $"CSV tables and export-report.json created in {_csvExportDirectory} · {_csvExportSummary.MediaUnavailable:N0} unavailable media references."
            : _databasePath is null || _outputDirectory is null
                ? "Build the local archive first."
                : $"Will create {Path.Combine(_outputDirectory, "csv-export")} with relational CSV tables and export-report.json.";
    }

    private void RefreshControls()
    {
        var busy = _operationCancellation is not null;
        MessagesNavigationItem.IsEnabled = true;
        CallsNavigationItem.IsEnabled = true;
        VoicemailsNavigationItem.IsEnabled = true;
        MediaNavigationItem.IsEnabled = true;
        IssuesNavigationItem.Visibility = _scanReport is null ? Visibility.Collapsed : Visibility.Visible;
        ExportNavigationItem.Visibility = _scanReport is null ? Visibility.Collapsed : Visibility.Visible;
        SelectZipButton.IsEnabled = !busy;
        SelectFolderButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy && _sourcePath is not null;
        SelectOutputButton.IsEnabled = !busy && _scanReport is not null;
        BuildArchiveButton.IsEnabled = !busy && _scanReport is not null && _outputDirectory is not null && _importReport is null;
        ExportCsvButton.IsEnabled = !busy && _databasePath is not null && _outputDirectory is not null
            && !Directory.Exists(Path.Combine(_outputDirectory, "csv-export"))
            && !File.Exists(Path.Combine(_outputDirectory, "csv-export"));
        OpenArchiveButton.IsEnabled = !busy && _archiveIndexPath is not null && File.Exists(_archiveIndexPath);
        OpenOutputButton.IsEnabled = !busy && _outputDirectory is not null && Directory.Exists(_outputDirectory);
        OpenCsvOutputButton.IsEnabled = !busy && _csvExportDirectory is not null && Directory.Exists(_csvExportDirectory);
    }

    private WorkspaceStatusPresentation GetWorkspaceStatusPresentation() =>
        WorkspaceStatusPresentation.Create(_scanReport, _importReport, _exportIssues.Count, _htmlExportSummary is not null);

    private void BrowserPaneGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyBrowserListWidth(_browserListWidth, e.NewSize.Width);

    private void BrowserPaneSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizingBrowserPane = true;
        _browserResizeStartX = e.GetCurrentPoint(BrowserPaneGrid).Position.X;
        _browserResizeStartWidth = BrowserListColumn.ActualWidth;
        BrowserPaneSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void BrowserPaneSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingBrowserPane)
        {
            return;
        }

        var currentX = e.GetCurrentPoint(BrowserPaneGrid).Position.X;
        ApplyBrowserListWidth(_browserResizeStartWidth + currentX - _browserResizeStartX, BrowserPaneGrid.ActualWidth);
        e.Handled = true;
    }

    private void BrowserPaneSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isResizingBrowserPane = false;
        BrowserPaneSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void BrowserPaneSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        _isResizingBrowserPane = false;

    private void BrowserPaneSplitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var direction = e.Key switch
        {
            Windows.System.VirtualKey.Left => -1,
            Windows.System.VirtualKey.Right => 1,
            _ => 0
        };
        if (direction == 0)
        {
            return;
        }

        ApplyBrowserListWidth(
            BrowserPaneSizing.AdjustListWidth(_browserListWidth, BrowserPaneGrid.ActualWidth, direction),
            BrowserPaneGrid.ActualWidth);
        e.Handled = true;
    }

    private void ApplyBrowserListWidth(double requestedWidth, double availableWidth)
    {
        _browserListWidth = BrowserPaneSizing.ClampListWidth(requestedWidth, availableWidth);
        BrowserListColumn.Width = new GridLength(_browserListWidth);
    }

    private void SetSource(string sourcePath)
    {
        _sourcePath = Path.GetFullPath(sourcePath);
        WorkspaceNavigation.SelectedItem = OverviewNavigationItem;
        _scanReport = null;
        _outputDirectory = null;
        ClearImportedState();
        UpdateCrashLogDirectory();
        SetStatus("Source selected. Scan it now; choose a destination later from Export.");
        RefreshWorkspace();
    }

    private void ClearImportedState()
    {
        _databasePath = null;
        _archiveIndexPath = null;
        _csvExportDirectory = null;
        _importReport = null;
        _htmlExportSummary = null;
        _csvExportSummary = null;
        _exportIssues.Clear();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = (isError ? "Issue: " : string.Empty) + message;
    }

    private void UpdateCrashLogDirectory()
    {
        if (Application.Current is App app)
        {
            app.CrashLogDirectory = CrashLogPolicy.Resolve(_sourcePath, _outputDirectory);
        }
    }

    private static WorkspaceEntry CreateMessageEntry(StoredMessage message)
    {
        var sender = string.Join(" · ", new[] { message.SenderDisplayName, message.SenderPhoneNumber }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var attachmentDetails = message.Attachments.Count == 0
            ? "No attachment references recorded."
            : "Attachment references:\n" + string.Join("\n", message.Attachments.Select(attachment =>
                $"{ShowRaw(attachment.RawReference)} · {ShowRaw(attachment.MediaType)} · {(attachment.MatchedSourceFileId is null ? "unresolved" : $"matched: {ShowRaw(attachment.MatchedRelativePath)}")}"));
        return new WorkspaceEntry(
            string.IsNullOrWhiteSpace(sender) ? "Sender not recorded" : sender,
            $"Raw timestamp: {ShowRaw(message.RawTimestamp)} · Parsed UTC: {ShowRaw(message.TimestampUtc)} · Direction: {(message.Direction is null ? "not determined" : message.Direction)}",
            message.Body is null ? "Message body not recorded." : message.Body,
            $"Source: {FormatProvenance(message.SourceRelativePath, message.SourceRowIndex)}\n{attachmentDetails}");
    }

    private static WorkspaceEntry CreateCallEntry(StoredCallRecord call)
    {
        var metadata = $"Raw event: {ShowRaw(call.RawEventType)} · Raw timestamp: {ShowRaw(call.RawTimestamp)} · Parsed UTC: {ShowRaw(call.TimestampUtc)}\n"
            + $"Contact: {ShowRaw(call.RawContact)} · Filename contact: {ShowRaw(call.RawFilenameContact)} · Contact source: {ShowRaw(call.RawContactSource)}\n"
            + $"Phone: {ShowRaw(call.RawPhoneNumber)} · Duration: {ShowRaw(call.DurationDisplayText)} · Duration seconds: {ShowRaw(call.DurationSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        var media = call.MediaReferences.Count == 0
            ? "No call media references recorded."
            : "Media references:\n" + string.Join("\n", call.MediaReferences.Select(FormatMediaReference));
        return new WorkspaceEntry($"Call record {call.Id}", metadata, media, $"Source: {call.SourceRelativePath}");
    }

    private static WorkspaceEntry CreateVoicemailEntry(StoredVoicemail voicemail)
    {
        var metadata = $"Raw timestamp: {ShowRaw(voicemail.RawTimestamp)} · Parsed UTC: {ShowRaw(voicemail.TimestampUtc)}\n"
            + $"Contact: {ShowRaw(voicemail.RawContact)} · Filename contact: {ShowRaw(voicemail.RawFilenameContact)} · Contact source: {ShowRaw(voicemail.RawContactSource)}\n"
            + $"Phone: {ShowRaw(voicemail.RawPhoneNumber)} · Duration: {ShowRaw(voicemail.DurationDisplayText)} · Duration seconds: {ShowRaw(voicemail.DurationSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        var audio = $"Audio match status: {voicemail.AudioMatchStatus}\nAudio reference: {ShowRaw(voicemail.AudioReference)}\nMatched audio path: {ShowRaw(voicemail.MatchedAudioRelativePath)}";
        var media = voicemail.MediaReferences.Count == 0
            ? audio
            : audio + "\nOther media references:\n" + string.Join("\n", voicemail.MediaReferences.Select(FormatMediaReference));
        return new WorkspaceEntry(
            $"Voicemail record {voicemail.Id}",
            metadata,
            string.IsNullOrWhiteSpace(voicemail.Transcript) ? "No transcript available." : $"Transcript: {voicemail.Transcript}",
            $"Source: {voicemail.SourceRelativePath}\n{media}");
    }

    private static WorkspaceEntry CreateMediaReferenceEntry(string title, string sourcePath, StoredMediaReference reference) => new(
        title,
        $"Match status: {reference.MatchStatus} · Media type: {ShowRaw(reference.MediaType)} · Matched path: {ShowRaw(reference.MatchedRelativePath)}",
        $"Raw reference: {reference.RawReference}",
        $"Source: {sourcePath}");

    private static string FormatMediaReference(StoredMediaReference reference) =>
        $"{reference.RawReference} · {reference.MatchStatus} · type {ShowRaw(reference.MediaType)} · matched path {ShowRaw(reference.MatchedRelativePath)}";

    private static IReadOnlyList<WorkspaceEntry> ReadUnavailableMediaIssues(string reportPath, string exportName)
    {
        if (!File.Exists(reportPath))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        if (!document.RootElement.TryGetProperty("unavailableMedia", out var unavailable) || unavailable.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return unavailable.EnumerateArray().Select(item =>
        {
            var id = item.TryGetProperty("SourceFileId", out var upperId) ? upperId.GetInt64()
                : item.TryGetProperty("sourceFileId", out var lowerId) ? lowerId.GetInt64() : 0;
            var path = item.TryGetProperty("RelativePath", out var upperPath) ? upperPath.GetString()
                : item.TryGetProperty("relativePath", out var lowerPath) ? lowerPath.GetString() : null;
            var reason = item.TryGetProperty("Reason", out var upperReason) ? upperReason.GetString()
                : item.TryGetProperty("reason", out var lowerReason) ? lowerReason.GetString() : null;
            return new WorkspaceEntry(
                $"{exportName} · unavailable media · source file {id}",
                "Export warning",
                $"Path: {ShowRaw(path)}\nReason: {ShowRaw(reason)}",
                $"Exact details are retained in {reportPath}");
        }).ToArray();
    }

    private static WorkspaceEntry CreateOperationIssue(string title, Exception exception) => new(
        title,
        exception.GetType().FullName ?? exception.GetType().Name,
        exception.Message,
        exception.StackTrace ?? string.Empty);

    private static string FormatProvenance(string? sourcePath, int? rowIndex) =>
        $"Source: {ShowRaw(sourcePath)} · Row: {(rowIndex is null ? "not recorded" : rowIndex.Value.ToString("N0"))}";

    private static string ShowRaw(string? value) => value switch
    {
        null => "(not recorded)",
        "" => "(empty)",
        _ => value
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KiB", "MiB", "GiB", "TiB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} bytes" : $"{size:N1} {units[unit]}";
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e) => await OpenPathAsync(_archiveIndexPath, "The offline archive isn't available yet.");
    private async void OpenOutput_Click(object sender, RoutedEventArgs e) => await OpenPathAsync(_outputDirectory, "Choose a destination folder first.");
    private async void OpenCsvOutput_Click(object sender, RoutedEventArgs e) => await OpenPathAsync(_csvExportDirectory, "The CSV export folder isn't available yet.");

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
        _operationCancellation.Cancel();
    }

    private CancellationTokenSource BeginOperation(string initialStatus)
    {
        _operationCancellation = new CancellationTokenSource();
        StatusText.Text = initialStatus;
        OperationProgressBar.Visibility = Visibility.Visible;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.Value = 0;
        CancelButton.Visibility = Visibility.Visible;
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
        OperationProgressBar.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        RefreshWorkspace();
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

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var about = new StackPanel { Spacing = 10 };
        about.Children.Add(new TextBlock { Text = $"VoiceBridge {version}", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        about.Children.Add(new TextBlock { Text = "Built by 404 Builds.", TextWrapping = TextWrapping.Wrap });
        about.Children.Add(new TextBlock { Text = "VoiceBridge reads the Google Voice Takeout you select and creates a local database, import report, and offline HTML archive in the destination you choose.", TextWrapping = TextWrapping.Wrap });
        about.Children.Add(new TextBlock { Text = "No account is required. VoiceBridge does not add telemetry or upload your Takeout.", TextWrapping = TextWrapping.Wrap });
        await ShowDialogAsync("About VoiceBridge", about);
    }

    private async void Privacy_Click(object sender, RoutedEventArgs e)
    {
        var privacy = new TextBlock
        {
            Text = "VoiceBridge processes the source you select on this PC. It does not use accounts, cloud processing, or telemetry. Your Takeout ZIP or extracted source is read without being changed. The database, import report, and offline archive are written only to the destination folder you choose.\n\n"
                + "If VoiceBridge encounters an unhandled application failure after you choose a safe output folder, it may save a local crash log there. The log can contain exception details and local file paths. It is never sent automatically; review it before choosing to share it. No crash log is created before a safe output folder is selected.\n\n"
                + "Removing the VoiceBridge application folder does not delete your selected output folder or crash logs. You control those files.",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        await ShowDialogAsync("VoiceBridge privacy", new ScrollViewer { MaxHeight = 480, Content = privacy });
    }

    private async Task ShowDialogAsync(string title, object content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static string FormatPathForDisplay(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root is not null && trimmedPath.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        var leaf = Path.GetFileName(trimmedPath);
        var parent = Path.GetFileName(Path.GetDirectoryName(trimmedPath));
        return string.IsNullOrWhiteSpace(parent)
            ? leaf
            : $"…{Path.DirectorySeparatorChar}{parent}{Path.DirectorySeparatorChar}{leaf}";
    }

    private static string GetUserMessage(Exception exception, string fallback) => exception switch
    {
        UnauthorizedAccessException => "VoiceBridge doesn't have permission to read the source or write to the destination. Choose locations you can access.",
        PathTooLongException => "The selected source or destination path is too long. Choose a shorter path and try again.",
        DirectoryNotFoundException => "A selected folder is no longer available. Choose the source and destination again, then retry.",
        FileNotFoundException => "A selected file is no longer available. Choose the source again, then retry.",
        IOException => exception.Message,
        InvalidDataException => exception.Message,
        ArgumentException => exception.Message,
        NotSupportedException => "This source or destination type isn't supported. Choose a local ZIP file, extracted folder, and writable destination.",
        _ => fallback
    };

    private sealed record BrowserPageResult(
        IReadOnlyList<BrowserItem> Items,
        long TotalCount,
        long DatabaseTotalCount,
        int DatabaseItemCount,
        string AppliedFilter,
        IReadOnlyList<string>? FilterOptions,
        StoredMediaBrowserSummary? MediaSummary = null);
}

public sealed record WorkspaceEntry(string Heading, string Metadata, string Body, string Evidence);
public sealed record BrowserItem(string RecordType, long Id, string Heading, string Metadata, string Body, string Evidence, object? Data);
