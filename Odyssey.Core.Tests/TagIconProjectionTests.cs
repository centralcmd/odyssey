using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The one helper that fills <c>TagIds</c> and <c>DisplayIcon</c> on journal entries, tasks and photos
/// (issue #345 §3, AC 7, AC 8).
/// </summary>
public class TagIconProjectionTests
{
    private static readonly Guid Low = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid High = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void Apply_orders_the_ids_by_tag_name_and_resolves_the_icon()
    {
        var art = Guid.NewGuid();
        var trip = Guid.NewGuid();

        var record = TagIconProjection.Apply(Photo(),
            [new TagIconCandidate(trip, "Trip", "beach_access"), new TagIconCandidate(art, "art", null)]);

        Assert.Equal([art, trip], record.TagIds);
        Assert.Equal("beach_access", record.DisplayIcon);
    }

    [Fact]
    public void A_dangling_link_keeps_its_id_sorts_last_by_id_and_adds_no_icon()
    {
        var trip = Guid.NewGuid();
        var lookup = new Dictionary<Guid, TagIconCandidate> { [trip] = new(trip, "Trip", null) };

        var record = TagIconProjection.Apply(Photo(), [High, trip, Low], lookup);

        Assert.Equal([trip, Low, High], record.TagIds);
        Assert.Equal(TagIcons.Default, record.DisplayIcon);
    }

    [Fact]
    public void A_repeated_id_is_returned_once()
    {
        var trip = Guid.NewGuid();
        var lookup = new Dictionary<Guid, TagIconCandidate> { [trip] = new(trip, "Trip", "beach_access") };

        var record = TagIconProjection.Apply(Photo(), [trip, trip], lookup);

        Assert.Equal([trip], record.TagIds);
    }

    [Fact]
    public void No_tags_is_the_default_icon_and_no_ids()
    {
        var record = TagIconProjection.Apply(Photo(), []);

        Assert.Empty(record.TagIds);
        Assert.Equal(TagIcons.Default, record.DisplayIcon);
    }

    private static PhotoSummary Photo() =>
        new() { PhotoId = Guid.NewGuid(), FileId = Guid.NewGuid(), PersonCount = 0, AlbumCount = 0 };
}
