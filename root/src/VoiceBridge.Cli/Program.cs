using Microsoft.Extensions.Logging;

namespace VoiceBridge.Cli;

internal static class Program
{
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

        logger.LogError("This command is not available yet. Run 'voicebridge --help' for usage.");
        return 2;
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("VoiceBridge — local Google Voice Takeout archive tools");
        output.WriteLine();
        output.WriteLine("Usage: voicebridge --help");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  -h, --help    Show this help.");
    }
}
