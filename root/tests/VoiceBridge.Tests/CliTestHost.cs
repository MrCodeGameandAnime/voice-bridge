using System.Reflection;
using System.Runtime.ExceptionServices;

namespace VoiceBridge.Tests;

[CollectionDefinition("CLI commands", DisableParallelization = true)]
public sealed class CliCommandCollection;

internal sealed record CliTestResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CliTestHost
{
    public static CliTestResult Run(params string[] args)
    {
        var entryPoint = Assembly.Load("VoiceBridge.Cli").EntryPoint
            ?? throw new InvalidOperationException("The CLI assembly has no entry point.");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var priorOutput = Console.Out;
        var priorError = Console.Error;

        try
        {
            Console.SetOut(standardOutput);
            Console.SetError(standardError);
            var result = entryPoint.Invoke(null, [args]);
            return new CliTestResult(result is int exitCode ? exitCode : 0, standardOutput.ToString(), standardError.ToString());
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        finally
        {
            Console.SetOut(priorOutput);
            Console.SetError(priorError);
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    private static readonly string TempRoot = Path.GetFullPath(Path.GetTempPath());

    public TemporaryDirectory()
    {
        RootPath = Path.Combine(TempRoot, $"VoiceBridge.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(RootPath);
        var prefix = Path.EndsInDirectorySeparator(TempRoot) ? TempRoot : TempRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a test directory outside the system temp folder.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
