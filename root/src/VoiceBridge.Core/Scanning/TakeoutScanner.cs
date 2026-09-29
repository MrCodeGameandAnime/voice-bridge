using System.IO.Compression;
using System.Globalization;
using VoiceBridge.Core.Domain;
using VoiceBridge.Core.Results;

namespace VoiceBridge.Core.Scanning;

public sealed class TakeoutScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".3gpp", ".mp4", ".m4v", ".mov", ".webm"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".amr", ".m4a", ".mp3", ".ogg", ".wav"
    };

    private static readonly string[] CallEventLabels = ["Placed", "Received", "Missed", "Recorded"];
    private static readonly string[] VoiceHtmlLabels = ["Text", "Voicemail", .. CallEventLabels];
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    public Result<ScanReport> Scan(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Failure("invalid_source", "Provide a ZIP archive or extracted Takeout directory.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("invalid_source", "The source path is not valid.");
        }

        if (Directory.Exists(fullPath))
        {
            return ScanDirectory(fullPath, cancellationToken);
        }

        if (!File.Exists(fullPath))
        {
            return Failure("source_not_found", "The source path does not exist.");
        }

        if (!Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("unsupported_source", "The source file must be a .zip archive.");
        }

        return ScanZip(fullPath, cancellationToken);
    }

    private static Result<ScanReport> ScanDirectory(string sourcePath, CancellationToken cancellationToken)
    {
        var state = new ScanState(sourcePath, SourceKind.Directory, IsVoiceDirectoryContext(sourcePath));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourcePath, filePath).Replace('\\', '/');
                state.AddFile(relativePath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Failure("source_unreadable", "The source directory could not be completely read.");
        }

        return Result<ScanReport>.Success(state.ToReport());
    }

    private static Result<ScanReport> ScanZip(string sourcePath, CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileNameWithoutExtension(sourcePath);
        var containingDirectory = Path.GetDirectoryName(sourcePath);
        var archiveIsVoiceSubfolder = (archiveName.Equals("Calls", StringComparison.OrdinalIgnoreCase)
                || archiveName.Equals("Spam", StringComparison.OrdinalIgnoreCase))
            && containingDirectory is not null
            && Path.GetFileName(containingDirectory).Equals("Voice", StringComparison.OrdinalIgnoreCase);
        var rootIsVoice = archiveName.Equals("Voice", StringComparison.OrdinalIgnoreCase) || archiveIsVoiceSubfolder;
        var state = new ScanState(sourcePath, SourceKind.ZipArchive, rootIsVoice);
        var buffer = new byte[81920];

        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizeArchivePath(entry.FullName);
                if (relativePath.Length == 0)
                {
                    continue;
                }

                if (entry.Name.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    state.ObserveDirectory(relativePath);
                    continue;
                }

                ValidateEntryChecksum(entry, buffer, cancellationToken);
                state.AddFile(relativePath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Failure("invalid_archive", "The ZIP archive is corrupt or unreadable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Failure("archive_unreadable", "The ZIP archive could not be read.");
        }

        return Result<ScanReport>.Success(state.ToReport());
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool IsVoiceDirectoryContext(string sourcePath)
    {
        var directory = new DirectoryInfo(sourcePath);
        return directory.Name.Equals("Voice", StringComparison.OrdinalIgnoreCase)
            || ((directory.Name.Equals("Calls", StringComparison.OrdinalIgnoreCase)
                    || directory.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase))
                && directory.Parent?.Name.Equals("Voice", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void ValidateEntryChecksum(ZipArchiveEntry entry, byte[] buffer, CancellationToken cancellationToken)
    {
        uint checksum = uint.MaxValue;
        long bytesRead = 0;

        using var content = entry.Open();
        int count;
        while ((count = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead += count;
            for (var index = 0; index < count; index++)
            {
                checksum = Crc32Table[(checksum ^ buffer[index]) & 0xff] ^ (checksum >> 8);
            }
        }

        if (bytesRead != entry.Length || ~checksum != entry.Crc32)
        {
            throw new InvalidDataException($"ZIP member '{entry.FullName}' failed its length or CRC-32 check.");
        }
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            var checksum = value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum & 1) == 1 ? 0xedb88320 ^ (checksum >> 1) : checksum >> 1;
            }

            table[value] = checksum;
        }

        return table;
    }

    private static Result<ScanReport> Failure(string code, string message) =>
        Result<ScanReport>.Failure(new VoiceBridgeError(code, message));

    private sealed class ScanState
    {
        private readonly string _sourcePath;
        private readonly SourceKind _sourceKind;
        private readonly bool _rootIsVoice;
        private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ScanWarning> _warnings = [];

        public ScanState(string sourcePath, SourceKind sourceKind, bool rootIsVoice)
        {
            _sourcePath = sourcePath;
            _sourceKind = sourceKind;
            _rootIsVoice = rootIsVoice;
            VoiceContentFound = rootIsVoice;
        }

        public bool VoiceContentFound { get; private set; }
        public long FilesScanned { get; private set; }
        public long CandidateMessageFiles { get; private set; }
        public long CandidateAttachments { get; private set; }
        public long CandidateVoicemailMediaFiles { get; private set; }
        public long VoicemailPages { get; private set; }
        public long CallEventPages { get; private set; }
        public long OtherVoiceFiles { get; private set; }
        public long UnknownFiles { get; private set; }

        public void ObserveDirectory(string relativePath)
        {
            if (IsVoicePath(relativePath, _rootIsVoice))
            {
                VoiceContentFound = true;
            }
        }

        public void AddFile(string relativePath)
        {
            FilesScanned++;
            var normalizedPath = NormalizeArchivePath(relativePath);
            if (!_seenPaths.Add(normalizedPath))
            {
                _warnings.Add(new ScanWarning(
                    "duplicate_source_path",
                    "The source contains duplicate paths; each file entry is counted separately.",
                    normalizedPath));
            }

            if (!IsVoicePath(normalizedPath, _rootIsVoice))
            {
                UnknownFiles++;
                return;
            }

            VoiceContentFound = true;
            var fileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
            var extension = Path.GetExtension(fileName);

            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase))
            {
                ClassifyHtml(normalizedPath, fileName);
                return;
            }

            if (ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension))
            {
                CandidateAttachments++;
                return;
            }

            if (AudioExtensions.Contains(extension))
            {
                CandidateVoicemailMediaFiles++;
                return;
            }

            if (extension.Equals(".vcf", StringComparison.OrdinalIgnoreCase))
            {
                OtherVoiceFiles++;
                return;
            }

            UnknownFiles++;
            _warnings.Add(new ScanWarning(
                "unknown_voice_file_type",
                "A file under Voice has an unrecognized file type.",
                normalizedPath));
        }

        public ScanReport ToReport()
        {
            if (FilesScanned == 0)
            {
                _warnings.Add(new ScanWarning("empty_source", "The source contains no files."));
            }

            if (!VoiceContentFound)
            {
                _warnings.Add(new ScanWarning("voice_folder_missing", "No Google Voice folder or Voice content was found."));
            }

            if (UnknownFiles > 0)
            {
                _warnings.Add(new ScanWarning(
                    "unclassified_files",
                    $"{UnknownFiles} file(s) could not be classified as Google Voice content."));
            }

            var warnings = _warnings
                .Distinct()
                .OrderBy(warning => warning.Code, StringComparer.Ordinal)
                .ThenBy(warning => warning.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(warning => warning.RelativePath, StringComparer.Ordinal)
                .ToArray();

            return new ScanReport(
                _sourcePath,
                _sourceKind,
                VoiceContentFound,
                FilesScanned,
                CandidateMessageFiles,
                CandidateAttachments,
                CandidateVoicemailMediaFiles,
                VoicemailPages,
                CallEventPages,
                OtherVoiceFiles,
                UnknownFiles,
                warnings);
        }

        private void ClassifyHtml(string relativePath, string fileName)
        {
            if (HasFilenameLabel(fileName, "Text") || fileName.StartsWith("Group Conversation - ", StringComparison.OrdinalIgnoreCase))
            {
                WarnIfMalformedTimestamp(relativePath, fileName);
                CandidateMessageFiles++;
                return;
            }

            if (HasFilenameLabel(fileName, "Voicemail"))
            {
                WarnIfMalformedTimestamp(relativePath, fileName);
                VoicemailPages++;
                OtherVoiceFiles++;
                return;
            }

            if (CallEventLabels.Any(label => HasFilenameLabel(fileName, label)))
            {
                WarnIfMalformedTimestamp(relativePath, fileName);
                CallEventPages++;
                OtherVoiceFiles++;
                return;
            }

            UnknownFiles++;
            _warnings.Add(new ScanWarning(
                "unrecognized_voice_html_filename",
                "An HTML file under Voice does not have a recognized message or event filename label.",
                relativePath));
        }

        private void WarnIfMalformedTimestamp(string relativePath, string fileName)
        {
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string? timestamp = null;

            if (nameWithoutExtension.StartsWith("Group Conversation - ", StringComparison.OrdinalIgnoreCase))
            {
                timestamp = nameWithoutExtension["Group Conversation - ".Length..];
            }
            else
            {
                foreach (var label in VoiceHtmlLabels)
                {
                    var marker = $" - {label} - ";
                    var markerIndex = nameWithoutExtension.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex >= 0)
                    {
                        timestamp = nameWithoutExtension[(markerIndex + marker.Length)..];
                        break;
                    }
                }
            }

            if (timestamp is not null && !DateTime.TryParseExact(
                    timestamp,
                    "yyyy-MM-dd'T'HH_mm_ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _))
            {
                _warnings.Add(new ScanWarning(
                    "malformed_voice_html_filename",
                    "A recognized Voice HTML filename has a malformed timestamp.",
                    relativePath));
            }
        }
    }

    private static bool IsVoicePath(string relativePath, bool rootIsVoice)
    {
        if (rootIsVoice)
        {
            return true;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment.Equals("Voice", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasFilenameLabel(string fileName, string label) =>
        fileName.Contains($" - {label} - ", StringComparison.OrdinalIgnoreCase);
}
