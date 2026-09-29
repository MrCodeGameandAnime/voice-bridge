using VoiceBridge.Core.Domain;

namespace VoiceBridge.Core.Reconstruction;

internal sealed class AttachmentReferenceMatcher
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff",
        ".3gp", ".3gpp", ".mp4", ".m4v", ".mov", ".webm",
        ".aac", ".amr", ".m4a", ".mp3", ".ogg", ".wav"
    };

    private readonly Dictionary<string, List<string>> _pathsByRelativePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pathsByBaseName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pathsByExtensionlessStem = new(StringComparer.OrdinalIgnoreCase);

    public AttachmentReferenceMatcher(IEnumerable<SourceFile> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);

        foreach (var sourceFile in sourceFiles)
        {
            var path = NormalizeSourcePath(sourceFile.RelativePath);
            var extension = GetExtension(path);
            if (!MediaExtensions.Contains(extension))
            {
                continue;
            }

            Add(_pathsByRelativePath, path, sourceFile.RelativePath);
            var baseName = GetFileName(path);
            Add(_pathsByBaseName, baseName, sourceFile.RelativePath);
            Add(_pathsByExtensionlessStem, GetFileNameWithoutExtension(baseName), sourceFile.RelativePath);
        }
    }

    public (Attachment Attachment, ImportIssue? Issue) Match(
        Attachment attachment,
        string sourceRelativePath,
        int? sourceRowIndex)
    {
        if (attachment.MatchedRelativePath is not null)
        {
            return (attachment, null);
        }

        var referencePath = ResolveRelativeReference(
            sourceRelativePath,
            attachment.RawReference,
            out var traversesAboveArchiveRoot);
        var candidates = !traversesAboveArchiveRoot
            && referencePath is not null
            && _pathsByRelativePath.TryGetValue(referencePath, out var exactMatches)
                ? exactMatches
                : traversesAboveArchiveRoot
                    ? null
                    : FindFallbackCandidates(attachment.RawReference);

        if (candidates is { Count: 1 })
        {
            var matchedPath = candidates[0];
            var mediaType = attachment.MediaType ?? GetMediaType(matchedPath);
            return (attachment with { MatchedRelativePath = matchedPath, MediaType = mediaType }, null);
        }

        var isAmbiguous = candidates is { Count: > 1 };
        var issue = new ImportIssue(
            isAmbiguous ? "attachment_reference_ambiguous" : "attachment_reference_unresolved",
            isAmbiguous
                ? "The attachment reference matches multiple source media files and was left unresolved."
                : "The attachment reference did not match a source media file and was left unresolved.",
            sourceRelativePath,
            sourceRowIndex);
        return (attachment, issue);
    }

    private List<string>? FindFallbackCandidates(string rawReference)
    {
        var cleanReference = StripQueryAndFragment(rawReference).Replace('\\', '/');
        if (Uri.TryCreate(cleanReference, UriKind.Absolute, out var absoluteUri))
        {
            if (!absoluteUri.IsFile)
            {
                return null;
            }

            cleanReference = absoluteUri.LocalPath.Replace('\\', '/');
        }

        var baseName = GetFileName(cleanReference);
        if (baseName.Length == 0)
        {
            return null;
        }

        var extension = GetExtension(baseName);
        if (extension.Length > 0)
        {
            return _pathsByBaseName.TryGetValue(baseName, out var baseNameMatches) ? baseNameMatches : null;
        }

        var stem = GetFileNameWithoutExtension(baseName);
        return _pathsByExtensionlessStem.TryGetValue(stem, out var stemMatches) ? stemMatches : null;
    }

    private static string? ResolveRelativeReference(
        string sourceRelativePath,
        string rawReference,
        out bool traversesAboveArchiveRoot)
    {
        traversesAboveArchiveRoot = false;
        var cleanReference = StripQueryAndFragment(rawReference);
        if (string.IsNullOrWhiteSpace(cleanReference))
        {
            return null;
        }

        if (Uri.TryCreate(cleanReference, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.IsFile ? NormalizeSourcePath(absoluteUri.LocalPath) : null;
        }

        cleanReference = Uri.UnescapeDataString(cleanReference).Replace('\\', '/');
        if (cleanReference.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var pathParts = new List<string>();
        if (!cleanReference.StartsWith("/", StringComparison.Ordinal))
        {
            var sourceDirectory = GetDirectoryName(NormalizeSourcePath(sourceRelativePath));
            if (sourceDirectory.Length > 0)
            {
                pathParts.AddRange(sourceDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        foreach (var part in cleanReference.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (pathParts.Count == 0)
                {
                    traversesAboveArchiveRoot = true;
                    return null;
                }

                pathParts.RemoveAt(pathParts.Count - 1);
                continue;
            }

            pathParts.Add(part);
        }

        return pathParts.Count == 0 ? null : string.Join('/', pathParts);
    }

    private static string? GetMediaType(string path) => GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" => "image",
        ".3gp" or ".3gpp" or ".mp4" or ".m4v" or ".mov" or ".webm" => "video",
        ".aac" or ".amr" or ".m4a" or ".mp3" or ".ogg" or ".wav" => "audio",
        _ => null
    };

    private static string StripQueryAndFragment(string value)
    {
        var cutIndex = value.IndexOfAny(['?', '#']);
        return cutIndex < 0 ? value : value[..cutIndex];
    }

    private static string NormalizeSourcePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string GetDirectoryName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string GetFileName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string GetExtension(string path)
    {
        var fileName = GetFileName(path);
        var period = fileName.LastIndexOf('.');
        return period <= 0 ? string.Empty : fileName[period..];
    }

    private static string GetFileNameWithoutExtension(string path)
    {
        var extension = GetExtension(path);
        return extension.Length == 0 ? path : path[..^extension.Length];
    }

    private static void Add(Dictionary<string, List<string>> index, string key, string path)
    {
        if (!index.TryGetValue(key, out var paths))
        {
            paths = [];
            index.Add(key, paths);
        }

        paths.Add(path);
    }
}
