using System.Text;

namespace VoiceBridge.Desktop.Diagnostics;

public static class LocalCrashLog
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string? TryWrite(string? outputDirectory, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return null;
        }

        try
        {
            var fullDirectory = Path.GetFullPath(outputDirectory);
            var fileName = $"voicebridge-crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt";
            var logPath = Path.Combine(fullDirectory, fileName);
            using var log = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(log, Utf8WithoutBom);
            writer.WriteLine("VoiceBridge local crash report");
            writer.WriteLine($"UTC: {DateTimeOffset.UtcNow:O}");
            writer.WriteLine();
            writer.WriteLine(exception.ToString());
            return logPath;
        }
        catch (Exception)
        {
            // Crash logging must never replace the original application failure.
            return null;
        }
    }
}
