using VoiceBridge.Core.Parsing;

namespace VoiceBridge.Core.Reconstruction;

public static class DuplicateSourcePageDetector
{
    public static IReadOnlyList<DuplicateSourcePageGroup> FindExactDuplicates(
        IEnumerable<MessagePageParseResult> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var pathsByHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (!page.IsSupportedMessagePage || string.IsNullOrWhiteSpace(page.SourceContentSha256))
            {
                continue;
            }

            if (!pathsByHash.TryGetValue(page.SourceContentSha256, out var paths))
            {
                paths = [];
                pathsByHash.Add(page.SourceContentSha256, paths);
            }

            paths.Add(page.SourceRelativePath);
        }

        return pathsByHash
            .Where(group => group.Value.Count > 1)
            .Select(group => new DuplicateSourcePageGroup(group.Key, group.Value.ToArray()))
            .ToArray();
    }
}
