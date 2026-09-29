using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceBridge.Core.Scanning;

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
            Console.Error.WriteLine("Scan cancelled.");
            return 130;
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

        logger.LogError("This command is not available yet. Run 'voicebridge --help' for usage.");
        return 2;
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
        output.WriteLine("  voicebridge --help");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  scan          Inventory a ZIP archive or extracted Takeout directory.");
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
