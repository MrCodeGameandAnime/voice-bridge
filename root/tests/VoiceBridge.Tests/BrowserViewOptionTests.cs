using VoiceBridge.Desktop.Presentation;

namespace VoiceBridge.Tests;

public sealed class BrowserViewOptionTests
{
    [Theory]
    [InlineData("Messages")]
    [InlineData("Calls")]
    [InlineData("Voicemails")]
    [InlineData("Media")]
    public void SortChoicesAreAvailableInTheSameSelectorAsFilters(string view)
    {
        var filters = view switch
        {
            "Messages" => new[] { "All", "With attachments", "Groups" },
            "Media" => new[] { "All", "Image", "Video", "Audio", "Unresolved" },
            _ => new[] { "All" }
        };
        var options = BrowserViewOption.CreateOptions(view, filters);
        var allNewestLabel = filters.Length == 1 ? "Newest" : "All · Newest";
        var allOldestLabel = filters.Length == 1 ? "Oldest" : "All · Oldest";

        Assert.Contains(options, option => option.Label == allNewestLabel && option.Filter == "All" && option.NewestFirst);
        Assert.Contains(options, option => option.Label == allOldestLabel && option.Filter == "All" && !option.NewestFirst);
        if (view == "Messages")
        {
            Assert.Contains(options, option => option.Label == "Groups · Newest" && option.Filter == "Groups" && option.NewestFirst);
            Assert.Contains(options, option => option.Label == "Groups · Oldest" && option.Filter == "Groups" && !option.NewestFirst);
        }
    }

    [Fact]
    public void SingleAllFilterShowsSimpleNewestAndOldestChoices()
    {
        var options = BrowserViewOption.CreateOptions("Calls", ["All"]);

        Assert.Equal(["Newest", "Oldest"], options.Select(option => option.Label));
    }

    [Fact]
    public void IssuesKeepTheirIssueFiltersWithoutSortChoices()
    {
        var options = BrowserViewOption.CreateOptions("Issues", ["All", "media_missing"]);

        Assert.Equal(["All", "media_missing"], options.Select(option => option.Label));
        Assert.All(options, option => Assert.True(option.NewestFirst));
    }
}
