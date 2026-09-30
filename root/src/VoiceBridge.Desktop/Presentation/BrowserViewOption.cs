namespace VoiceBridge.Desktop.Presentation;

public sealed record BrowserViewOption(string Filter, bool NewestFirst, string Label)
{
    private static readonly HashSet<string> SortableViews = new(StringComparer.Ordinal)
    {
        "Messages",
        "Calls",
        "Voicemails",
        "Media"
    };

    public override string ToString() => Label;

    public static IReadOnlyList<BrowserViewOption> CreateOptions(string view, IEnumerable<string> filters)
    {
        var distinctFilters = filters.Distinct(StringComparer.Ordinal).ToArray();
        if (!SortableViews.Contains(view))
        {
            return distinctFilters.Select(filter => new BrowserViewOption(filter, true, filter)).ToArray();
        }

        var includeFilterLabel = distinctFilters.Any(filter => !string.Equals(filter, "All", StringComparison.Ordinal));
        return distinctFilters
            .SelectMany(filter => new[]
            {
                new BrowserViewOption(filter, true, FormatLabel(filter, "Newest", includeFilterLabel)),
                new BrowserViewOption(filter, false, FormatLabel(filter, "Oldest", includeFilterLabel))
            })
            .ToArray();
    }

    private static string FormatLabel(string filter, string sort, bool includeFilterLabel) =>
        includeFilterLabel ? $"{filter} · {sort}" : sort;
}
