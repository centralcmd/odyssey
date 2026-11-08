using Bunit;
using Odyssey.Client.Components;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The journal / task / photo tag chip row (design system · <c>docs/components-calendar-journal-and-tasks.md</c>,
/// "Tag icons on journal entries, tasks and photos"): each chip leads with its own tag's icon or the
/// generic glyph, and <see cref="JournalTagChips.Max"/> folds the rest into a "+N" chip whose hidden names
/// reach assistive technology as text.
/// </summary>
public sealed class JournalTagChipsTests : IAsyncLifetime
{
    private readonly BunitContext ctx = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ctx.DisposeAsync().AsTask();

    private static List<JournalTagChips.Tag> Tags(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new JournalTagChips.Tag($"Tag {(char)('A' + i)}", "flag", null))];

    private IRenderedComponent<JournalTagChips> Render(IEnumerable<JournalTagChips.Tag> tags, int? max = null) =>
        ctx.Render<JournalTagChips>(p => p.Add(c => c.Tags, tags).Add(c => c.Max, max));

    [Theory]
    [InlineData("hiking", "hiking")]
    [InlineData(null, JournalTagChips.FallbackGlyph)]
    [InlineData("", JournalTagChips.FallbackGlyph)]
    [InlineData("not_a_known_key", JournalTagChips.FallbackGlyph)]
    [InlineData("HIKING", JournalTagChips.FallbackGlyph)]
    [InlineData(TagIcons.Default, JournalTagChips.FallbackGlyph)]
    public void A_chip_leads_with_its_known_icon_else_the_generic_glyph(string? icon, string glyph) =>
        Assert.Equal(glyph, JournalTagChips.GlyphFor(icon));

    [Fact]
    public void Each_chip_shows_its_own_icon_decoratively_beside_its_name()
    {
        var cut = Render([new("Travel", "flight", null), new("Misc", null, null)]);

        var chips = cut.FindAll(".odc-chip.tag");
        Assert.Equal(2, chips.Count);
        Assert.Equal("flight", chips[0].QuerySelector(".material-icons")!.TextContent.Trim());
        Assert.EndsWith("Travel", chips[0].TextContent.Trim());
        Assert.Equal(JournalTagChips.FallbackGlyph, chips[1].QuerySelector(".material-icons")!.TextContent.Trim());
        Assert.All(cut.FindAll(".material-icons"), g => Assert.Equal("true", g.GetAttribute("aria-hidden")));
    }

    [Fact]
    public void No_tags_render_nothing() => Assert.Empty(Render([]).Markup.Trim());

    [Fact]
    public void Over_the_cap_the_rest_fold_into_a_plus_n_chip_that_names_them_for_assistive_technology()
    {
        var cut = Render(Tags(6), max: 4);

        var named = cut.FindAll(".odc-chip.tag:not(.je-tagchips-more)");
        Assert.Equal(["Tag A", "Tag B", "Tag C", "Tag D"], named.Select(c => c.TextContent.Replace("flag", "").Trim()));

        var more = cut.Find(".odc-chip.tag.je-tagchips-more");
        Assert.StartsWith("+2", more.TextContent.Trim());
        Assert.Equal("Tag E, Tag F", more.GetAttribute("title"));
        Assert.Equal("more tags: Tag E, Tag F", more.QuerySelector(".sr-only")!.TextContent.Trim());
        Assert.Null(more.QuerySelector(".material-icons"));
    }

    [Theory]
    [InlineData(4, null)]
    [InlineData(4, 4)]
    [InlineData(4, 5)]
    [InlineData(3, 4)]
    public void At_or_under_the_cap_every_tag_shows_and_there_is_no_overflow_chip(int count, int? max)
    {
        var cut = Render(Tags(count), max);

        Assert.Equal(count, cut.FindAll(".odc-chip.tag").Count);
        Assert.Empty(cut.FindAll(".je-tagchips-more"));
    }

    [Fact]
    public void A_cap_of_zero_shows_only_the_overflow_chip()
    {
        var cut = Render(Tags(2), max: 0);

        var chip = Assert.Single(cut.FindAll(".odc-chip.tag"));
        Assert.Contains("je-tagchips-more", chip.ClassList);
        Assert.StartsWith("+2", chip.TextContent.Trim());
    }

    [Fact]
    public void For_resolves_ids_in_order_and_skips_one_the_map_does_not_hold()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var byId = new Dictionary<Guid, (string Name, string? Icon)> { [a] = ("Alpha", "flag"), [b] = ("Beta", null) };

        var tags = JournalTagChips.Tag.For([b, Guid.NewGuid(), a], byId, t => t.Name, t => t.Icon, _ => null);

        Assert.Equal([new JournalTagChips.Tag("Beta", null, null), new JournalTagChips.Tag("Alpha", "flag", null)], tags);
    }

    [Theory]
    [InlineData("Hidden names", "Hidden names")]
    [InlineData(null, null)]
    public void OdsChip_renders_its_title_only_when_set(string? title, string? expected)
    {
        var cut = ctx.Render<OdsChip>(p => p.Add(c => c.Title, title).AddChildContent("x"));

        Assert.Equal(expected, cut.Find(".odc-chip").GetAttribute("title"));
    }
}
