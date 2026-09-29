using Microsoft.Extensions.Logging;

namespace VoiceBridge.Cli;

internal sealed record CliConfiguration(LogLevel MinimumLogLevel)
{
    public static CliConfiguration Load()
    {
        var configuredLevel = Environment.GetEnvironmentVariable("VOICEBRIDGE_LOG_LEVEL");
        return Enum.TryParse(configuredLevel, ignoreCase: true, out LogLevel level) && Enum.IsDefined(level)
            ? new CliConfiguration(level)
            : new CliConfiguration(LogLevel.Information);
    }
}
