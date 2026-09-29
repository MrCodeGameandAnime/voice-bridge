using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceBridge.Tests;

[Collection("CLI commands")]
public sealed class TakeoutScanCliTests
{
    [Fact]
    public void ZipInputProducesJsonInventoryWithoutChangingArchive()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "sample.zip");
        CreateZip(archivePath,
            ("Takeout/Voice/Calls/Contact - Text - 2024-01-02T03_04_05Z.html", "<html></html>"),
            ("Takeout/Voice/Calls/Contact - Received - 2024-01-02T03_04_05Z.html", "<html></html>"),
            ("Takeout/Voice/Calls/Contact - Voicemail - 2024-01-02T03_04_05Z.html", "<html></html>"),
            ("Takeout/Voice/Calls/photo.jpg", "image"),
            ("Takeout/Voice/Calls/voicemail.mp3", "audio"),
            ("Takeout/Voice/Phones.vcf", "contact"),
            ("Takeout/archive_browser.html", "takeout"));
        var originalHash = SHA256.HashData(File.ReadAllBytes(archivePath));

        using var report = RunJson("scan", archivePath, "--json");

        Assert.Equal("zipArchive", report.RootElement.GetProperty("sourceKind").GetString());
        Assert.True(report.RootElement.GetProperty("voiceContentFound").GetBoolean());
        Assert.Equal(7, report.RootElement.GetProperty("filesScanned").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateImageVideoMediaFiles").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateAudioMediaFiles").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateCallEventPages").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateVoicemailPages").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("otherVoiceFiles").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("unknownFiles").GetInt64());
        Assert.Equal(7, SumClassifiedFiles(report.RootElement));
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(archivePath)));
    }

    [Fact]
    public void DirectoryInputClassifiesGroupVoicemailAndAttachments()
    {
        using var temporary = new TemporaryDirectory();
        var voiceRoot = Path.Combine(temporary.RootPath, "Takeout", "Voice");
        var spamRoot = Path.Combine(voiceRoot, "Spam");
        Directory.CreateDirectory(spamRoot);
        File.WriteAllText(Path.Combine(spamRoot, "Group Conversation - 2024-01-02T03_04_05Z.html"), "<html></html>");
        File.WriteAllText(Path.Combine(spamRoot, "Caller - Voicemail - 2024-01-02T03_04_05Z.html"), "<html></html>");
        File.WriteAllText(Path.Combine(spamRoot, "voicemail.mp3"), "audio");
        File.WriteAllText(Path.Combine(spamRoot, "image.gif"), "image");
        File.WriteAllText(Path.Combine(voiceRoot, "Phones.vcf"), "contact");

        using var report = RunJson("scan", temporary.RootPath, "--json");

        Assert.Equal("directory", report.RootElement.GetProperty("sourceKind").GetString());
        Assert.Equal(5, report.RootElement.GetProperty("filesScanned").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateImageVideoMediaFiles").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateAudioMediaFiles").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateVoicemailPages").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("candidateCallEventPages").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("otherVoiceFiles").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("unknownFiles").GetInt64());
        Assert.Equal(5, SumClassifiedFiles(report.RootElement));
    }

    [Fact]
    public void MissingVoiceFolderLeavesFilesUnclassifiedAndWarns()
    {
        using var temporary = new TemporaryDirectory();
        var takeout = Path.Combine(temporary.RootPath, "Takeout", "Photos");
        Directory.CreateDirectory(takeout);
        File.WriteAllText(Path.Combine(takeout, "picture.jpg"), "image");
        File.WriteAllText(Path.Combine(temporary.RootPath, "notes.txt"), "notes");

        using var report = RunJson("scan", temporary.RootPath, "--json");

        Assert.False(report.RootElement.GetProperty("voiceContentFound").GetBoolean());
        Assert.Equal(2, report.RootElement.GetProperty("filesScanned").GetInt64());
        Assert.Equal(2, report.RootElement.GetProperty("unknownFiles").GetInt64());
        Assert.Contains("voice_folder_missing", WarningCodes(report));
    }

    [Fact]
    public void CorruptZipReturnsClearErrorAndNonzeroExit()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "broken.zip");
        File.WriteAllText(archivePath, "not a zip archive");

        var result = CliTestHost.Run("scan", archivePath, "--json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("archive", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", result.StandardOutput);
    }

    [Fact]
    public void ZipWithCorruptMemberChecksumReturnsClearError()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "corrupt-member.zip");
        CreateZip(archivePath, ("Takeout/Voice/Calls/record.txt", "member payload"));
        CorruptFirstCentralDirectoryChecksum(archivePath);

        var result = CliTestHost.Run("scan", archivePath, "--json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("corrupt", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", result.StandardOutput);
    }

    [Fact]
    public void EmptyArchiveProducesZeroCountsAndWarnings()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "empty.zip");
        CreateZip(archivePath);

        using var report = RunJson("scan", archivePath, "--json");

        Assert.Equal(0, report.RootElement.GetProperty("filesScanned").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.Contains("empty_source", WarningCodes(report));
        Assert.Contains("voice_folder_missing", WarningCodes(report));
    }

    [Fact]
    public void UnrelatedTakeoutContentIsUnknown()
    {
        using var temporary = new TemporaryDirectory();
        var drive = Path.Combine(temporary.RootPath, "Takeout", "Drive");
        Directory.CreateDirectory(drive);
        File.WriteAllText(Path.Combine(drive, "report.json"), "{}");
        File.WriteAllText(Path.Combine(drive, "readme.txt"), "text");

        using var report = RunJson("scan", temporary.RootPath, "--json");

        Assert.Equal(2, report.RootElement.GetProperty("filesScanned").GetInt64());
        Assert.Equal(2, report.RootElement.GetProperty("unknownFiles").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.False(report.RootElement.GetProperty("voiceContentFound").GetBoolean());
    }

    [Fact]
    public void DirectoryNamedCallsOutsideVoiceIsNotTreatedAsVoice()
    {
        using var temporary = new TemporaryDirectory();
        var calls = Path.Combine(temporary.RootPath, "Photos", "Calls");
        Directory.CreateDirectory(calls);
        File.WriteAllText(Path.Combine(calls, "notes.txt"), "notes");

        using var report = RunJson("scan", calls, "--json");

        Assert.False(report.RootElement.GetProperty("voiceContentFound").GetBoolean());
        Assert.Equal(1, report.RootElement.GetProperty("unknownFiles").GetInt64());
    }

    [Fact]
    public void SpamNamedArchiveOutsideVoiceIsNotTreatedAsVoice()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "Spam.zip");
        CreateZip(archivePath, ("notes.txt", "notes"));

        using var report = RunJson("scan", archivePath, "--json");

        Assert.False(report.RootElement.GetProperty("voiceContentFound").GetBoolean());
        Assert.Equal(1, report.RootElement.GetProperty("unknownFiles").GetInt64());
    }

    [Fact]
    public void MalformedVoiceHtmlFilenameIsUnknownAndWarned()
    {
        using var temporary = new TemporaryDirectory();
        var calls = Path.Combine(temporary.RootPath, "Takeout", "Voice", "Calls");
        Directory.CreateDirectory(calls);
        File.WriteAllText(Path.Combine(calls, "conversation.html"), "<html></html>");

        using var report = RunJson("scan", temporary.RootPath, "--json");

        Assert.Equal(1, report.RootElement.GetProperty("unknownFiles").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.Contains("unrecognized_voice_html_filename", WarningCodes(report));
    }

    [Fact]
    public void RecognizedHtmlLabelWithMalformedTimestampRemainsCandidateAndWarns()
    {
        using var temporary = new TemporaryDirectory();
        var calls = Path.Combine(temporary.RootPath, "Takeout", "Voice", "Calls");
        Directory.CreateDirectory(calls);
        File.WriteAllText(Path.Combine(calls, "Contact - Text - not-a-timestamp.html"), "<html></html>");

        using var report = RunJson("scan", temporary.RootPath, "--json");

        Assert.Equal(1, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.Contains("malformed_voice_html_filename", WarningCodes(report));
    }

    [Fact]
    public void FilenameKindIsParsedFromRightHandSuffix()
    {
        using var temporary = new TemporaryDirectory();
        var calls = Path.Combine(temporary.RootPath, "Takeout", "Voice", "Calls");
        Directory.CreateDirectory(calls);
        File.WriteAllText(
            Path.Combine(calls, "Contact - Text - birthday - Received - 2024-01-02T03_04_05Z.html"),
            "<html></html>");

        using var report = RunJson("scan", temporary.RootPath, "--json");

        Assert.Equal(0, report.RootElement.GetProperty("candidateMessagePages").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("candidateCallEventPages").GetInt64());
        Assert.Equal(1, SumClassifiedFiles(report.RootElement));
    }

    [Fact]
    public void WarningPathsUseOrdinalTieBreakAfterCaseInsensitiveSort()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.RootPath, "case-tie.zip");
        CreateZip(archivePath,
            ("Takeout/Voice/Calls/A.unknown", "a"),
            ("Takeout/Voice/Calls/a.unknown", "b"));

        using var report = RunJson("scan", archivePath, "--json");

        var paths = report.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Where(warning => warning.GetProperty("code").GetString() == "unknown_voice_file_type")
            .Select(warning => warning.GetProperty("relativePath").GetString()!)
            .ToArray();
        Assert.Equal(["Takeout/Voice/Calls/A.unknown", "Takeout/Voice/Calls/a.unknown"], paths);
    }

    [Fact]
    public void ScannerHonorsCancellationBeforeReadingSource()
    {
        using var temporary = new TemporaryDirectory();
        var scannerType = Assembly.Load("VoiceBridge.Core").GetType("VoiceBridge.Core.Scanning.TakeoutScanner");
        Assert.NotNull(scannerType);
        var scanMethod = scannerType.GetMethod("Scan");
        Assert.NotNull(scanMethod);
        var scanner = Activator.CreateInstance(scannerType);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            scanMethod.Invoke(scanner, [temporary.RootPath, cancellation.Token]));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    private static JsonDocument RunJson(params string[] args)
    {
        var result = CliTestHost.Run(args);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static IEnumerable<string> WarningCodes(JsonDocument report) =>
        report.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(warning => warning.GetProperty("code").GetString()!);

    private static long SumClassifiedFiles(JsonElement report) =>
        report.GetProperty("candidateMessagePages").GetInt64()
        + report.GetProperty("candidateVoicemailPages").GetInt64()
        + report.GetProperty("candidateCallEventPages").GetInt64()
        + report.GetProperty("candidateImageVideoMediaFiles").GetInt64()
        + report.GetProperty("candidateAudioMediaFiles").GetInt64()
        + report.GetProperty("otherVoiceFiles").GetInt64()
        + report.GetProperty("unknownFiles").GetInt64();

    private static void CreateZip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private static void CorruptFirstCentralDirectoryChecksum(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var signature = new byte[] { 0x50, 0x4b, 0x01, 0x02 };
        var centralDirectoryOffset = bytes.AsSpan().LastIndexOf(signature);
        Assert.True(centralDirectoryOffset >= 0, "The test archive must contain a central directory entry.");
        bytes[centralDirectoryOffset + 16] ^= 0xff;
        File.WriteAllBytes(path, bytes);
    }
}
