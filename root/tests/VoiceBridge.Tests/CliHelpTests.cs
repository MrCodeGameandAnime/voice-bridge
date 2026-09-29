using System.Reflection;

namespace VoiceBridge.Tests;

public sealed class CliHelpTests
{
    [Fact]
    public async Task HelpFlagDisplaysUsageAndExitsSuccessfully()
    {
        var entryPoint = Assembly.Load("VoiceBridge.Cli").EntryPoint;
        Assert.NotNull(entryPoint);

        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var priorOutput = Console.Out;
        var priorError = Console.Error;

        try
        {
            Console.SetOut(standardOutput);
            Console.SetError(standardError);
            var result = entryPoint.Invoke(null, [new[] { "--help" }]);
            var exitCode = await GetExitCodeAsync(result);

            Assert.Equal(0, exitCode);
            Assert.Contains("Usage: voicebridge", standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(priorOutput);
            Console.SetError(priorError);
        }
    }

    private static async Task<int> GetExitCodeAsync(object? result)
    {
        if (result is int exitCode)
        {
            return exitCode;
        }

        if (result is Task<int> exitTask)
        {
            return await exitTask;
        }

        if (result is Task task)
        {
            await task;
        }

        return 0;
    }
}
