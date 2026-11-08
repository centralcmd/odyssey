using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Api.Tests;

/// <summary>
/// Unified locations over HTTP (issue #380 §12) — every acceptance criterion the EF InMemory tier can run.
/// The relational-only ones (row locks, merge locking, delete races, <c>1062</c>/<c>1452</c>, the
/// <c>AllUses</c> recount under <c>FOR UPDATE</c>, "nothing deleted implicitly") are in
/// <c>Odyssey.IntegrationTests/LocationUnificationRelationalTests</c>.
/// </summary>
public class LocationUnificationApiTests
{
    private const string Actor = "unified-locations-actor";
    private const string LocationsPath = "/api/locations";
    private const string ContactsPath = "/api/contacts";
    private const string PropertiesPath = "/api/properties";
    private const string EventsPath = "/api/calendar-events";
    private const string SeriesPath = "/api/recurrence-patterns";
    private const string EntriesPath = "/api/journal-entries";

    private static readonly string[] LocationClaims =
        [PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate, PermissionClaims.LocationsDelete];

    private static readonly string[] Everything =
    [
        .. LocationClaims,
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate, PermissionClaims.ContactsDelete,
        PermissionClaims.PropertiesRead, PermissionClaims.PropertiesCreate, PermissionClaims.PropertiesUpdate, PermissionClaims.PropertiesDelete,
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate, PermissionClaims.CalendarDelete,
        PermissionClaims.JournalRead, PermissionClaims.JournalCreate, PermissionClaims.JournalUpdate, PermissionClaims.JournalDelete,
        PermissionClaims.WeatherRead, PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate,
    ];

    // ── Locations: wire, validation, filters (AC 17, 23, 24; §5.5) ──────────────────────────────────

    [Fact]
    public async Task A_place_without_coordinates_reads_with_nulls_and_hasCoordinates_false()
    {
        await using var host = await Host.StartAsync(Everything);
        var created = await host.PostLocationAsync(Place("Storgata 1", "Oslo"));

        Assert.Null(created.Latitude);
        Assert.Null(created.Longitude);
        Assert.Null(created.TimeZoneId);
        Assert.False(created.HasCoordinates);
        Assert.Equal("Storgata 1", created.Line1);
        Assert.Equal("NO", created.CountryCode);
    }

    [Fact]
    public async Task Partial_coordinates_are_422_keyed_latitude()
    {
        await using var host = await Host.StartAsync(Everything);

        var response = await host.Client.PostAsJsonAsync(LocationsPath, Place("Storgata 1", "Oslo") with { Latitude = 59.9m });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "latitude");
    }

    [Theory]
    [InlineData(LocationSource.Converted)]
    [InlineData(LocationSource.ContactAddress)]
    [InlineData(LocationSource.PropertyAddress)]
    [InlineData(LocationSource.Imported)]
    public async Task Post_with_an_application_source_is_422_keyed_source(LocationSource source)
    {
        await using var host = await Host.StartAsync(Everything);

        var response = await host.Client.PostAsJsonAsync(LocationsPath, Place("Storgata 1", "Oslo") with { Source = source });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "source");
    }

    [Fact]
    public async Task Put_may_keep_a_stored_application_source_or_change_it_to_Kartverket_but_not_to_another()
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Converted place", source: LocationSource.Converted);

        var keep = await host.Client.PutAsJsonAsync($"{LocationsPath}/{id}", Update("Converted place", LocationSource.Converted));
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);

        var toImported = await host.Client.PutAsJsonAsync($"{LocationsPath}/{id}", Update("Converted place", LocationSource.Imported));
        await AssertProblem(toImported, HttpStatusCode.UnprocessableEntity, "source");

        var geocoded = await host.Client.PutAsJsonAsync($"{LocationsPath}/{id}", Update("Converted place", LocationSource.Kartverket) with
        {
            Latitude = 59.9m, Longitude = 10.7m, TimeZoneId = "Europe/Oslo",
        });
        Assert.Equal(HttpStatusCode.OK, geocoded.StatusCode);
        Assert.Equal(LocationSource.Kartverket, (await geocoded.Content.ReadFromJsonAsync<ExistingLocation>())!.Source);
    }

    [Fact]
    public async Task An_application_source_carries_no_reference()
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Converted place", source: LocationSource.Converted);

        var response = await host.Client.PutAsJsonAsync(
            $"{LocationsPath}/{id}", Update("Converted place", LocationSource.Converted) with { SourceReference = "123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_to_an_address_another_place_holds_is_409_keyed_line1()
    {
        await using var host = await Host.StartAsync(Everything);
        await host.PostLocationAsync(Place("Storgata 1", "Oslo"));
        var other = await host.PostLocationAsync(Place("Storgata 2", "Oslo"));

        var response = await host.Client.PutAsJsonAsync($"{LocationsPath}/{other.LocationId}", Update(other.Name, LocationSource.Manual) with
        {
            Line1 = "STORGATA  1", City = "oslo", CountryCode = "no",
        });

        await AssertProblem(response, HttpStatusCode.Conflict, "line1");
    }

    [Fact]
    public async Task Put_that_leaves_a_contact_address_incomplete_is_422_keyed_on_the_first_missing_field()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));

        var response = await host.Client.PutAsJsonAsync($"{LocationsPath}/{address.LocationId}", Update("Edited", LocationSource.ContactAddress) with
        {
            Line1 = "Storgata 1", City = null, CountryCode = null,
        });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "city");
    }

    [Fact]
    public async Task Put_clearing_the_coordinates_of_a_place_linked_to_weather_is_allowed()
    {
        // Issue #387 AC 23: a weather point keeps its own position, so the place's coordinates are its own.
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Cabin", latitude: 61.5m);
        await host.SeedWeatherAsync(id);

        var response = await host.Client.PutAsJsonAsync($"{LocationsPath}/{id}", Update("Cabin", LocationSource.Manual));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_on_a_merged_away_place_is_422_keyed_id()
    {
        await using var host = await Host.StartAsync(Everything);
        var target = await host.SeedLocationAsync("Target");
        var source = await host.SeedLocationAsync("Forwarder", mergedInto: target, archived: true);

        var response = await host.Client.PutAsJsonAsync($"{LocationsPath}/{source}", Update("Forwarder", LocationSource.Manual));

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "id");
    }

    [Fact]
    public async Task The_list_filters_on_coordinates_and_on_being_referenced()
    {
        await using var host = await Host.StartAsync(Everything);
        var geocoded = await host.SeedLocationAsync("Geocoded", latitude: 60m);
        var bare = await host.SeedLocationAsync("Bare");
        var used = await host.SeedLocationAsync("Used");
        await host.SeedEventAsync(used);

        var withCoordinates = await host.ListLocationIdsAsync("?hasCoordinates=true");
        var withoutCoordinates = await host.ListLocationIdsAsync("?hasCoordinates=false");
        var referenced = await host.ListLocationIdsAsync("?referenced=true");
        var unused = await host.ListLocationIdsAsync("?referenced=false");

        Assert.Equal([geocoded], withCoordinates);
        Assert.Equivalent(new[] { bare, used }, withoutCoordinates);
        Assert.Equal([used], referenced);
        Assert.Equivalent(new[] { geocoded, bare }, unused);
    }

    [Fact]
    public async Task Usage_counts_each_readable_kind_and_is_null_for_a_kind_the_caller_cannot_read()
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Home", line1: "Storgata 1", city: "Oslo", country: "NO");
        await host.SeedEventAsync(id);
        await host.SeedEventAsync(id);
        await host.SeedSeriesAsync(id);
        await host.SeedEntryAsync(id);
        var contactId = await host.PostContactAsync("Acme");
        await host.PostAddressAsync(contactId, new NewAddress { Label = AddressLabel.Visiting, LocationId = id });

        var full = (await host.Client.GetFromJsonAsync<ExistingLocation>($"{LocationsPath}/{id}"))!;
        Assert.Equal(new LocationUsage { Contacts = 1, Properties = 0, CalendarEvents = 2, RecurringSeries = 1, JournalEntries = 1 }, full.Usage);

        await using var reader = await host.SharingAsync([PermissionClaims.LocationsRead, PermissionClaims.ContactsRead]);
        var json = await reader.Client.GetStringAsync($"{LocationsPath}/{id}");
        using var document = JsonDocument.Parse(json);
        var usage = document.RootElement.GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("contacts").GetInt32());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("calendarEvents").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("recurringSeries").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("journalEntries").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("properties").ValueKind);
        Assert.False(usage.TryGetProperty("total", out _));
    }

    // ── Delete (AC 11) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_an_unused_place_is_204_and_it_is_gone()
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Unused");

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{LocationsPath}/{id}")).StatusCode);
        Assert.Equal(0, await host.ReadAsync(c => c.Locations.CountAsync(l => l.LocationId == id)));
    }

    /// <summary>
    /// Issue #387 §8: a place's weather link, a weather preference and the household default name a weather
    /// POINT, not a place, so none of them blocks deleting the place; the link simply goes with it.
    /// </summary>
    [Fact]
    public async Task Weather_never_blocks_deleting_a_place()
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Linked");
        var point = await host.SeedWeatherAsync(id);
        await host.SeedPreferenceAsync(point);
        await host.Factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherDefaultPointId, point.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"{LocationsPath}/{id}")).StatusCode);
        Assert.Equal(1, await host.ReadAsync(c => c.WeatherPoints.CountAsync(p => p.WeatherPointId == point)));
    }

    private static readonly string[] ReferenceKinds =
        ["event", "series", "entry", "address", "property", "forwarder"];

    public static TheoryData<string> References => new(ReferenceKinds);

    [Theory]
    [MemberData(nameof(References))]
    public async Task Deleting_a_referenced_place_is_409_with_a_generic_body(string reference)
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("In use", line1: "Storgata 1", city: "Oslo", country: "NO");
        switch (reference)
        {
            case "event": await host.SeedEventAsync(id); break;
            case "series": await host.SeedSeriesAsync(id); break;
            case "entry": await host.SeedEntryAsync(id); break;
            case "address":
                await host.PostAddressAsync(await host.PostContactAsync("Acme"), new NewAddress { Label = AddressLabel.Visiting, LocationId = id });
                break;
            case "property": await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, LocationId = id }); break;
            case "forwarder": await host.SeedLocationAsync("Forwarder", mergedInto: id, archived: true); break;
        }

        var response = await host.Client.DeleteAsync($"{LocationsPath}/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(reference, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Storgata", body, StringComparison.Ordinal);
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync(l => l.LocationId == id)));
    }

    /// <summary>
    /// AC 11 (senior-tester L2): "generic" means the six refusals are indistinguishable — one body whatever
    /// holds the place, so the 409 is no oracle for WHICH kind of record uses it. Each place is referenced by
    /// exactly one kind; the route id and the trace id are the only parts allowed to differ.
    /// </summary>
    [Fact]
    public async Task The_six_delete_refusals_have_one_identical_body()
    {
        await using var host = await Host.StartAsync(Everything);
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in ReferenceKinds)
        {
            var id = await host.SeedLocationAsync($"In use by {reference}", line1: $"Storgata {bodies.Count + 1}", city: "Oslo", country: "NO");
            switch (reference)
            {
                case "event": await host.SeedEventAsync(id); break;
                case "series": await host.SeedSeriesAsync(id); break;
                case "entry": await host.SeedEntryAsync(id); break;
                case "address":
                    await host.PostAddressAsync(await host.PostContactAsync("Acme"), new NewAddress { Label = AddressLabel.Visiting, LocationId = id });
                    break;
                case "property": await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, LocationId = id }); break;
                case "forwarder": await host.SeedLocationAsync("Forwarder", mergedInto: id, archived: true); break;
            }

            var response = await host.Client.DeleteAsync($"{LocationsPath}/{id}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            problem.Remove("traceId");
            bodies[reference] = problem.ToJsonString().Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(6, bodies.Count);
        Assert.Single(bodies.Values.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Deleting_needs_locations_delete()
    {
        await using var host = await Host.StartAsync(Everything);
        var id = await host.SeedLocationAsync("Unused");
        await using var updater = await host.SharingAsync(
            [PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate, PermissionClaims.LocationsUpdate]);

        Assert.Equal(HttpStatusCode.Forbidden, (await updater.Client.DeleteAsync($"{LocationsPath}/{id}")).StatusCode);
    }

    // ── Merge (AC 13) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_repoints_every_kind_dedupes_contact_rows_and_leaves_a_forwarder()
    {
        await using var host = await Host.StartAsync(Everything);
        var source = await host.SeedLocationAsync("Source", line1: "Storgata 1", city: "Oslo", country: "NO");
        var target = await host.SeedLocationAsync("Target", line1: "Storgata 2", city: "Oslo", country: "NO", latitude: 59m);
        var olderForwarder = await host.SeedLocationAsync("Older", mergedInto: source, archived: true);
        var eventId = await host.SeedEventAsync(source);
        var seriesId = await host.SeedSeriesAsync(source);
        var entryId = await host.SeedEntryAsync(source);
        var both = await host.PostContactAsync("Both");
        await host.PostAddressAsync(both, new NewAddress { Label = AddressLabel.Visiting, LocationId = target });
        var primary = await host.PostAddressAsync(both, new NewAddress { Label = AddressLabel.Billing, LocationId = source, IsPrimary = true });
        var one = await host.PostContactAsync("One");
        await host.PostAddressAsync(one, new NewAddress { Label = AddressLabel.Visiting, LocationId = source });
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, LocationId = source });

        var response = await host.Client.PostAsJsonAsync($"{LocationsPath}/{source}/merge", new MergeLocationRequest { TargetLocationId = target });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var merged = (await response.Content.ReadFromJsonAsync<ExistingLocation>())!;
        Assert.Equal(target, merged.LocationId);
        Assert.Equal("Storgata 2", merged.Line1);
        Assert.Equal(59m, merged.Latitude);

        await host.ReadAsync(async c =>
        {
            Assert.Equal(target, (await c.CalendarEvents.SingleAsync(e => e.CalendarEventId == eventId)).LocationId);
            Assert.Equal(target, (await c.RecurrencePatterns.SingleAsync(p => p.RecurrencePatternId == seriesId)).LocationId);
            Assert.Equal(target, (await c.JournalEntries.SingleAsync(e => e.JournalEntryId == entryId)).LocationId);
            Assert.Equal(target, (await c.RealEstateDetails.SingleAsync(d => d.PropertyId == property)).LocationId);
            var bothRows = await c.Addresses.Where(a => a.ContactId == both).ToListAsync();
            var kept = Assert.Single(bothRows);
            Assert.Equal(target, kept.LocationId);
            Assert.True(kept.IsPrimary);
            Assert.Equal(target, (await c.Addresses.SingleAsync(a => a.ContactId == one)).LocationId);
            var gone = await c.Locations.SingleAsync(l => l.LocationId == source);
            Assert.NotNull(gone.Archived);
            Assert.Equal(target, gone.MergedIntoLocationId);
            Assert.Equal(target, (await c.Locations.SingleAsync(l => l.LocationId == olderForwarder)).MergedIntoLocationId);
            return 0;
        });
        Assert.NotEqual(Guid.Empty, primary.Id);

        // A later find-or-create of the source's address follows the forwarder to the target.
        var later = await host.PostContactAsync("Later");
        var linked = await host.PostAddressAsync(later, Address("Storgata 1", "Oslo"));
        Assert.Equal(target, linked.LocationId);
    }

    [Fact]
    public async Task Merge_without_a_class_claim_is_a_generic_403_and_changes_nothing()
    {
        await using var host = await Host.StartAsync(Everything);
        var source = await host.SeedLocationAsync("Source");
        var target = await host.SeedLocationAsync("Target");
        var entryId = await host.SeedEntryAsync(source);
        await using var noJournal = await host.SharingAsync(
            [.. LocationClaims, PermissionClaims.CalendarUpdate, PermissionClaims.ContactsUpdate, PermissionClaims.PropertiesUpdate]);

        var response = await noJournal.Client.PostAsJsonAsync($"{LocationsPath}/{source}/merge", new MergeLocationRequest { TargetLocationId = target });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("journal", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, await host.ReadAsync(c => c.JournalEntries.Where(e => e.JournalEntryId == entryId).Select(e => e.LocationId).SingleAsync()));
        Assert.Null(await host.ReadAsync(c => c.Locations.Where(l => l.LocationId == source).Select(l => l.MergedIntoLocationId).SingleAsync()));
    }

    [Fact]
    public async Task Merge_refusals_are_404_and_422_on_the_documented_keys()
    {
        await using var host = await Host.StartAsync(Everything);
        var source = await host.SeedLocationAsync("Source");
        var target = await host.SeedLocationAsync("Target");
        var archived = await host.SeedLocationAsync("Archived", archived: true);
        Task<HttpResponseMessage> Merge(Guid from, Guid to) =>
            host.Client.PostAsJsonAsync($"{LocationsPath}/{from}/merge", new MergeLocationRequest { TargetLocationId = to });

        Assert.Equal(HttpStatusCode.NotFound, (await Merge(Guid.NewGuid(), target)).StatusCode);
        await AssertProblem(await Merge(source, source), HttpStatusCode.UnprocessableEntity, "targetLocationId");
        await AssertProblem(await Merge(source, archived), HttpStatusCode.UnprocessableEntity, "targetLocationId");
        await AssertProblem(await Merge(archived, target), HttpStatusCode.UnprocessableEntity, "sourceId");
    }

    // ── Events, series, entries (§5.2) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_event_links_by_id_and_reads_back_the_embed_only_for_a_locations_read_holder()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Cabin", latitude: 61m);
        var calendar = await host.SeedCalendarAsync();

        var created = await host.Client.PostAsJsonAsync(EventsPath, NewEvent(calendar, place, "room 3"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = (await created.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
        Assert.Equal(place, dto.LocationId);
        Assert.Equal("room 3", dto.Location);
        Assert.Equal(new LocationRef { LocationId = place, Name = "Cabin", Archived = false, HasCoordinates = true }, dto.LocationRef);

        await using var noLocations = await host.SharingAsync([PermissionClaims.CalendarRead]);
        var bare = (await noLocations.Client.GetFromJsonAsync<ExistingCalendarEvent>($"{EventsPath}/{dto.CalendarEventId}"))!;
        Assert.Equal(place, bare.LocationId);
        Assert.Null(bare.LocationRef);
    }

    [Fact]
    public async Task An_event_link_to_an_unknown_or_archived_place_is_422_and_without_locations_read_403()
    {
        await using var host = await Host.StartAsync(Everything);
        var archived = await host.SeedLocationAsync("Old", archived: true);
        var place = await host.SeedLocationAsync("Cabin");
        var calendar = await host.SeedCalendarAsync();

        await AssertProblem(await host.Client.PostAsJsonAsync(EventsPath, NewEvent(calendar, Guid.NewGuid())), HttpStatusCode.UnprocessableEntity, "locationId");
        await AssertProblem(await host.Client.PostAsJsonAsync(EventsPath, NewEvent(calendar, archived)), HttpStatusCode.UnprocessableEntity, "locationId");

        await using var writer = await host.SharingAsync([PermissionClaims.CalendarCreate, PermissionClaims.CalendarRead, PermissionClaims.CalendarUpdate]);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.Client.PostAsJsonAsync(EventsPath, NewEvent(calendar, place))).StatusCode);
    }

    [Fact]
    public async Task An_unchanged_event_link_is_kept_without_a_claim_even_to_a_place_archived_since_and_an_omitted_one_clears()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Cabin");
        var calendar = await host.SeedCalendarAsync();
        var created = (await (await host.Client.PostAsJsonAsync(EventsPath, NewEvent(calendar, place))).Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
        await host.ArchiveLocationAsync(place);

        await using var writer = await host.SharingAsync([PermissionClaims.CalendarRead, PermissionClaims.CalendarUpdate]);
        var kept = await writer.Client.PutAsJsonAsync($"{EventsPath}/{created.CalendarEventId}", NewEvent(calendar, place, "renamed"));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(place, (await kept.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!.LocationId);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await writer.Client.PutAsJsonAsync($"{EventsPath}/{created.CalendarEventId}", NewEvent(calendar, null))).StatusCode);

        var cleared = await host.Client.PutAsJsonAsync($"{EventsPath}/{created.CalendarEventId}", NewEvent(calendar, null));
        Assert.Null((await cleared.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!.LocationId);
    }

    [Fact]
    public async Task The_event_series_and_entry_lists_filter_on_locationId()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Cabin");
        var linkedEvent = await host.SeedEventAsync(place);
        await host.SeedEventAsync(null);
        var linkedSeries = await host.SeedSeriesAsync(place);
        await host.SeedSeriesAsync(null);
        var linkedEntry = await host.SeedEntryAsync(place);
        await host.SeedEntryAsync(null);

        var events = await host.Client.GetFromJsonAsync<PagedResult<ExistingCalendarEvent>>(
            $"{EventsPath}?from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z&locationId={place}");
        var series = await host.Client.GetFromJsonAsync<PagedResult<ExistingRecurrencePattern>>($"{SeriesPath}?locationId={place}");
        var entries = await host.Client.GetFromJsonAsync<PagedResult<JournalEntrySummary>>($"{EntriesPath}?locationId={place}");

        Assert.Equal([linkedEvent], events!.Items.Select(e => e.CalendarEventId));
        Assert.Equal([linkedSeries], series!.Items.Select(p => p.RecurrencePatternId));
        Assert.Equal([linkedEntry], entries!.Items.Select(e => e.JournalEntryId));
        Assert.Equal("Cabin", entries.Items[0].LocationRef!.Name);
    }

    [Fact]
    public async Task A_series_copies_its_place_onto_every_generated_occurrence()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Pool");
        var calendar = await host.SeedCalendarAsync();

        var response = await host.Client.PostAsJsonAsync(SeriesPath, new NewRecurrencePattern
        {
            CalendarId = calendar,
            Title = "Swim",
            StartDateTime = new DateTime(2026, 1, 5, 17, 0, 0, DateTimeKind.Utc),
            EndDateTime = new DateTime(2026, 1, 5, 18, 0, 0, DateTimeKind.Utc),
            Frequency = Odyssey.Dtos.Journal.RecurrenceFrequency.Daily,
            OccurrenceCount = 3,
            LocationId = place,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var series = (await response.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
        Assert.Equal(place, series.LocationId);
        Assert.Equal([place, place, place], await host.ReadAsync(c => c.CalendarEvents
            .Where(e => e.RecurrencePatternId == series.RecurrencePatternId).Select(e => e.LocationId).ToListAsync()));
    }

    [Fact]
    public async Task A_journal_entry_links_by_id_and_a_nested_location_object_changes_no_place()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Cabin");

        var body = new Dictionary<string, object?>
        {
            ["title"] = "Trip", ["content"] = "Nice", ["entryDate"] = "2026-01-10T00:00:00Z",
            ["locationId"] = place,
            ["locationRef"] = new { locationId = place, name = "Hijacked", archived = true },
            ["location"] = "by the lake",
        };
        var response = await host.Client.PostAsJsonAsync(EntriesPath, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == place));
        Assert.Equal("Cabin", stored.Name);
        Assert.Null(stored.Archived);
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    // ── Contact addresses (§5.3; AC 5–9, 14, 24–26) ────────────────────────────────────────────────

    [Fact]
    public async Task Two_contacts_at_one_address_share_one_place_case_variants_included()
    {
        await using var host = await Host.StartAsync(Everything);
        var first = await host.PostAddressAsync(await host.PostContactAsync("A"), Address("Storgata 1", "Oslo"));
        var second = await host.PostAddressAsync(await host.PostContactAsync("B"), Address("STORGATA  1", "oslo") with { CountryCode = "no" });

        Assert.Equal(first.LocationId, second.LocationId);
        Assert.Equal(1, second.OtherUses);
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Fact]
    public async Task A_contact_address_reads_under_contacts_read_alone_with_its_location_id()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));
        await using var reader = await host.SharingAsync([PermissionClaims.ContactsRead]);

        var rows = (await reader.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{contactId}/addresses"))!;
        var row = Assert.Single(rows);
        Assert.Equal(address.LocationId, row.LocationId);
        Assert.Equal("Storgata 1", row.Line1);
        Assert.Equal("Oslo", row.City);
        Assert.Equal("NO", row.CountryCode);
        Assert.False(row.PlaceHasCoordinates);
    }

    [Fact]
    public async Task Linking_by_id_needs_locations_read_and_the_fields_path_needs_locations_create()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Office", line1: "Storgata 1", city: "Oslo", country: "NO");
        var contactId = await host.PostContactAsync("Acme");
        await host.PostAddressAsync(await host.PostContactAsync("Other"), Address("Kirkegata 2", "Oslo"));
        await using var contactsOnly = await host.SharingAsync([PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate]);
        var path = $"{ContactsPath}/{contactId}/addresses";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await contactsOnly.Client.PostAsJsonAsync(path, new NewAddress { Label = AddressLabel.Visiting, LocationId = place })).StatusCode);
        // The fields path is refused before the lookup: an existing address and a new one look the same.
        Assert.Equal(HttpStatusCode.Forbidden, (await contactsOnly.Client.PostAsJsonAsync(path, Address("Kirkegata 2", "Oslo"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await contactsOnly.Client.PostAsJsonAsync(path, Address("Nowhere 9", "Oslo"))).StatusCode);
    }

    [Fact]
    public async Task Linking_a_contact_by_id_to_an_incomplete_place_is_422_keyed_locationId()
    {
        await using var host = await Host.StartAsync(Everything);
        var cityOnly = await host.SeedLocationAsync("Somewhere", city: "Oslo");
        var contactId = await host.PostContactAsync("Acme");

        var response = await host.Client.PostAsJsonAsync(
            $"{ContactsPath}/{contactId}/addresses", new NewAddress { Label = AddressLabel.Visiting, LocationId = cityOnly });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "locationId");
    }

    [Fact]
    public async Task Missing_fields_without_a_locationId_are_422_keyed_on_the_first_missing_one()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");

        var response = await host.Client.PostAsJsonAsync(
            $"{ContactsPath}/{contactId}/addresses", new NewAddress { Label = AddressLabel.Visiting, Line1 = "Storgata 1" });

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "city");
    }

    [Fact]
    public async Task An_id_with_differing_fields_is_422_and_a_round_tripped_address_is_accepted()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));
        var path = $"{ContactsPath}/{contactId}/addresses";

        var differing = new NewAddress { Label = AddressLabel.Visiting, LocationId = address.LocationId, Line1 = "Storgata 9", City = "Oslo", CountryCode = "NO" };
        await AssertProblem(await host.Client.PutAsJsonAsync($"{path}/{address.Id}", differing), HttpStatusCode.UnprocessableEntity, "locationId");

        // The read shape, posted back as it came (extra members included), is accepted on PUT and POST.
        var roundTrip = JsonContent.Create(address);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsync($"{path}/{address.Id}", roundTrip)).StatusCode);
        var other = await host.PostContactAsync("Other");
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsync($"{ContactsPath}/{other}/addresses", JsonContent.Create(address))).StatusCode);
    }

    [Fact]
    public async Task A_contact_listing_a_place_twice_is_409_keyed_locationId()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));

        var response = await host.Client.PostAsJsonAsync($"{ContactsPath}/{contactId}/addresses", Address("Storgata 1", "Oslo"));

        await AssertProblem(response, HttpStatusCode.Conflict, "locationId");
    }

    [Fact]
    public async Task A_changed_address_without_applyTo_is_422_keyed_applyTo()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));

        var response = await host.Client.PutAsJsonAsync($"{ContactsPath}/{contactId}/addresses/{address.Id}", Address("Storgata 2", "Oslo"));

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "applyTo");
    }

    [Fact]
    public async Task ThisRecord_moves_only_this_link_and_leaves_the_old_place_unchanged()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var b = await host.PostContactAsync("B");
        var first = await host.PostAddressAsync(a, Address("Storgata 1", "Oslo"));
        await host.PostAddressAsync(b, Address("Storgata 1", "Oslo"));

        var response = await host.Client.PutAsJsonAsync(
            $"{ContactsPath}/{a}/addresses/{first.Id}", Address("Storgata 2", "Oslo") with { ApplyTo = AddressApplyTo.ThisRecord });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var moved = Assert.Single((await host.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{a}/addresses"))!);
        var stayed = Assert.Single((await host.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{b}/addresses"))!);
        Assert.NotEqual(first.LocationId, moved.LocationId);
        Assert.Equal("Storgata 2", moved.Line1);
        Assert.Equal(first.LocationId, stayed.LocationId);
        Assert.Equal("Storgata 1", stayed.Line1);
        Assert.Equal(2, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Fact]
    public async Task ThisRecord_without_locations_create_is_403_whether_or_not_the_address_exists()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));
        await host.PostAddressAsync(await host.PostContactAsync("B"), Address("Storgata 2", "Oslo"));
        await using var noCreate = await host.SharingAsync([PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate, PermissionClaims.LocationsRead]);
        var path = $"{ContactsPath}/{contactId}/addresses/{address.Id}";

        Assert.Equal(HttpStatusCode.Forbidden,
            (await noCreate.Client.PutAsJsonAsync(path, Address("Storgata 2", "Oslo") with { ApplyTo = AddressApplyTo.ThisRecord })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noCreate.Client.PutAsJsonAsync(path, Address("Storgata 77", "Oslo") with { ApplyTo = AddressApplyTo.ThisRecord })).StatusCode);
    }

    [Fact]
    public async Task Resending_an_unchanged_locationId_needs_no_location_claim()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));
        await using var contactsOnly = await host.SharingAsync([PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate]);

        var response = await contactsOnly.Client.PutAsJsonAsync(
            $"{ContactsPath}/{contactId}/addresses/{address.Id}",
            new NewAddress { Label = AddressLabel.Billing, IsPrimary = true, LocationId = address.LocationId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AllUses_edits_the_place_in_place_recomposes_an_auto_name_and_keeps_everything_else()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var b = await host.PostContactAsync("B");
        var address = await host.PostAddressAsync(a, Address("Storgata 1", "Oslo"));
        await host.PostAddressAsync(b, Address("Storgata 1", "Oslo"));
        var eventId = await host.SeedEventAsync(address.LocationId);
        await host.EditLocationAsync(address.LocationId, l =>
        {
            l.Description = "Ring twice";
            l.Latitude = 59.9m;
            l.Longitude = 10.7m;
            l.TimeZoneId = "Europe/Oslo";
        });
        var before = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == address.LocationId));
        Assert.Contains("Storgata 1", before.Name, StringComparison.Ordinal);

        var response = await host.Client.PutAsJsonAsync($"{ContactsPath}/{a}/addresses/{address.Id}",
            Address("Storgata 3", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 2 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == address.LocationId));
        Assert.Equal("Storgata 3", after.Line1);
        Assert.DoesNotContain("Storgata 1", after.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("Storgata 1", after.AddressText ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Ring twice", after.Description);
        Assert.Equal(59.9m, after.Latitude);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));

        var other = Assert.Single((await host.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{b}/addresses"))!);
        Assert.Equal("Storgata 3", other.Line1);
        Assert.True(other.PlaceHasCoordinates);
        var linkedEvent = (await host.Client.GetFromJsonAsync<ExistingCalendarEvent>($"{EventsPath}/{eventId}"))!;
        Assert.DoesNotContain("Storgata 1", linkedEvent.LocationRef!.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllUses_leaves_a_typed_name_alone()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(a, Address("Storgata 1", "Oslo"));
        await host.EditLocationAsync(address.LocationId, l => l.Name = "Grandma's");

        await host.Client.PutAsJsonAsync($"{ContactsPath}/{a}/addresses/{address.Id}",
            Address("Storgata 3", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0 });

        Assert.Equal("Grandma's", await host.ReadAsync(c => c.Locations.Select(l => l.Name).SingleAsync()));
    }

    [Fact]
    public async Task AllUses_needs_locations_update_and_expectedOtherUses_and_rechecks_the_count()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(a, Address("Storgata 1", "Oslo"));
        var path = $"{ContactsPath}/{a}/addresses/{address.Id}";
        await using var noUpdate = await host.SharingAsync(
            [PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate, PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate]);

        Assert.Equal(HttpStatusCode.Forbidden, (await noUpdate.Client.PutAsJsonAsync(path,
            Address("Storgata 3", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0 })).StatusCode);
        await AssertProblem(await host.Client.PutAsJsonAsync(path, Address("Storgata 3", "Oslo") with { ApplyTo = AddressApplyTo.AllUses }),
            HttpStatusCode.UnprocessableEntity, "expectedOtherUses");
        await AssertProblem(await host.Client.PutAsJsonAsync(path,
                Address("Storgata 3", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 4 }),
            HttpStatusCode.Conflict, "applyTo");

        Assert.Equal("Storgata 1", await host.ReadAsync(c => c.Locations.Select(l => l.Line1).SingleAsync()));
    }

    [Fact]
    public async Task AllUses_to_an_address_another_place_holds_is_409_keyed_line1()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(a, Address("Storgata 1", "Oslo"));
        await host.PostAddressAsync(await host.PostContactAsync("B"), Address("Storgata 2", "Oslo"));

        var response = await host.Client.PutAsJsonAsync($"{ContactsPath}/{a}/addresses/{address.Id}",
            Address("Storgata 2", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0 });

        await AssertProblem(response, HttpStatusCode.Conflict, "line1");
    }

    [Fact]
    public async Task A_case_only_change_is_applied_in_place_and_needs_locations_update_whatever_applyTo_says()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(a, Address("storgata 1", "oslo"));
        var path = $"{ContactsPath}/{a}/addresses/{address.Id}";
        await using var noUpdate = await host.SharingAsync(
            [PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate, PermissionClaims.LocationsRead, PermissionClaims.LocationsCreate]);

        Assert.Equal(HttpStatusCode.Forbidden, (await noUpdate.Client.PutAsJsonAsync(path,
            Address("Storgata 1", "Oslo") with { ApplyTo = AddressApplyTo.ThisRecord, ExpectedOtherUses = 0 })).StatusCode);

        var response = await host.Client.PutAsJsonAsync(path, Address("Storgata 1", "Oslo") with { ApplyTo = AddressApplyTo.ThisRecord, ExpectedOtherUses = 0 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var place = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync());
        Assert.Equal("Storgata 1", place.Line1);
        Assert.Equal("Oslo", place.City);
        Assert.Equal(address.LocationId, place.LocationId);
    }

    [Fact]
    public async Task OtherUses_excludes_the_row_itself_and_counts_only_readable_kinds()
    {
        await using var host = await Host.StartAsync(Everything);
        var a = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(a, Address("Storgata 1", "Oslo"));
        await host.SeedEventAsync(address.LocationId);
        await host.SeedEntryAsync(address.LocationId);
        Assert.Equal(2, address.OtherUses + 2);

        var full = Assert.Single((await host.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{a}/addresses"))!);
        Assert.Equal(2, full.OtherUses);

        await using var contactsOnly = await host.SharingAsync([PermissionClaims.ContactsRead, PermissionClaims.CalendarRead]);
        var partial = Assert.Single((await contactsOnly.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{a}/addresses"))!);
        Assert.Equal(1, partial.OtherUses);
    }

    // ── Properties (§5.4) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unchanged_property_save_keeps_its_link_for_a_partial_keyless_address_too()
    {
        await using var host = await Host.StartAsync(Everything);
        var cityOnly = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, CountryCode = "NO" });
        var before = await host.ReadAsync(c => c.RealEstateDetails.Where(d => d.PropertyId == cityOnly).Select(d => d.LocationId).SingleAsync());

        var response = await host.Client.PutAsJsonAsync($"{PropertiesPath}/{cityOnly}", PropertyBody(new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House, CountryCode = "no",
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await host.ReadAsync(c => c.RealEstateDetails.Where(d => d.PropertyId == cityOnly).Select(d => d.LocationId).SingleAsync()));
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    /// <summary>
    /// <c>PUT /api/properties/{id}</c> with a geocode (issue #383 v5 §3.3a, review note): a new address creates a
    /// geocoded place and moves only this property onto it; an address a saved place already has links that place,
    /// whose coordinates and source the geocode never overwrites.
    /// </summary>
    [Fact]
    public async Task A_property_put_with_a_geocode_creates_a_geocoded_place_or_links_a_matching_one_unchanged()
    {
        await using var host = await Host.StartAsync(Everything);
        var original = await host.SeedLocationAsync("Old address", line1: "Storgata 1", city: "Oslo", country: "NO");
        var curated = await host.SeedLocationAsync("Curated", line1: "Storgata 9", city: "Oslo", country: "NO");
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, LocationId = original });
        RealEstateDetailsDto Geocoded(string line1) => new()
        {
            Kind = RealEstateKind.House, AddressLine = line1, PostalCode = null, City = "Oslo", CountryCode = "NO",
            Latitude = 59.912734m, Longitude = 10.746094m, TimeZoneId = "Europe/Oslo", Source = LocationSource.Kartverket,
            SourceReference = "0301-12345-1", ApplyTo = AddressApplyTo.ThisRecord, ExpectedOtherUses = 0,
        };
        Task<Guid?> Linked() => host.ReadAsync(c => c.RealEstateDetails.Where(d => d.PropertyId == property).Select(d => d.LocationId).SingleAsync());

        var created = await host.Client.PutAsJsonAsync($"{PropertiesPath}/{property}", PropertyBody(Geocoded("Storgata 5")));

        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var newPlaceId = (await Linked())!.Value;
        Assert.NotEqual(original, newPlaceId);
        var newPlace = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == newPlaceId));
        Assert.Equal(("Storgata 5", 59.91273m, 10.74609m, "Europe/Oslo", LocationSource.Kartverket),
            (newPlace.Line1, newPlace.Latitude, newPlace.Longitude, newPlace.TimeZoneId, newPlace.Source));
        var old = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == original));
        Assert.Equal(("Storgata 1", (decimal?)null), (old.Line1, old.Latitude));

        var linked = await host.Client.PutAsJsonAsync($"{PropertiesPath}/{property}", PropertyBody(Geocoded("STORGATA 9")));

        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
        Assert.Equal(curated, await Linked());
        var kept = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == curated));
        Assert.Equal(("Curated", "Storgata 9", (decimal?)null, (string?)null, LocationSource.Manual, (string?)null),
            (kept.Name, kept.Line1, kept.Latitude, kept.TimeZoneId, kept.Source, kept.SourceReference));
        Assert.Equal(3, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Fact]
    public async Task A_property_AllUses_edit_writes_four_fields_and_leaves_line2_and_region()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Shared", line1: "Storgata 1", city: "Oslo", country: "NO", line2: "H0101", region: "Oslo fylke");
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, LocationId = place });

        var response = await host.Client.PutAsJsonAsync($"{PropertiesPath}/{property}", PropertyBody(new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House, AddressLine = "Storgata 5", City = "Oslo", CountryCode = "NO",
            ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0,
        }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == place));
        Assert.Equal("Storgata 5", stored.Line1);
        Assert.Equal("H0101", stored.Line2);
        Assert.Equal("Oslo fylke", stored.Region);
        Assert.Equal("Shared", stored.Name);
    }

    [Fact]
    public async Task A_property_AllUses_edit_that_leaves_a_contact_place_incomplete_is_422()
    {
        await using var host = await Host.StartAsync(Everything);
        var contact = await host.PostContactAsync("A");
        var address = await host.PostAddressAsync(contact, Address("Storgata 1", "Oslo"));
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, LocationId = address.LocationId });

        var response = await host.Client.PutAsJsonAsync($"{PropertiesPath}/{property}", PropertyBody(new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House, AddressLine = "Storgata 1", City = "Oslo",
            ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 1,
        }));

        await AssertProblem(response, HttpStatusCode.UnprocessableEntity, "countryCode");
    }

    [Fact]
    public async Task A_cleared_property_address_unlinks_and_keeps_the_place()
    {
        await using var host = await Host.StartAsync(Everything);
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, AddressLine = "Storgata 1", City = "Oslo", CountryCode = "NO" });
        await using var propertiesOnly = await host.SharingAsync([PermissionClaims.PropertiesRead, PermissionClaims.PropertiesUpdate]);

        var response = await propertiesOnly.Client.PutAsJsonAsync($"{PropertiesPath}/{property}", PropertyBody(new RealEstateDetailsDto { Kind = RealEstateKind.House }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await host.ReadAsync(c => c.RealEstateDetails.Where(d => d.PropertyId == property).Select(d => d.LocationId).SingleAsync()));
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Fact]
    public async Task A_property_reads_its_address_under_properties_read_alone()
    {
        await using var host = await Host.StartAsync(Everything);
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto { Kind = RealEstateKind.House, AddressLine = "Storgata 1", City = "Oslo", CountryCode = "NO" });
        await using var reader = await host.SharingAsync([PermissionClaims.PropertiesRead]);

        var read = (await reader.Client.GetFromJsonAsync<ExistingProperty>($"{PropertiesPath}/{property}"))!;

        Assert.Equal("Storgata 1", read.RealEstateDetails!.AddressLine);
        Assert.NotNull(read.RealEstateDetails.LocationId);
    }

    // ── Weather (§5.8, AC 16, 17) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 16 and 20 on both record kinds that link a place (senior-tester M2): the event path and the journal
    /// entry path resolve through the same rule, and a linked place with no weather is <c>NoData</c> on THAT
    /// place — never a silent fallback to the preference or the household default.
    /// </summary>
    [Theory]
    [InlineData("event")]
    [InlineData("entry")]
    public async Task Record_weather_resolves_explicit_then_linked_then_preference_then_household(string kind)
    {
        await using var host = await Host.StartAsync(Everything);
        var linkedWithWeather = await host.SeedLocationAsync("Cabin", latitude: 61m);
        var cabinPoint = await host.SeedWeatherAsync(linkedWithWeather);
        var linkedForecastOff = await host.SeedLocationAsync("Shed", latitude: 62m);
        var shedPoint = await host.SeedWeatherAsync(linkedForecastOff, forecast: false);
        var unlinked = await host.SeedLocationAsync("Somewhere");
        var household = await host.SeedWeatherAsync(await host.SeedLocationAsync("Home", latitude: 59m));
        var preferred = await host.SeedWeatherAsync(await host.SeedLocationAsync("Office", latitude: 58m));
        await host.Factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherDefaultPointId, household.ToString());

        Task<Guid> Seed(Guid? locationId) => kind == "event" ? host.SeedEventAsync(locationId) : host.SeedEntryAsync(locationId);

        async Task<JsonElement> Weather(Guid recordId, string query = "")
        {
            var path = kind == "event" ? EventsPath : EntriesPath;
            var json = await host.Client.GetStringAsync($"{path}/{recordId}/weather{query}");
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        static Guid PointOf(JsonElement weather) => weather.GetProperty("point").GetProperty("weatherPointId").GetGuid();

        var withWeather = await Weather(await Seed(linkedWithWeather));
        Assert.Equal("Linked", withWeather.GetProperty("resolvedFrom").GetString());
        Assert.Equal(cabinPoint, PointOf(withWeather));
        Assert.Equal(linkedWithWeather, withWeather.GetProperty("linkedLocation").GetProperty("locationId").GetGuid());

        // A linked point with its forecast off is used anyway, with no fallback (issue #387 §8).
        var forecastOff = await Weather(await Seed(linkedForecastOff));
        Assert.Equal("Linked", forecastOff.GetProperty("resolvedFrom").GetString());
        Assert.Equal("NoData", forecastOff.GetProperty("status").GetString());
        Assert.Equal(shedPoint, PointOf(forecastOff));

        var unlinkedRecord = await Seed(unlinked);
        var fallback = await Weather(unlinkedRecord);
        Assert.Equal("Household", fallback.GetProperty("resolvedFrom").GetString());
        Assert.Equal(household, PointOf(fallback));
        Assert.False(fallback.TryGetProperty("linkedLocation", out _));
        var explicitly = await Weather(unlinkedRecord, $"?weatherPointId={preferred}");
        Assert.Equal("Explicit", explicitly.GetProperty("resolvedFrom").GetString());
        Assert.Equal(preferred, PointOf(explicitly));

        await host.SeedPreferenceAsync(preferred);
        var fromPreference = await Weather(unlinkedRecord);
        Assert.Equal("Preferred", fromPreference.GetProperty("resolvedFrom").GetString());
        Assert.Equal(preferred, PointOf(fromPreference));
    }

    /// <summary>The hourly and daily reads carry <c>resolvedFrom</c> on the wire too (M2).</summary>
    [Fact]
    public async Task Hourly_and_daily_weather_carry_resolvedFrom()
    {
        await using var host = await Host.StartAsync(Everything);
        var household = await host.SeedWeatherAsync(await host.SeedLocationAsync("Home", latitude: 59m));
        var named = await host.SeedWeatherAsync(await host.SeedLocationAsync("Cabin", latitude: 61m));
        await host.Factory.SetSystemSettingAsync(SystemSettingsKeys.WeatherDefaultPointId, household.ToString());

        async Task<string?> ResolvedFrom(string url) =>
            JsonDocument.Parse(await host.Client.GetStringAsync(url)).RootElement.GetProperty("resolvedFrom").GetString();

        const string hourly = "/api/weather/hourly?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-01-01T06:00:00Z";
        const string daily = "/api/weather/daily?from=2026-01-01&to=2026-01-05";
        Assert.Equal("Household", await ResolvedFrom(hourly));
        Assert.Equal("Explicit", await ResolvedFrom($"{hourly}&weatherPointId={named}"));
        Assert.Equal("Household", await ResolvedFrom(daily));
        Assert.Equal("Explicit", await ResolvedFrom($"{daily}&weatherPointId={named}"));
    }

    [Fact]
    public async Task Event_weather_with_nothing_to_resolve_is_NoPlace_with_no_resolvedFrom()
    {
        await using var host = await Host.StartAsync(Everything);

        var json = await host.Client.GetStringAsync($"{EventsPath}/{await host.SeedEventAsync(null)}/weather");

        var nothing = JsonDocument.Parse(json).RootElement;
        Assert.Equal("NoPlace", nothing.GetProperty("status").GetString());
        Assert.False(nothing.TryGetProperty("resolvedFrom", out _));
    }

    // ── Interop (§8.4, AC 18, 19) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ics_import_without_both_location_claims_stores_text_and_with_them_links_or_creates()
    {
        await using var host = await Host.StartAsync(Everything);
        var calendar = await host.SeedCalendarAsync();
        var existing = await host.SeedLocationAsync("Åsen");
        await host.SeedLocationAsync("Asen skole");
        var ics = Ics(("1", "Åsen"), ("2", "Brand new place"), ("3", "https://meet.example.com/abc"));

        await using var noCreate = await host.SharingAsync(
            [PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate, PermissionClaims.CalendarRead, PermissionClaims.LocationsRead]);
        var textOnly = await ImportIcs(noCreate.Client, calendar, ics);
        Assert.Equal(0, textOnly.PlacesLinked);
        Assert.Equal(2, await host.ReadAsync(c => c.Locations.CountAsync()));
        Assert.Equal("Åsen", await host.ReadAsync(c => c.CalendarEvents.Where(e => e.ExternalUid == "1").Select(e => e.Location).SingleAsync()));

        var linked = await ImportIcs(host.Client, calendar, ics);
        Assert.Equal(2, linked.PlacesLinked);
        Assert.Equal(1, linked.PlacesCreated);
        await host.ReadAsync(async c =>
        {
            var first = await c.CalendarEvents.SingleAsync(e => e.ExternalUid == "1");
            Assert.Equal(existing, first.LocationId);
            Assert.Null(first.Location);
            var created = await c.Locations.SingleAsync(l => l.Name == "Brand new place");
            Assert.Equal(LocationSource.Imported, created.Source);
            var link = await c.CalendarEvents.SingleAsync(e => e.ExternalUid == "3");
            Assert.Null(link.LocationId);
            Assert.Equal("https://meet.example.com/abc", link.Location);
            return 0;
        });
    }

    [Fact]
    public async Task Ics_export_names_the_place_only_for_locations_read_and_never_writes_GEO()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Cabin; north, \"old\"", latitude: 61m);
        var calendar = await host.SeedCalendarAsync();
        var eventId = await host.SeedEventAsync(place, calendar, detail: "room 3,\nback door");

        var withRead = await host.Client.GetStringAsync($"{EventsPath}/{eventId}/ics");
        Assert.DoesNotContain("GEO", withRead, StringComparison.Ordinal);
        var parsed = Ical.Net.Calendar.Load(withRead)!.Events.Single();
        Assert.Equal("Cabin; north, \"old\" — room 3,\nback door", parsed.Location);

        await using var noLocations = await host.SharingAsync([PermissionClaims.CalendarRead]);
        var withoutRead = await noLocations.Client.GetStringAsync($"{EventsPath}/{eventId}/ics");
        Assert.DoesNotContain("Cabin", withoutRead, StringComparison.Ordinal);
        Assert.Equal("room 3,\nback door", Ical.Net.Calendar.Load(withoutRead)!.Events.Single().Location);
    }

    [Fact]
    public async Task VCard_import_without_locations_create_skips_every_ADR_and_counts_it()
    {
        await using var host = await Host.StartAsync(Everything);
        const string vcf = "BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Kari\r\nN:Nordmann;Kari;;;\r\n"
            + "ADR;TYPE=home:;;Storgata 1;Oslo;;0150;NO\r\nADR;TYPE=work:;;Kirkegata 2;Oslo;;0153;NO\r\nEND:VCARD\r\n";
        await using var noCreate = await host.SharingAsync([PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate, PermissionClaims.ContactsRead]);

        var skipped = await ImportVCard(noCreate.Client, vcf);
        Assert.Equal(1, skipped.CreatedCount);
        Assert.Equal(2, skipped.SkippedAddressCount);
        Assert.Equal(0, skipped.AddressesLinked);
        Assert.Null(skipped.PlacesCreated);
        Assert.Empty(skipped.Skipped);
        Assert.Equal(0, await host.ReadAsync(c => c.Locations.CountAsync()));

        var linked = await ImportVCard(host.Client, vcf.Replace("FN:Kari", "FN:Ola", StringComparison.Ordinal).Replace("Kari;", "Ola;", StringComparison.Ordinal));
        Assert.Equal(2, linked.AddressesLinked);
        Assert.Equal(2, linked.PlacesCreated);
        Assert.Equal(0, linked.SkippedAddressCount);
    }

    /// <summary>
    /// Security review, observation 1: a UID-matched update replaces a contact's addresses only when the importer
    /// may re-create them. Without <c>locations.create</c> every ADR is skipped, so the existing rows must stay
    /// rather than be deleted with nothing put back.
    /// </summary>
    [Fact]
    public async Task VCard_update_without_locations_create_keeps_the_existing_addresses()
    {
        await using var host = await Host.StartAsync(Everything);
        const string original = "BEGIN:VCARD\r\nVERSION:4.0\r\nUID:urn:uuid:kari-1\r\nFN:Kari\r\nN:Nordmann;Kari;;;\r\n"
            + "ADR;TYPE=home:;;Storgata 1;Oslo;;0150;NO\r\nEND:VCARD\r\n";
        var created = await ImportVCard(host.Client, original);
        Assert.Equal(1, created.CreatedCount);
        Assert.Equal(1, created.AddressesLinked);
        var before = await host.ReadAsync(c => c.Addresses.Select(a => new { a.Id, a.LocationId }).SingleAsync());

        await using var noCreate = await host.SharingAsync([PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate, PermissionClaims.ContactsRead]);
        var update = await ImportVCard(noCreate.Client, original.Replace("Storgata 1", "Kirkegata 2", StringComparison.Ordinal));

        Assert.Equal(1, update.UpdatedCount);
        Assert.Equal(1, update.SkippedAddressCount);
        var after = await host.ReadAsync(c => c.Addresses.Select(a => new { a.Id, a.LocationId }).ToListAsync());
        Assert.Equal([before], after);
    }

    [Fact]
    public async Task VCard_export_round_trips_separators_and_newlines_from_the_linked_place()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        await host.PostAddressAsync(contactId, Address("Storgata 1; bakgården, inngang B", "Oslo"));

        var vcf = await host.Client.GetStringAsync($"{ContactsPath}/{contactId}/vcard");
        Assert.Contains(@"Storgata 1\; bakgården\, inngang B", vcf.Replace("\r\n ", "", StringComparison.Ordinal), StringComparison.Ordinal);

        await using var other = await Host.StartAsync(Everything);
        var result = await ImportVCard(other.Client, vcf);
        Assert.Equal(1, result.AddressesLinked);
        Assert.Equal("Storgata 1; bakgården, inngang B", await other.ReadAsync(c => c.Locations.Select(l => l.Line1).SingleAsync()));
    }

    // ── Logging (§7.1.11, AC 21) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_log_line_carries_a_name_an_address_or_a_detail_and_address_changes_name_the_actor()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await Host.StartAsync(Everything, logs);
        var contactId = await host.PostContactAsync("Zyxwvut Person");
        var address = await host.PostAddressAsync(contactId, Address("Qwertygata 17", "Plughville"));
        await host.Client.PutAsJsonAsync($"{ContactsPath}/{contactId}/addresses/{address.Id}",
            Address("Asdfgata 19", "Plughville") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0 });
        var calendar = await host.SeedCalendarAsync();
        await host.Client.PostAsJsonAsync(EventsPath, NewEvent(calendar, address.LocationId, "Secretroom detail"));
        var spare = await host.PostLocationAsync(Place("Mergegata 3", "Plughville"));
        await host.Client.PostAsJsonAsync($"{LocationsPath}/{spare.LocationId}/merge", new MergeLocationRequest { TargetLocationId = address.LocationId });
        var doomed = await host.PostLocationAsync(Place("Deletegata 4", "Plughville"));
        await host.Client.DeleteAsync($"{LocationsPath}/{doomed.LocationId}");

        string[] secrets = ["Qwertygata", "Asdfgata", "Plughville", "Secretroom", "Mergegata", "Deletegata", "Zyxwvut"];
        var leaks = logs.Entries.Where(entry => secrets.Any(secret => entry.Message.Contains(secret, StringComparison.Ordinal))).ToList();
        Assert.Empty(leaks);

        Assert.Contains(logs.Entries, e => e.Message.Contains("edited in place", StringComparison.Ordinal) && e.Message.Contains(Actor, StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Message.Contains("merged into", StringComparison.Ordinal) && e.Message.Contains(Actor, StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Message.Contains("deleted by", StringComparison.Ordinal) && e.Message.Contains(Actor, StringComparison.Ordinal));
    }

    // ── Geocode on the fields path (§5.3a, v9; AC 27) ──────────────────────────────────────────────

    [Fact]
    public async Task A_contact_address_with_a_geocode_creates_a_place_with_those_coordinates_and_that_source()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");

        var address = await host.PostAddressAsync(contactId, Geocoded(Address("Storgata 1", "Oslo")));

        Assert.True(address.PlaceHasCoordinates);
        var place = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == address.LocationId));
        Assert.Equal(59.91273m, place.Latitude);
        Assert.Equal(10.74609m, place.Longitude);
        Assert.Equal("Europe/Oslo", place.TimeZoneId);
        Assert.Equal(LocationSource.Kartverket, place.Source);
        Assert.Equal("0301-12345-1", place.SourceReference);
        Assert.Equal("Storgata 1", place.Line1);
    }

    [Fact]
    public async Task A_contact_address_with_a_geocode_for_an_existing_address_links_that_place_unchanged()
    {
        await using var host = await Host.StartAsync(Everything);
        var existing = await host.SeedLocationAsync("Curated", line1: "Storgata 1", city: "Oslo", country: "NO", latitude: 60m);

        var address = await host.PostAddressAsync(await host.PostContactAsync("Acme"), Geocoded(Address("STORGATA 1", "oslo")));

        Assert.Equal(existing, address.LocationId);
        var place = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == existing));
        Assert.Equal(60m, place.Latitude);
        Assert.Equal(10m, place.Longitude);
        Assert.Equal(LocationSource.Manual, place.Source);
        Assert.Null(place.SourceReference);
        Assert.Equal("Curated", place.Name);
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Fact]
    public async Task A_geocode_for_a_merged_away_address_links_the_survivor_unchanged()
    {
        await using var host = await Host.StartAsync(Everything);
        var survivor = await host.SeedLocationAsync("Survivor");
        await host.SeedLocationAsync("Forwarder", line1: "Storgata 1", city: "Oslo", country: "NO", archived: true, mergedInto: survivor);

        var address = await host.PostAddressAsync(await host.PostContactAsync("Acme"), Geocoded(Address("Storgata 1", "Oslo")));

        Assert.Equal(survivor, address.LocationId);
        var place = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == survivor));
        Assert.Null(place.Latitude);
        Assert.Equal(LocationSource.Manual, place.Source);
    }

    [Fact]
    public async Task A_geocode_sent_with_a_locationId_is_ignored()
    {
        await using var host = await Host.StartAsync(Everything);
        var place = await host.SeedLocationAsync("Office", line1: "Storgata 1", city: "Oslo", country: "NO");
        var contactId = await host.PostContactAsync("Acme");

        var response = await host.Client.PostAsJsonAsync($"{ContactsPath}/{contactId}/addresses",
            Geocoded(new NewAddress { Label = AddressLabel.Visiting, LocationId = place }) with { Source = LocationSource.Imported });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == place));
        Assert.Null(stored.Latitude);
        Assert.Equal(LocationSource.Manual, stored.Source);
    }

    [Fact]
    public async Task ThisRecord_with_a_geocode_creates_the_new_place_geocoded_and_AllUses_ignores_it()
    {
        await using var host = await Host.StartAsync(Everything);
        var contactId = await host.PostContactAsync("Acme");
        var address = await host.PostAddressAsync(contactId, Address("Storgata 1", "Oslo"));
        var path = $"{ContactsPath}/{contactId}/addresses/{address.Id}";

        var edited = await host.Client.PutAsJsonAsync(path, Geocoded(Address("Storgata 3", "Oslo")) with
        {
            ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0,
        });
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var inPlace = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == address.LocationId));
        Assert.Equal("Storgata 3", inPlace.Line1);
        Assert.Null(inPlace.Latitude);
        Assert.Equal(LocationSource.ContactAddress, inPlace.Source);

        var copied = await host.Client.PutAsJsonAsync(path, Geocoded(Address("Storgata 5", "Oslo")) with { ApplyTo = AddressApplyTo.ThisRecord });
        Assert.Equal(HttpStatusCode.NoContent, copied.StatusCode);
        var moved = Assert.Single((await host.Client.GetFromJsonAsync<List<ExistingAddress>>($"{ContactsPath}/{contactId}/addresses"))!);
        Assert.NotEqual(address.LocationId, moved.LocationId);
        Assert.True(moved.PlaceHasCoordinates);
        var created = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == moved.LocationId));
        Assert.Equal(LocationSource.Kartverket, created.Source);
    }

    [Fact]
    public async Task A_property_with_a_geocode_creates_a_geocoded_place_and_its_read_never_echoes_the_geocode()
    {
        await using var host = await Host.StartAsync(Everything);
        var property = await host.PostPropertyAsync(new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House, AddressLine = "Storgata 1", PostalCode = "0155", City = "Oslo", CountryCode = "NO",
            Latitude = 59.912734m, Longitude = 10.746094m, TimeZoneId = "Europe/Oslo", Source = LocationSource.Kartverket,
            SourceReference = "0301-12345-1",
        });

        var place = await host.ReadAsync(c => c.RealEstateDetails.Where(d => d.PropertyId == property).Select(d => d.Location!).AsNoTracking().SingleAsync());
        Assert.Equal(59.91273m, place.Latitude);
        Assert.Equal(10.74609m, place.Longitude);
        Assert.Equal(LocationSource.Kartverket, place.Source);

        using var read = await host.Client.GetAsync($"{PropertiesPath}/{property}");
        var details = JsonNode.Parse(await read.Content.ReadAsStringAsync())!["realEstateDetails"]!.AsObject();
        Assert.True(details["placeHasCoordinates"]!.GetValue<bool>());
        foreach (var member in new[] { "latitude", "longitude", "timeZoneId", "source", "sourceReference" })
        {
            Assert.False(details.ContainsKey(member), $"The property read echoed '{member}'.");
        }
    }

    [Fact]
    public async Task A_property_geocode_for_an_existing_address_links_that_place_unchanged()
    {
        await using var host = await Host.StartAsync(Everything);
        var existing = await host.SeedLocationAsync("Curated", line1: "Storgata 1", city: "Oslo", country: "NO");

        var property = await host.PostPropertyAsync(new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House, AddressLine = "Storgata 1", City = "Oslo", CountryCode = "NO",
            Latitude = 59.9m, Longitude = 10.7m, TimeZoneId = "Europe/Oslo", Source = LocationSource.Kartverket,
        });

        Assert.Equal(existing, await host.ReadAsync(c => c.RealEstateDetails.Where(d => d.PropertyId == property).Select(d => d.LocationId).SingleAsync()));
        var place = await host.ReadAsync(c => c.Locations.AsNoTracking().SingleAsync(l => l.LocationId == existing));
        Assert.Null(place.Latitude);
        Assert.Equal(LocationSource.Manual, place.Source);
    }

    public static TheoryData<string, string> GeocodeRefusals => new()
    {
        { "lat-only", "latitude" },
        { "no-zone", "latitude" },
        { "source-only", "latitude" },
        { "no-source", "source" },
        { "imported", "source" },
        { "contact-address", "source" },
    };

    private static NewAddress Refused(NewAddress body, string shape) => shape switch
    {
        "lat-only" => body with { Latitude = 59.9m },
        "no-zone" => Geocoded(body) with { TimeZoneId = null },
        "source-only" => body with { Source = LocationSource.Kartverket },
        "no-source" => Geocoded(body) with { Source = null, SourceReference = null },
        "imported" => Geocoded(body) with { Source = LocationSource.Imported, SourceReference = null },
        "contact-address" => Geocoded(body) with { Source = LocationSource.ContactAddress, SourceReference = null },
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [MemberData(nameof(GeocodeRefusals))]
    public async Task An_invalid_contact_geocode_is_422_on_the_documented_key_whether_or_not_the_address_exists(string shape, string key)
    {
        await using var host = await Host.StartAsync(Everything);
        await host.SeedLocationAsync("Existing", line1: "Storgata 1", city: "Oslo", country: "NO");
        var path = $"{ContactsPath}/{await host.PostContactAsync("Acme")}/addresses";

        await AssertProblem(await host.Client.PostAsJsonAsync(path, Refused(Address("Storgata 1", "Oslo"), shape)), HttpStatusCode.UnprocessableEntity, key);
        await AssertProblem(await host.Client.PostAsJsonAsync(path, Refused(Address("Storgata 9", "Oslo"), shape)), HttpStatusCode.UnprocessableEntity, key);
        Assert.Equal(1, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Theory]
    [InlineData("latitude")]
    [InlineData("source")]
    public async Task An_invalid_property_geocode_is_422_on_the_documented_key(string key)
    {
        await using var host = await Host.StartAsync(Everything);
        var details = new RealEstateDetailsDto
        {
            Kind = RealEstateKind.House, AddressLine = "Storgata 1", City = "Oslo", CountryCode = "NO",
            Latitude = 59.9m, Longitude = key == "latitude" ? null : 10.7m, TimeZoneId = "Europe/Oslo",
            Source = key == "source" ? LocationSource.PropertyAddress : LocationSource.Kartverket,
        };

        await AssertProblem(await host.Client.PostAsJsonAsync(PropertiesPath, PropertyBody(details)), HttpStatusCode.UnprocessableEntity, key);
    }

    [Fact]
    public async Task A_geocode_reference_must_fit_its_source_and_a_bad_zone_or_range_is_400()
    {
        await using var host = await Host.StartAsync(Everything);
        var path = $"{ContactsPath}/{await host.PostContactAsync("Acme")}/addresses";

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(path,
            Geocoded(Address("Storgata 1", "Oslo")) with { Source = LocationSource.Manual })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(path,
            Geocoded(Address("Storgata 1", "Oslo")) with { SourceReference = "not a reference!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(path,
            Geocoded(Address("Storgata 1", "Oslo")) with { TimeZoneId = "Mars/Olympus_Mons" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(path,
            Geocoded(Address("Storgata 1", "Oslo")) with { Latitude = 91m })).StatusCode);
        Assert.Equal(0, await host.ReadAsync(c => c.Locations.CountAsync()));
    }

    [Fact]
    public void A_contact_address_body_with_a_geocode_still_prints_redacted()
    {
        var text = Geocoded(Address("Storgata 1", "Oslo")).ToString();

        Assert.DoesNotContain("59.9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Storgata", text, StringComparison.Ordinal);
    }

    private static NewAddress Geocoded(NewAddress body) => body with
    {
        Latitude = 59.912734m,
        Longitude = 10.746094m,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Kartverket,
        SourceReference = "0301-12345-1",
    };

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static NewLocation Place(string line1, string city, string country = "NO") => new()
    {
        Name = $"{line1}, {city}",
        Line1 = line1,
        City = city,
        CountryCode = country,
        Source = LocationSource.Manual,
    };

    private static UpdateLocation Update(string name, LocationSource source) => new() { Name = name, Source = source };

    private static NewAddress Address(string line1, string city) => new()
    {
        Label = AddressLabel.Visiting, Line1 = line1, City = city, CountryCode = "NO",
    };

    private static NewCalendarEvent NewEvent(Guid calendarId, Guid? locationId, string? detail = null) => new()
    {
        CalendarId = calendarId,
        Title = "Meeting",
        StartDateTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
        EndDateTime = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc),
        LocationId = locationId,
        Location = detail,
    };

    private static NewProperty PropertyBody(RealEstateDetailsDto details) => new()
    {
        Name = "House",
        Description = "Family home",
        Type = PropertyType.RealEstate,
        CurrencyCode = "USD",
        RealEstateDetails = details,
    };

    private static string Ics(params (string Uid, string Location)[] events)
    {
        var builder = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n");
        foreach (var (uid, location) in events)
        {
            builder.Append("BEGIN:VEVENT\r\n")
                .Append($"UID:{uid}\r\nSUMMARY:Event {uid}\r\n")
                .Append("DTSTART:20260110T090000Z\r\nDTEND:20260110T100000Z\r\n")
                .Append($"LOCATION:{location}\r\nEND:VEVENT\r\n");
        }

        return builder.Append("END:VCALENDAR\r\n").ToString();
    }

    private static async Task<IcsImportResult> ImportIcs(HttpClient client, Guid calendarId, string ics)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/calendar");
        content.Add(file, "file", "calendar.ics");
        var response = await client.PostAsync($"/api/calendars/{calendarId}/ics", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IcsImportResult>())!;
    }

    private static async Task<VCardImportResult> ImportVCard(HttpClient client, string vcf)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(vcf));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/vcard");
        content.Add(file, "file", "contacts.vcf");
        var response = await client.PostAsync($"{ContactsPath}/vcard", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VCardImportResult>())!;
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string key)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var keys = document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(member => member.Name).ToList()
            : [];
        Assert.Contains(keys, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A host on its own in-memory store, its client, and the seeding helpers the tests share.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private Host(OdysseyApiFactory factory)
        {
            Factory = factory;
            Client = factory.CreateClient();
        }

        public OdysseyApiFactory Factory { get; }

        public HttpClient Client { get; }

        public static async Task<Host> StartAsync(IReadOnlyCollection<string> permissions, CapturingLoggerProvider? logs = null)
        {
            var factory = new OdysseyApiFactory(permissions, Actor, configureServices: services =>
            {
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            });
            await PropertyApiTestSupport.EnsureCreatedAsync(factory);
            return new Host(factory);
        }

        /// <summary>A second principal against this host's store.</summary>
        public Task<Host> SharingAsync(IReadOnlyCollection<string> permissions) =>
            Task.FromResult(new Host(new OdysseyApiFactory(permissions, Actor, sharingStoreWith: Factory)));

        public async Task<T> ReadAsync<T>(Func<OdysseyContext, Task<T>> read)
        {
            using var scope = Factory.Services.CreateScope();
            return await read(scope.ServiceProvider.GetRequiredService<OdysseyContext>());
        }

        public async Task<ExistingLocation> PostLocationAsync(NewLocation location)
        {
            var response = await Client.PostAsJsonAsync(LocationsPath, location);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ExistingLocation>())!;
        }

        public async Task<List<Guid>> ListLocationIdsAsync(string query) =>
            (await Client.GetFromJsonAsync<PagedResult<ExistingLocation>>($"{LocationsPath}{query}"))!.Items.Select(l => l.LocationId).ToList();

        public Task<Guid> SeedLocationAsync(
            string name, string? line1 = null, string? city = null, string? country = null, decimal? latitude = null,
            bool archived = false, Guid? mergedInto = null, LocationSource source = LocationSource.Manual,
            string? line2 = null, string? region = null) =>
            ReadAsync(async c =>
            {
                var location = new LocationEntity
                {
                    Name = name,
                    Line1 = line1,
                    Line2 = line2,
                    City = city,
                    Region = region,
                    CountryCode = country,
                    AddressKey = LocationAddressKey.Compute(country, null, city, line1, line2, region),
                    Latitude = latitude,
                    Longitude = latitude is null ? null : 10m,
                    TimeZoneId = latitude is null ? null : "Europe/Oslo",
                    Source = source,
                    Archived = archived ? DateTime.UtcNow : null,
                    MergedIntoLocationId = mergedInto,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                c.Locations.Add(location);
                await c.SaveChangesAsync();
                return location.LocationId;
            });

        public Task EditLocationAsync(Guid id, Action<LocationEntity> edit) =>
            ReadAsync(async c =>
            {
                edit(await c.Locations.SingleAsync(l => l.LocationId == id));
                return await c.SaveChangesAsync();
            });

        public Task ArchiveLocationAsync(Guid id) => EditLocationAsync(id, l => l.Archived = DateTime.UtcNow);

        public Task<Guid> SeedCalendarAsync() =>
            ReadAsync(async c =>
            {
                var calendar = new Calendar { Name = "Family", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                c.Calendars.Add(calendar);
                await c.SaveChangesAsync();
                return calendar.CalendarId;
            });

        public async Task<Guid> SeedEventAsync(Guid? locationId, Guid? calendarId = null, string? detail = null)
        {
            var calendar = calendarId ?? await SeedCalendarAsync();
            return await ReadAsync(async c =>
            {
                var row = new CalendarEvent
                {
                    CalendarId = calendar,
                    Title = "Event",
                    StartDateTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
                    EndDateTime = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc),
                    LocationId = locationId,
                    Location = detail,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                c.CalendarEvents.Add(row);
                await c.SaveChangesAsync();
                return row.CalendarEventId;
            });
        }

        public async Task<Guid> SeedSeriesAsync(Guid? locationId)
        {
            var calendar = await SeedCalendarAsync();
            return await ReadAsync(async c =>
            {
                var row = new RecurrencePattern
                {
                    CalendarId = calendar,
                    Title = "Series",
                    StartDateTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
                    EndDateTime = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc),
                    Frequency = Odyssey.Context.RecurrenceFrequency.Daily,
                    OccurrenceCount = 1,
                    LocationId = locationId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                c.RecurrencePatterns.Add(row);
                await c.SaveChangesAsync();
                return row.RecurrencePatternId;
            });
        }

        public Task<Guid> SeedEntryAsync(Guid? locationId) =>
            ReadAsync(async c =>
            {
                var row = new JournalEntry
                {
                    ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                    Title = "Entry",
                    Content = "Text",
                    EntryDate = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                    LocationId = locationId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                c.JournalEntries.Add(row);
                await c.SaveChangesAsync();
                return row.JournalEntryId;
            });

        /// <summary>A weather point (issue #387) at the place's position, with the place linked to it; returns the point id.</summary>
        public Task<Guid> SeedWeatherAsync(Guid locationId, bool forecast = true) =>
            ReadAsync(async c =>
            {
                var place = await c.Locations.SingleAsync(l => l.LocationId == locationId);
                var id = Guid.NewGuid();
                c.WeatherPoints.Add(new WeatherPoint
                {
                    WeatherPointId = id,
                    Name = $"Point {id:N}",
                    ForecastEnabled = forecast,
                    HistoryEnabled = false,
                    EnabledAt = DateTime.UtcNow,
                    CurrentSiteVersion = 1,
                    SettingsVersion = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                c.WeatherPointSites.Add(new WeatherPointSite
                {
                    WeatherPointId = id, SiteVersion = 1, Latitude = place.Latitude ?? 59.9m, Longitude = place.Longitude ?? 10.7m,
                    TimeZoneId = place.TimeZoneId ?? "Europe/Oslo", AppliedAt = DateTime.UtcNow,
                });
                place.WeatherPointId = id;
                await c.SaveChangesAsync();
                return id;
            });

        public Task SeedPreferenceAsync(Guid pointId) =>
            ReadAsync(async c =>
            {
                c.UserPreferences.Add(new UserPreference
                {
                    UserPreferenceId = Guid.NewGuid(),
                    UserId = Actor,
                    Key = "weather-preferences",
                    PreferencesJson = $"{{\"defaultPointId\":\"{pointId}\"}}",
                    UpdatedAt = DateTime.UtcNow,
                });
                return await c.SaveChangesAsync();
            });

        public async Task<Guid> PostContactAsync(string legalName)
        {
            var response = await Client.PostAsJsonAsync(ContactsPath, new NewContact
            {
                Type = ContactType.Organization,
                Archived = false,
                OrganizationDetails = new() { LegalName = legalName },
            });
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return Guid.Parse(response.Headers.Location!.Segments[^1]);
        }

        public async Task<ExistingAddress> PostAddressAsync(Guid contactId, NewAddress address)
        {
            var response = await Client.PostAsJsonAsync($"{ContactsPath}/{contactId}/addresses", address);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ExistingAddress>())!;
        }

        public async Task<Guid> PostPropertyAsync(RealEstateDetailsDto details)
        {
            var response = await Client.PostAsJsonAsync(PropertiesPath, PropertyBody(details));
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ExistingProperty>())!.PropertyId;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
