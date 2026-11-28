using Odyssey.Client.Pages.Internet;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>The Overview's "By feed" / "By podcast" rows (<see cref="NewsVisuals.CountByFeedRows"/>).</summary>
public sealed class NewsVisualsCountByFeedTests
{
    [Fact]
    public void A_negative_counter_reads_as_zero_and_sorts_as_zero()
    {
        var rows = NewsVisuals.CountByFeedRows(
        [
            new FeedCount(Guid.NewGuid(), "Drifted", -3, "rss_feed"),
            new FeedCount(Guid.NewGuid(), "Alpha", 0, "rss_feed"),
        ]);

        Assert.Equal([("Alpha", 0), ("Drifted", 0)], rows.Select(r => (r.Label, (int)r.Count)));
    }

    [Theory]
    [InlineData(1, "1 episode")]
    [InlineData(0, "0 episodes")]
    [InlineData(-2, "0 episodes")]
    public void A_count_with_a_unit_names_it_in_text(int count, string expected)
    {
        var row = Assert.Single(NewsVisuals.CountByFeedRows([new FeedCount(Guid.NewGuid(), "Show", count, "podcasts", ("episode", "episodes"))]));

        Assert.Equal(expected, row.Count);
    }

    [Fact]
    public void No_feeds_give_no_rows()
    {
        Assert.Empty(NewsVisuals.CountByFeedRows([]));
    }
}
