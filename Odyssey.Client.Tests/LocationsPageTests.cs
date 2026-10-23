using System.Text.RegularExpressions;
using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// What the Locations page offers by claim (issue #350 §7.2; design system · Locations.jsx): a Guest holds
/// <c>locations.read</c> only and sees the page read-only — no create verb, no edit, no archive.
/// </summary>
/// <remarks>
/// The menu and the empty-state copy are pure functions of the claims, so they are tested directly. The
/// page's create surfaces are checked by a source lint: the page is an <c>@page</c> whose rows arrive
/// through <c>OdsInfiniteList</c>, which renders nothing without the JS observer bUnit lacks (the same
/// trade-off <see cref="ContractsCardRowActionTests"/> records).
/// </remarks>
public class LocationsPageTests
{
    private static ExistingLocation Row(bool archived = false) => new()
    {
        LocationId = Guid.NewGuid(),
        Name = "Cabin",
        TimeZoneId = "Europe/Oslo",
        Archived = archived ? DateTime.UtcNow : null,
    };

    private static IReadOnlyList<string?> Labels(ExistingLocation row, bool canUpdate) =>
        [.. LocationsPage.BuildRowActions(row, canUpdate, default, default, default, default)
            .Where(i => !i.Divider).Select(i => i.Label)];

    [Fact]
    public void A_reader_without_update_gets_only_the_copies()
    {
        Assert.Equal(["Copy coordinates", "Copy ID"], Labels(Row(), canUpdate: false));
    }

    [Fact]
    public void An_editor_gets_edit_and_archive_and_never_delete()
    {
        Assert.Equal(["Edit location", "Copy coordinates", "Copy ID", "Archive"], Labels(Row(), canUpdate: true));
        Assert.Equal("Restore", Labels(Row(archived: true), canUpdate: true)[^1]);
    }

    [Fact]
    public void The_empty_state_offers_a_guest_nothing_they_cannot_use()
    {
        Assert.Contains("Someone with permission", LocationsPage.EmptyDescriptionFor(canCreate: false));
        Assert.DoesNotContain("Someone with permission", LocationsPage.EmptyDescriptionFor(canCreate: true));
    }

    [Theory]
    [InlineData(@"ShowPrimaryAction=""_canCreate""")]
    [InlineData(@"<CreateAction>\s*@if \(_canCreate\)")]
    [InlineData(@"<Trailing>\s*@if \(_canCreate\)")]
    public void Every_create_surface_is_gated_on_locations_create(string pattern)
    {
        var markup = File.ReadAllText(Path.Combine(ClientSource.Root, "Pages", "Journal", "LocationsPage.razor"));

        Assert.Matches(new Regex(pattern), markup);
    }
}
