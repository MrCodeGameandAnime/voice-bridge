using System.Reflection;

namespace VoiceBridge.Tests;

public sealed class Gate10SupportTests
{
    [Fact]
    public void CrashLogDestinationAllowsASeparateUserSelectedOutputFolder()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.RootPath, "Takeout")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(temporary.RootPath, "VoiceBridge output")).FullName;

        var resolved = InvokeStatic<string?>(
            "VoiceBridge.Desktop.Diagnostics.CrashLogPolicy",
            "Resolve",
            source,
            output);

        Assert.Equal(Path.GetFullPath(output), resolved);
    }

    [Fact]
    public void CrashLogDestinationRejectsAnyFolderInsideTheExtractedSource()
    {
        using var temporary = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temporary.RootPath, "Takeout")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(source, "VoiceBridge output")).FullName;

        var resolved = InvokeStatic<string?>(
            "VoiceBridge.Desktop.Diagnostics.CrashLogPolicy",
            "Resolve",
            source,
            output);

        Assert.Null(resolved);
    }

    [Fact]
    public void CrashLogWritesDetailsOnlyInsideAnExistingSelectedOutputFolder()
    {
        using var temporary = new TemporaryDirectory();
        var output = Directory.CreateDirectory(Path.Combine(temporary.RootPath, "VoiceBridge output")).FullName;

        var logPath = InvokeStatic<string?>(
            "VoiceBridge.Desktop.Diagnostics.LocalCrashLog",
            "TryWrite",
            output,
            new InvalidOperationException("fixture crash detail"));

        Assert.NotNull(logPath);
        Assert.Equal(Path.GetFullPath(output), Path.GetDirectoryName(logPath));
        Assert.StartsWith("voicebridge-crash-", Path.GetFileName(logPath), StringComparison.OrdinalIgnoreCase);
        var logContents = File.ReadAllText(logPath);
        Assert.Contains("InvalidOperationException", logContents, StringComparison.Ordinal);
        Assert.Contains("fixture crash detail", logContents, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashLogDoesNotCreateOrWriteOutsideAnUnselectedOutputFolder()
    {
        using var temporary = new TemporaryDirectory();
        var output = Path.Combine(temporary.RootPath, "not-created");

        var logPath = InvokeStatic<string?>(
            "VoiceBridge.Desktop.Diagnostics.LocalCrashLog",
            "TryWrite",
            output,
            new InvalidOperationException("fixture crash detail"));

        Assert.Null(logPath);
        Assert.False(Directory.Exists(output));
    }

    private static T? InvokeStatic<T>(string typeName, string methodName, params object?[] arguments)
    {
        var type = typeof(Gate10SupportTests).Assembly.GetType(typeName);
        Assert.NotNull(type);
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (T?)method.Invoke(null, arguments);
    }
}
