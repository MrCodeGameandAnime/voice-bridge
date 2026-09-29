using VoiceBridge.Core.Importing;

namespace VoiceBridge.Desktop.Diagnostics;

public static class CrashLogPolicy
{
    public static string? Resolve(string? sourcePath, string? outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        try
        {
            SourceOutputPathValidator.EnsureOutputOutsideDirectorySource(sourcePath, outputDirectory);
            return Path.GetFullPath(outputDirectory);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }
}
