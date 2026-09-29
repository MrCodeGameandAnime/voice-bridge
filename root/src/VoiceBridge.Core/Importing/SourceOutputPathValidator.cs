namespace VoiceBridge.Core.Importing;

public static class SourceOutputPathValidator
{
    public static void EnsureOutputOutsideDirectorySource(string sourcePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!Directory.Exists(sourcePath))
        {
            return;
        }

        var resolvedSource = ResolveDirectoryAliases(sourcePath);
        var resolvedOutput = ResolveDirectoryAliases(outputPath);
        var relativePath = Path.GetRelativePath(resolvedSource, resolvedOutput);
        var isSameOrInside = relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));

        if (isSameOrInside)
        {
            throw new ArgumentException("The output directory must be outside the extracted source directory.", nameof(outputPath));
        }
    }

    private static string ResolveDirectoryAliases(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The path must have a filesystem root.", nameof(path));
        var current = root;
        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }

        return Path.GetFullPath(current);
    }
}
