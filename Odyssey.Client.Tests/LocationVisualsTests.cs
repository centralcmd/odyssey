using Odyssey.Client.Pages.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>The Locations page's display rules (issue #350; design system · locations-data.js helpers).</summary>
public class LocationVisualsTests
{
    [Theory]
    [InlineData("59.912734", "59.91273")]
    [InlineData("59.912735", "59.91274")]
    [InlineData("-0.000005", "-0.00001")]
    public void Round_matches_the_servers_five_decimals_away_from_zero(string typed, string stored) =>
        Assert.Equal(decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture),
            LocationVisuals.Round(decimal.Parse(typed, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void WillRound_is_true_only_past_five_decimals()
    {
        Assert.False(LocationVisuals.WillRound(59.91273m));
        Assert.True(LocationVisuals.WillRound(59.912731m));
        Assert.False(LocationVisuals.WillRound(null));
    }

    [Fact]
    public void Coordinates_read_in_hemisphere_words_not_by_sign_alone()
    {
        Assert.Equal("59.91273, 10.75027", LocationVisuals.FormatPair(59.91273m, 10.75027m));
        Assert.Equal("33.86882° S · 151.20929° E", LocationVisuals.FormatHemispheres(-33.86882m, 151.20929m));
        Assert.Equal("40.71280° N · 74.00600° W", LocationVisuals.FormatHemispheres(40.7128m, -74.006m));
    }

    [Fact]
    public void Now_there_states_the_offset_and_the_local_time()
    {
        var summer = new DateTime(2026, 7, 1, 12, 5, 0, DateTimeKind.Utc);

        Assert.Equal("UTC+02:00 now · 14:05 there", LocationVisuals.NowThere("Europe/Oslo", summer));
        Assert.Equal("UTC−04:00 now · 08:05 there", LocationVisuals.NowThere("America/New_York", summer));
        Assert.Null(LocationVisuals.NowThere("Mars/Olympus_Mons", summer));
    }

    [Fact]
    public void The_picker_lists_only_iana_ids_the_server_accepts()
    {
        Assert.Contains("Europe/Oslo", LocationVisuals.TimeZoneIds);
        Assert.All(LocationVisuals.TimeZoneIds, id => Assert.True(id == "UTC" || id.Contains('/'), id));
    }

    [Fact]
    public void Credits_name_each_source_among_the_rows_once_and_none_for_manual_rows()
    {
        var kartverket = new LocationAttribution { Text = "© Kartverket", License = "CC BY 4.0", Url = "https://www.kartverket.no/" };
        ExistingLocation Row(LocationAttribution? attribution) => new()
        {
            LocationId = Guid.NewGuid(), Name = "x", TimeZoneId = "UTC", Attribution = attribution,
        };

        Assert.Single(LocationVisuals.Credits([Row(kartverket), Row(kartverket with { }), Row(null)]));
        Assert.Empty(LocationVisuals.Credits([Row(null)]));
    }

    [Theory]
    [InlineData(LocationSearchRegion.Norway, 2)]
    [InlineData(LocationSearchRegion.World, 3)]
    public void The_regions_minimum_is_the_shared_rule(LocationSearchRegion region, int min) =>
        Assert.Equal(min, LocationVisuals.RegionOf(region).MinLength);

    [Theory]
    [InlineData("https://www.kartverket.no/", true)]
    [InlineData("http://example.com/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void Attribution_links_only_https(string? url, bool expected) =>
        Assert.Equal(expected, LocationAttributionLink.IsSafeUrl(url));

    [Fact]
    public void Archive_and_restore_carry_every_other_field_forward()
    {
        var row = new ExistingLocation
        {
            LocationId = Guid.NewGuid(), Name = "Cabin", Description = "d", AddressText = "a",
            Latitude = 61.5m, Longitude = 10.1m, AltitudeMeters = 640, TimeZoneId = "Europe/Oslo",
            Source = LocationSource.Kartverket, SourceReference = "3439-1-4",
        };

        var body = LocationsPage.WithArchived(row, archived: true);

        Assert.True(body.Archived);
        Assert.Equal(("Cabin", "d", "a", 61.5m, 10.1m, 640, "Europe/Oslo", LocationSource.Kartverket, "3439-1-4"),
            (body.Name, body.Description, body.AddressText, body.Latitude!.Value, body.Longitude!.Value, body.AltitudeMeters!.Value,
             body.TimeZoneId, body.Source!.Value, body.SourceReference));
    }

    [Theory]
    [InlineData("name", "Name")]
    [InlineData("created", "CreatedAt")]
    [InlineData("updated", "UpdatedAt")]
    public void Toolbar_sort_keys_map_onto_LocationSortBy(string key, string sortBy) =>
        Assert.Equal(sortBy, LocationsPage.SortByFor(key));
}
