using Odyssey.Dtos.Finance;
using Odyssey.TestData.Catalog;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// Issue #279: every icon the demo seed gives a tag is a catalogue key. A key later removed from the
/// catalogue would otherwise seed as unknown and silently render as the default, hiding the iconned path
/// the seed exists to show. Some tags stay icon-less on purpose, so the default path is visible too.
/// </summary>
public class DemoTagIconTests
{
    [Fact]
    public void Every_seeded_icon_is_a_catalogue_key_and_both_paths_are_seeded()
    {
        var tags = Tags.Build();

        Assert.All(tags.Where(tag => tag.Icon is not null), tag =>
            Assert.True(TransactionTagIcons.IsKnown(tag.Icon), $"Demo tag '{tag.Name}' seeds an unknown icon."));
        Assert.Contains(tags, tag => tag.Icon is not null);
        Assert.Contains(tags, tag => tag.Icon is null);
    }
}
