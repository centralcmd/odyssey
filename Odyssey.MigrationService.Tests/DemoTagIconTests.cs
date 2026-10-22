using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.TestData.Catalog;
using Odyssey.TestData.Generators;
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
            Assert.True(TagIcons.IsKnown(tag.Icon), $"Demo tag '{tag.Name}' seeds an unknown icon."));
        Assert.Contains(tags, tag => tag.Icon is not null);
        Assert.Contains(tags, tag => tag.Icon is null);
    }

    /// <summary>
    /// Issue #345: the same two guarantees for the journal, task and photo tag sets, so the demo stack
    /// shows an iconned and a default tag on each of the three tag pages.
    /// </summary>
    [Fact]
    public void Journal_task_and_photo_tags_seed_known_icons_and_both_paths()
    {
        var anchor = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var sets = new (string Kind, IReadOnlyList<(string Name, string? Icon)> Tags)[]
        {
            ("journal", JournalTagGenerator.Generate(anchor).Select(tag => (tag.Name, tag.Icon)).ToList()),
            ("task", JournalTaskTagGenerator.Generate(anchor).Select(tag => (tag.Name, tag.Icon)).ToList()),
            ("photo", PhotoGenerator.Generate(anchor).Tags.Select(tag => (tag.Name, tag.Icon)).ToList()),
        };

        foreach (var (kind, tags) in sets)
        {
            Assert.All(tags.Where(tag => tag.Icon is not null), tag =>
                Assert.True(TagIcons.IsKnown(tag.Icon), $"Demo {kind} tag '{tag.Name}' seeds an unknown icon."));
            Assert.Contains(tags, tag => tag.Icon is not null);
            Assert.Contains(tags, tag => tag.Icon is null);
        }
    }
}
