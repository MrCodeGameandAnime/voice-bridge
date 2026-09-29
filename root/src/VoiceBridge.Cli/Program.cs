using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceBridge.Core.Importing;
using VoiceBridge.Core.Scanning;
using VoiceBridge.Export;
using VoiceBridge.Storage;

namespace VoiceBridge.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(CliConfiguration.Load().MinimumLogLevel)
                    .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            });

            var logger = loggerFactory.CreateLogger("VoiceBridge.Cli");
            return Run(args, cancellation.Token, logger);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Import failed: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int Run(string[] args, CancellationToken cancellationToken, ILogger logger)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            WriteHelp(Console.Out);
            return 0;
        }

        if (args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            return RunScan(args, cancellationToken, logger);
        }

        if (args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            return RunImport(args, cancellationToken, logger);
        }

        if (args[0].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            return RunExport(args, cancellationToken, logger);
        }

        logger.LogError("This command is not available yet. Run 'voicebridge --help' for usage.");
        return 2;
    }

    private static int RunImport(string[] args, CancellationToken cancellationToken, ILogger logger)
    {
        if (args.Length != 4 || !args[2].Equals("--output", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Usage: voicebridge import <source> --output <directory>");
            return 2;
        }

        var sourcePath = Path.GetFullPath(args[1]);
        var outputDirectory = Path.GetFullPath(args[3]);
        SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(sourcePath, outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var databasePath = Path.Combine(outputDirectory, "voicebridge.db");
        var reportPath = Path.Combine(outputDirectory, "import-report.json");
        if (File.Exists(databasePath) || File.Exists(reportPath))
        {
            logger.LogError("An import database or report already exists in the output directory. Choose a new output directory.");
            return 1;
        }

        var service = new TakeoutImportService(new SqliteTakeoutImportStoreFactory());
        service.ProgressChanged += (_, progress) =>
        {
            if (progress.Stage == ImportProgressStage.ProcessingMessages)
            {
                var processed = progress.Completed + 1;
                if (progress.Completed == 0 || processed % 500 == 0 || processed == progress.Total)
                {
                    Console.Out.WriteLine(progress.Message);
                }
            }
            else
            {
                Console.Out.WriteLine($"{progress.Message}...");
            }
        };

        var report = service.ImportAsync(sourcePath, databasePath, cancellationToken).GetAwaiter().GetResult();
        using (var reportStream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(reportStream, report, JsonOptions);
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine("Import complete.");
        WriteCount(Console.Out, "Conversations", report.RecordsParsed.Conversations);
        WriteCount(Console.Out, "Messages", report.RecordsParsed.Messages);
        WriteCount(Console.Out, "Attachments", report.RecordsParsed.Attachments);
        WriteCount(Console.Out, "Warnings", report.Warnings);
        WriteCount(Console.Out, "Errors", report.Errors);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"Database: {databasePath}");
        Console.Out.WriteLine($"Report:   {reportPath}");
        return 0;
    }

    private static int RunExport(string[] args, CancellationToken cancellationToken, ILogger logger)
    {
        if (args.Length != 5 || !args[3].Equals("--output", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Usage: voicebridge export <html|csv> <database> --output <directory>");
            return 2;
        }

        var databasePath = Path.GetFullPath(args[2]);
        var outputDirectory = Path.GetFullPath(args[4]);
        var summary = args[1].ToLowerInvariant() switch
        {
            "html" => TakeoutArchiveExporter.ExportHtmlAsync(databasePath, outputDirectory, cancellationToken).GetAwaiter().GetResult(),
            "csv" => TakeoutArchiveExporter.ExportCsvAsync(databasePath, outputDirectory, cancellationToken).GetAwaiter().GetResult(),
            _ => null
        };

        if (summary is null)
        {
            logger.LogError("Export format must be 'html' or 'csv'. Usage: voicebridge export <html|csv> <database> --output <directory>");
            return 2;
        }

        var format = args[1].Equals("html", StringComparison.OrdinalIgnoreCase) ? "HTML archive" : "CSV";
        Console.Out.WriteLine($"{format} export complete.");
        WriteCount(Console.Out, "Conversations", summary.Conversations);
        WriteCount(Console.Out, "Messages", summary.Messages);
        WriteCount(Console.Out, "Attachments", summary.Attachments);
        WriteCount(Console.Out, "Media copied", summary.MediaCopied);
        WriteCount(Console.Out, "Media unavailable", summary.MediaUnavailable);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"Output: {outputDirectory}");
        return 0;
    }

    private static int RunScan(string[] args, CancellationToken cancellationToken, ILogger logger)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && !args[2].Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogError("Usage: voicebridge scan <source> [--json]");
            return 2;
        }

        var scanResult = new TakeoutScanner().Scan(args[1], cancellationToken);
        if (!scanResult.IsSuccess)
        {
            var error = scanResult.Error!;
            logger.LogError("{ErrorCode}: {ErrorMessage}", error.Code, error.Message);
            return 1;
        }

        var report = scanResult.Value!;
        if (args.Length == 3)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }

        WriteSummary(report, Console.Out);
        foreach (var warning in report.Warnings)
        {
            logger.LogWarning("{WarningCode}: {WarningMessage}", warning.Code, warning.Message);
        }

        return 0;
    }

    private static void WriteSummary(ScanReport report, TextWriter output)
    {
        output.WriteLine(report.VoiceContentFound
            ? "Google Voice export detected"
            : "No Google Voice export detected");
        output.WriteLine();
        WriteCount(output, "Files scanned", report.FilesScanned);
        WriteCount(output, "Candidate message pages", report.CandidateMessagePages);
        WriteCount(output, "Candidate image/video media", report.CandidateImageVideoMediaFiles);
        WriteCount(output, "Candidate audio media", report.CandidateAudioMediaFiles);
        WriteCount(output, "Candidate voicemail pages", report.CandidateVoicemailPages);
        WriteCount(output, "Candidate call/event pages", report.CandidateCallEventPages);
        WriteCount(output, "Other recognized Voice files", report.OtherVoiceFiles);
        WriteCount(output, "Unknown files", report.UnknownFiles);
        WriteCount(output, "Warnings", report.Warnings.Count);
    }

    private static void WriteCount(TextWriter output, string label, long count) =>
        output.WriteLine($"{label + ":",-30} {count.ToString("N0", CultureInfo.InvariantCulture),10}");

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("VoiceBridge — local Google Voice Takeout archive tools");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  voicebridge scan <source> [--json]");
        output.WriteLine("  voicebridge import <source> --output <directory>");
        output.WriteLine("  voicebridge export <html|csv> <database> --output <directory>");
        output.WriteLine("  voicebridge --help");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  scan          Inventory a ZIP archive or extracted Takeout directory.");
        output.WriteLine("  import        Create a local SQLite archive and import report.");
        output.WriteLine("  export        Create an offline HTML archive or relational CSV files.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --json        Write a machine-readable scan report to standard output.");
        output.WriteLine("  -h, --help    Show this help.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
