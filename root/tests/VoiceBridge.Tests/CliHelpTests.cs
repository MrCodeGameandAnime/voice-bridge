namespace VoiceBridge.Tests;

[Collection("CLI commands")]
public sealed class CliHelpTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpFlagDisplaysUsageAndExitsSuccessfully(string flag)
    {
        var result = CliTestHost.Run(flag);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }
}
