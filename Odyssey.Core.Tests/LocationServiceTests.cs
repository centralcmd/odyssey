using Microsoft.Extensions.Logging;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// <see cref="LocationService"/> on the EF InMemory provider (issue #350). The InMemory provider enforces
/// no unique index, so the uniqueness assertions here exercise the service's pre-check alone; the index
/// and the 1062 race path are covered against real MariaDB in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class LocationServiceTests
{
    private const string UserId = "location-user";

    private static NewLocation Cabin(string name = "Cabin") => new()
    {
        Name = name,
        Description = "Key under the blue pot",
        AddressText = "Fjellveien 12, 2630 Ringebu",
        Latitude = 61.529144m,
        Longitude = 10.138016m,
        AltitudeMeters = 820,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Kartverket,
        SourceReference = "3439-1020-12",
    };

    private static UpdateLocation AsUpdate(ExistingLocation l, bool archived = false) => new()
    {
        Name = l.Name,
        Description = l.Description,
        AddressText = l.AddressText,
        Latitude = l.Latitude,
        Longitude = l.Longitude,
        AltitudeMeters = l.AltitudeMeters,
        TimeZoneId = l.TimeZoneId,
        Source = l.Source,
        SourceReference = l.SourceReference,
        Archived = archived,
    };

    [Fact]
    public async Task Create_round_trips_every_field_and_rounds_coordinates_to_five_decimals()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);

        var created = await service.Create(Cabin() with { Latitude = 59.912734m, Longitude = 10.750271m }, UserId);
        var read = await service.Get(created.LocationId);

        Assert.NotNull(read);
        Assert.Equal(59.91273m, read!.Latitude);
        Assert.Equal(10.75027m, read.Longitude);
        Assert.Equal("Cabin", read.Name);
        Assert.Equal("Key under the blue pot", read.Description);
        Assert.Equal("Fjellveien 12, 2630 Ringebu", read.AddressText);
        Assert.Equal(820, read.AltitudeMeters);
        Assert.Equal("Europe/Oslo", read.TimeZoneId);
        Assert.Equal(LocationSource.Kartverket, read.Source);
        Assert.Equal("3439-1020-12", read.SourceReference);
        Assert.Null(read.Archived);
    }

    [Theory]
    [InlineData(-61.529145, -61.52915)]
    [InlineData(61.529145, 61.52915)]
    public async Task Rounding_is_away_from_zero_in_both_directions(double input, double expected)
    {
        using var context = TestContextFactory.CreateJournal();
        var created = await new LocationService(context).Create(Cabin() with { Latitude = (decimal)input }, UserId);

        Assert.Equal((decimal)expected, created.Latitude);
    }

    [Theory]
    [InlineData("cabin")]
    [InlineData("CABIN")]
    [InlineData("Cabín")]
    [InlineData("  Cabin  ")]
    public async Task A_name_differing_only_by_case_or_accent_is_a_409_keyed_name(string name)
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        await service.Create(Cabin(), UserId);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => service.Create(Cabin(name), UserId));

        Assert.Equal("A location with this name already exists.", ex.Message);
        Assert.Contains("name", ex.Errors!.Keys);
    }

    [Fact]
    public async Task An_archived_row_still_reserves_its_name_and_the_message_says_it_is_archived()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        var cabin = await service.Create(Cabin(), UserId);
        await service.Update(cabin.LocationId, AsUpdate(cabin, archived: true), UserId);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => service.Create(Cabin("cabín"), UserId));

        Assert.Equal("An archived location with this name exists — restore it or choose another name.", ex.Message);
    }

    [Fact]
    public async Task Renaming_onto_itself_is_not_a_conflict()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        var cabin = await service.Create(Cabin(), UserId);

        var updated = await service.Update(cabin.LocationId, AsUpdate(cabin) with { Name = "CABIN" }, UserId);

        Assert.Equal("CABIN", updated!.Name);
    }

    [Fact]
    public async Task Renaming_onto_another_locations_name_is_a_409_keyed_name()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        await service.Create(Cabin(), UserId);
        var home = await service.Create(Cabin("Home"), UserId);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Update(home.LocationId, AsUpdate(home) with { Name = "cabín" }, UserId));

        Assert.Equal("A location with this name already exists.", ex.Message);
        Assert.Contains("name", ex.Errors!.Keys);
        Assert.Equal("Home", (await service.Get(home.LocationId))!.Name);
    }

    [Fact]
    public async Task Renaming_onto_an_archived_locations_name_says_it_is_archived()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        var cabin = await service.Create(Cabin(), UserId);
        await service.Update(cabin.LocationId, AsUpdate(cabin, archived: true), UserId);
        var home = await service.Create(Cabin("Home"), UserId);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.Update(home.LocationId, AsUpdate(home) with { Name = "Cabin" }, UserId));

        Assert.Equal("An archived location with this name exists — restore it or choose another name.", ex.Message);
    }

    [Fact]
    public async Task A_save_that_changes_nothing_leaves_the_edit_stamp_alone()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context, new StepClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)));
        var cabin = await service.Create(Cabin(), UserId);

        var saved = await service.Update(cabin.LocationId, AsUpdate(cabin), "someone-else");

        Assert.Equal(cabin.UpdatedAt, saved!.UpdatedAt);
        var entity = context.Locations.Single();
        Assert.Equal(UserId, entity.UpdatedByUserId);
    }

    [Fact]
    public async Task An_edit_by_another_member_stamps_the_editor_and_keeps_the_creator()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context, new StepClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)));
        var cabin = await service.Create(Cabin(), UserId);

        var saved = await service.Update(cabin.LocationId, AsUpdate(cabin) with { Description = "New key spot" }, "someone-else");

        Assert.True(saved!.UpdatedAt > cabin.UpdatedAt);
        Assert.Equal(cabin.CreatedAt, saved.CreatedAt);
        var entity = context.Locations.Single();
        Assert.Equal(UserId, entity.CreatedByUserId);
        Assert.Equal("someone-else", entity.UpdatedByUserId);
    }

    [Fact]
    public async Task Archive_hides_from_Active_shows_under_Archived_and_restore_brings_it_back()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        var cabin = await service.Create(Cabin(), UserId);
        await service.Create(Cabin("Home"), UserId);

        var archived = await service.Update(cabin.LocationId, AsUpdate(cabin, archived: true), UserId);
        Assert.NotNull(archived!.Archived);

        var active = await service.ListAsync(new LocationsQueryParams { Status = ArchivalStatus.Active });
        var archivedOnly = await service.ListAsync(new LocationsQueryParams { Status = ArchivalStatus.Archived });
        var all = await service.ListAsync(new LocationsQueryParams());

        Assert.Equal(["Home"], active.Items.Select(l => l.Name));
        Assert.Equal(["Cabin"], archivedOnly.Items.Select(l => l.Name));
        Assert.Equal(1, archivedOnly.TotalCount);
        Assert.Equal(2, all.TotalCount);

        var restored = await service.Update(cabin.LocationId, AsUpdate(archived, archived: false), UserId);
        Assert.Null(restored!.Archived);
    }

    [Fact]
    public async Task Update_of_an_unknown_id_is_null_not_an_upsert()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        var cabin = await service.Create(Cabin(), UserId);

        Assert.Null(await service.Update(Guid.NewGuid(), AsUpdate(cabin), UserId));
        Assert.Equal(1, (await service.ListAsync(new LocationsQueryParams())).TotalCount);
    }

    [Fact]
    public async Task Search_matches_name_description_and_address()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);
        await service.Create(Cabin(), UserId);
        await service.Create(Cabin("Home") with { Description = "Third floor", AddressText = "Storgata 1, 0155 Oslo" }, UserId);

        Assert.Equal(["Cabin"], (await service.ListAsync(new LocationsQueryParams { Search = "ringebu" })).Items.Select(l => l.Name));
        Assert.Equal(["Home"], (await service.ListAsync(new LocationsQueryParams { Search = "floor" })).Items.Select(l => l.Name));
        Assert.Equal(["Home"], (await service.ListAsync(new LocationsQueryParams { Search = "hom" })).Items.Select(l => l.Name));
    }

    // Created Bravo, Charlie, Alpha in that order, then Bravo edited last — so the creation order and the
    // edit order differ, and swapping the CreatedAt and UpdatedAt expressions fails a row.
    [Theory]
    [InlineData(LocationSortBy.Name, SortDirection.Asc, new[] { "Alpha", "Bravo", "Charlie" })]
    [InlineData(LocationSortBy.Name, SortDirection.Desc, new[] { "Charlie", "Bravo", "Alpha" })]
    [InlineData(LocationSortBy.CreatedAt, SortDirection.Asc, new[] { "Bravo", "Charlie", "Alpha" })]
    [InlineData(LocationSortBy.CreatedAt, SortDirection.Desc, new[] { "Alpha", "Charlie", "Bravo" })]
    [InlineData(LocationSortBy.UpdatedAt, SortDirection.Asc, new[] { "Charlie", "Alpha", "Bravo" })]
    [InlineData(LocationSortBy.UpdatedAt, SortDirection.Desc, new[] { "Bravo", "Alpha", "Charlie" })]
    public async Task Sorts_by_each_key_in_both_directions(LocationSortBy sortBy, SortDirection dir, string[] expected)
    {
        using var context = TestContextFactory.CreateJournal();
        var clock = new StepClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var service = new LocationService(context, clock);
        var created = new List<ExistingLocation>();
        foreach (var name in new[] { "Bravo", "Charlie", "Alpha" })
        {
            created.Add(await service.Create(Cabin(name), UserId));
        }

        await service.Update(created[0].LocationId, AsUpdate(created[0]) with { Description = "Edited" }, UserId);

        var rows = await service.ListAsync(new LocationsQueryParams { SortBy = sortBy, SortDir = dir });

        Assert.Equal(expected, rows.Items.Select(l => l.Name));
    }

    [Fact]
    public async Task Control_and_bidi_characters_are_stripped_from_stored_text()
    {
        using var context = TestContextFactory.CreateJournal();
        var service = new LocationService(context);

        var created = await service.Create(Cabin("Ca‮bin\u0007") with
        {
            Description = "Line one\r\nLine⁦ two\u0000",
            AddressText = "Fjellveien\t12\n, 2630",
        }, UserId);

        Assert.Equal("Cabin", created.Name);
        Assert.Equal("Line one\nLine two", created.Description);
        Assert.Equal("Fjellveien12, 2630", created.AddressText);
    }

    [Fact]
    public async Task An_all_whitespace_optional_field_is_stored_as_null()
    {
        using var context = TestContextFactory.CreateJournal();
        var created = await new LocationService(context).Create(Cabin() with { Description = "   ", AddressText = "\t" }, UserId);

        Assert.Null(created.Description);
        Assert.Null(created.AddressText);
    }

    [Theory]
    [InlineData(LocationSource.Kartverket, "© Kartverket")]
    [InlineData(LocationSource.OpenMeteo, "© Open-Meteo")]
    public async Task Attribution_is_derived_from_the_source(LocationSource source, string text)
    {
        using var context = TestContextFactory.CreateJournal();
        var created = await new LocationService(context).Create(
            Cabin() with { Source = source, SourceReference = source == LocationSource.OpenMeteo ? "2267057" : "3439-1020-12" }, UserId);

        Assert.Equal(text, created.Attribution!.Text);
        Assert.Equal("CC BY 4.0", created.Attribution.License);
    }

    [Fact]
    public async Task A_manual_location_has_no_attribution()
    {
        using var context = TestContextFactory.CreateJournal();
        var created = await new LocationService(context).Create(
            Cabin() with { Source = LocationSource.Manual, SourceReference = null }, UserId);

        Assert.Null(created.Attribution);
    }

    [Fact]
    public async Task The_audit_line_names_the_changed_fields_and_never_their_values()
    {
        using var context = TestContextFactory.CreateJournal();
        var logger = new RecordingLogger();
        var service = new LocationService(context, logger: logger);
        var cabin = await service.Create(Cabin(), UserId);

        await service.Update(cabin.LocationId, AsUpdate(cabin) with
        {
            AddressText = "Secret street 9",
            Latitude = 60.1m,
            Longitude = 9.2m,
        }, UserId);

        var update = Assert.Single(logger.Lines, line => line.Contains("updated", StringComparison.Ordinal));
        Assert.Contains("[AddressText, Latitude, Longitude]", update);
        Assert.Contains(UserId, update);
        foreach (var line in logger.Lines)
        {
            Assert.DoesNotContain("Secret street", line);
            Assert.DoesNotContain("Fjellveien", line);
            Assert.DoesNotContain("Cabin", line);
            Assert.DoesNotContain("blue pot", line);
            Assert.DoesNotContain("60.1", line);
        }
    }

    [Fact]
    public async Task Archive_and_restore_are_audited_as_such()
    {
        using var context = TestContextFactory.CreateJournal();
        var logger = new RecordingLogger();
        var service = new LocationService(context, logger: logger);
        var cabin = await service.Create(Cabin(), UserId);

        await service.Update(cabin.LocationId, AsUpdate(cabin, archived: true), UserId);
        await service.Update(cabin.LocationId, AsUpdate(cabin, archived: false), UserId);

        Assert.Contains(logger.Lines, line => line.Contains("archived by", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, line => line.Contains("restored by", StringComparison.Ordinal));
    }

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow()
        {
            now = now.AddMinutes(1);
            return now;
        }
    }

    private sealed class RecordingLogger : ILogger<LocationService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
