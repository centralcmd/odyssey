using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Locations;
using Odyssey.Dtos.Journal;
using Xunit;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.Core.Tests;

/// <summary>
/// The location resolver's rules (issue #380 §3, §5.3, §8.1) on the EF InMemory tier: the claim gates, the
/// archived and completeness rules, find-or-create with forwarders, name allocation, the auto-composed name
/// test, the usage masking and the ICS mapping. The races they guard (1062, 1452, FOR UPDATE) are in
/// <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class LocationResolverTests
{
    private static readonly LocationClaims ReadOnly = new() { LocationsRead = true };
    private static readonly LocationClaims CreateOnly = new() { LocationsCreate = true };

    [Fact]
    public async Task Linking_a_new_id_needs_locations_read_but_keeping_the_stored_one_needs_nothing()
    {
        await using var context = TestContextFactory.CreateJournal();
        var id = await SeedAsync(context, Located("Cabin"));
        var resolver = new LocationResolver(context);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => resolver.LinkByIdAsync(id, null, false, LocationClaims.None, default));
        Assert.Equal(id, (await resolver.LinkByIdAsync(id, id, true, LocationClaims.None, default)).LocationId);
        Assert.Equal(id, await resolver.ResolveRecordLinkAsync(id, id, LocationClaims.None, default));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => resolver.ResolveRecordLinkAsync(null, id, LocationClaims.None, default));
    }

    [Fact]
    public async Task A_new_link_to_an_archived_or_unknown_place_is_422_and_an_address_link_needs_a_complete_place()
    {
        await using var context = TestContextFactory.CreateJournal();
        var archived = await SeedAsync(context, Located("Old", archived: true));
        var cityOnly = await SeedAsync(context, With(Located("Town"), l => l.City = "Oslo"));
        var resolver = new LocationResolver(context);

        var unknown = await Assert.ThrowsAsync<DomainUnprocessableException>(() => resolver.LinkByIdAsync(Guid.NewGuid(), null, false, ReadOnly, default));
        Assert.Contains("locationId", unknown.Errors!.Keys);
        await Assert.ThrowsAsync<DomainUnprocessableException>(() => resolver.LinkByIdAsync(archived, null, false, ReadOnly, default));
        await Assert.ThrowsAsync<DomainUnprocessableException>(() => resolver.LinkByIdAsync(cityOnly, null, true, ReadOnly, default));
        Assert.Equal(cityOnly, (await resolver.LinkByIdAsync(cityOnly, null, false, ReadOnly, default)).LocationId);
    }

    [Fact]
    public async Task Find_or_create_checks_locations_create_before_the_lookup()
    {
        await using var context = TestContextFactory.CreateJournal();
        var address = LocationAddress.Of("Storgata 1", null, null, "Oslo", null, "NO");
        await SeedAsync(context, FromAddress("Existing", address));
        var resolver = new LocationResolver(context);

        await Assert.ThrowsAsync<DomainForbiddenException>(
            () => resolver.FindOrCreateAsync(address, LocationSource.ContactAddress, null, ReadOnly, default));
        await Assert.ThrowsAsync<DomainForbiddenException>(
            () => resolver.FindOrCreateAsync(LocationAddress.Of("Nowhere 1", null, null, "Oslo", null, "NO"), LocationSource.ContactAddress, null, ReadOnly, default));
    }

    [Fact]
    public async Task Find_or_create_follows_a_forwarder_and_links_an_archived_match_without_restoring_it()
    {
        await using var context = TestContextFactory.CreateJournal();
        var address = LocationAddress.Of("Storgata 1", null, null, "Oslo", null, "NO");
        var survivor = await SeedAsync(context, Located("Survivor", archived: true));
        await SeedAsync(context, With(FromAddress("Forwarder", address), l => { l.MergedIntoLocationId = survivor; l.Archived = DateTime.UtcNow; }));
        var resolver = new LocationResolver(context);

        var result = await resolver.FindOrCreateAsync(
            LocationAddress.Of("STORGATA  1", null, null, "oslo", null, "no"), LocationSource.ContactAddress, null, CreateOnly, default);

        Assert.False(result.Created);
        Assert.Equal(survivor, result.Location.LocationId);
        Assert.NotNull(result.Location.Archived);
    }

    [Fact]
    public async Task A_created_place_is_named_from_its_address_never_from_a_record_and_suffixed_on_a_collision()
    {
        await using var context = TestContextFactory.CreateJournal();
        await SeedAsync(context, Located("Storgata 1, Oslo, NO"));
        var resolver = new LocationResolver(context);

        var result = await resolver.FindOrCreateAsync(
            LocationAddress.Of("Storgata 1", null, null, "Oslo", null, "NO"), LocationSource.PropertyAddress, "actor", CreateOnly, default);
        await resolver.SaveChangesAsync(default);

        Assert.True(result.Created);
        Assert.Equal("Storgata 1, Oslo, NO (2)", result.Location.Name);
        Assert.Equal("Storgata 1, Oslo, NO", result.Location.AddressText);
        Assert.Equal(LocationSource.PropertyAddress, result.Location.Source);
        Assert.Null(result.Location.Latitude);
    }

    [Fact]
    public async Task A_keyless_partial_address_always_creates()
    {
        await using var context = TestContextFactory.CreateJournal();
        var resolver = new LocationResolver(context);
        var countryOnly = LocationAddress.Of(null, null, null, null, null, "NO");

        var first = await resolver.FindOrCreateAsync(countryOnly, LocationSource.PropertyAddress, null, CreateOnly, default);
        var second = await resolver.FindOrCreateAsync(countryOnly, LocationSource.PropertyAddress, null, CreateOnly, default);

        Assert.Null(first.Location.AddressKey);
        Assert.True(second.Created);
        Assert.NotEqual(first.Location.Name, second.Location.Name);
    }

    [Theory]
    [InlineData("Storgata 1, 0150 Oslo, NO", true)]
    [InlineData("Storgata 1, 0150 Oslo, NO (2)", true)]
    [InlineData("Storgata 1, 0150 Oslo, NO (12)", true)]
    [InlineData("Storgata 1, 0150 Oslo, NO (1)", false)]
    [InlineData("storgata 1, 0150 oslo, no", false)]
    [InlineData("Grandma's", false)]
    public void An_auto_composed_name_is_recognised_suffixed_or_not_and_a_typed_one_is_not(string name, bool expected) =>
        Assert.Equal(expected, LocationResolver.IsAutoComposedName(name, "Storgata 1, 0150 Oslo, NO"));

    [Fact]
    public void A_truncated_auto_composed_name_is_recognised_through_the_composer_truncation()
    {
        var composed = new string('a', 120) + ", Oslo";

        Assert.True(LocationResolver.IsAutoComposedName(LocationNaming.Candidate(composed, 1), composed));
        Assert.True(LocationResolver.IsAutoComposedName(LocationNaming.Candidate(composed, 7), composed));
    }

    [Fact]
    public void A_property_edit_keeps_the_place_line2_and_region()
    {
        var location = new LocationEntity { Name = "x", Line1 = "Old 1", Line2 = "H0101", City = "Oslo", Region = "Viken", CountryCode = "NO" };

        var result = LocationResolver.Resulting(location, LocationAddress.Of("New 2", null, "0150", "Oslo", null, "NO"), propertyFields: true);

        Assert.Equal(LocationAddress.Of("New 2", "H0101", "0150", "Oslo", "Viken", "NO"), result);
    }

    [Fact]
    public void Usage_masks_kinds_the_caller_cannot_read_and_otherUses_excludes_the_row_itself()
    {
        var counts = new LocationReferenceGuard.Counts(Contacts: 2, Properties: 1, CalendarEvents: 5, RecurringSeries: 1, JournalEntries: 3);
        var contactsOnly = new LocationClaims { ContactsRead = true };

        var usage = LocationReferenceGuard.ToUsage(counts, contactsOnly);

        Assert.Equal(new LocationUsage { Contacts = 2 }, usage);
        Assert.Equal(1, LocationReferenceGuard.OtherUses(counts, LocationReferenceGuard.Kind.Contact, contactsOnly));
        Assert.Equal(11, LocationReferenceGuard.OtherUses(counts, LocationReferenceGuard.Kind.Contact, LocationClaims.Trusted));
        Assert.Equal(0, LocationReferenceGuard.OtherUses(counts, LocationReferenceGuard.Kind.Property, new LocationClaims()));
    }

    [Fact]
    public async Task Weather_no_longer_makes_a_place_referenced()
    {
        // Issue #387 §8: the preference and the household default name a weather POINT, and a place's own link
        // to a point is a column on the place — none of them is a use that blocks a delete.
        await using var context = TestContextFactory.CreateJournal();
        var preferred = await SeedAsync(context, Located("Preferred"));
        var household = await SeedAsync(context, Located("Household"));
        var linked = await SeedAsync(context, Located("Linked"));
        var pointId = Guid.NewGuid();
        context.WeatherPoints.Add(new Odyssey.Context.WeatherPoint
        {
            WeatherPointId = pointId, Name = "Oslo", CurrentSiteVersion = 1,
        });
        (await context.Locations.SingleAsync(l => l.LocationId == linked)).WeatherPointId = pointId;
        context.UserPreferences.Add(new Odyssey.Context.UserPreference
        {
            UserPreferenceId = Guid.NewGuid(), UserId = "u", Key = "weather-preferences",
            PreferencesJson = $"{{\"defaultPointId\":\"{preferred}\"}}", UpdatedAt = DateTime.UtcNow,
        });
        context.SystemSettings.Add(new Odyssey.Context.SystemSetting
        {
            Key = Odyssey.Context.SystemSettingsKeys.WeatherDefaultPointId, Value = household.ToString(), UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        var guard = new LocationReferenceGuard(context);

        Assert.False(await guard.IsReferencedAsync(preferred, default));
        Assert.False(await guard.IsReferencedAsync(household, default));
        Assert.False(await guard.IsReferencedAsync(linked, default));
    }

    [Fact]
    public async Task Ics_import_without_both_claims_keeps_text_and_runs_no_lookup()
    {
        await using var context = TestContextFactory.CreateJournal();
        await SeedAsync(context, Located("Cabin"));

        var places = await IcsPlaces.ForImportAsync(context, ReadOnly, "u", TimeProvider.System, default);

        Assert.False(places.Enabled);
        Assert.Equal((null, "Cabin"), places.Map("Cabin"));
        Assert.Equal(0, places.Linked);
    }

    [Fact]
    public async Task Ics_import_matches_exactly_on_letters_keeps_link_like_text_and_splits_its_own_export_shape()
    {
        await using var context = TestContextFactory.CreateJournal();
        var asen = await SeedAsync(context, Located("Åsen"));
        var both = new LocationClaims { LocationsRead = true, LocationsCreate = true };

        var places = await IcsPlaces.ForImportAsync(context, both, "u", TimeProvider.System, default);

        Assert.Equal((asen, null), places.Map("åsen"));
        Assert.Equal((asen, "room 3"), places.Map("Åsen — room 3"));
        Assert.Equal((null, "whereby.com/odyssey"), places.Map("whereby.com/odyssey"));
        Assert.Equal((null, "Meeting ID 123 passcode 9"), places.Map("Meeting ID 123 passcode 9"));
        var (created, detail) = places.Map("Asen");
        Assert.NotEqual(asen, created);
        Assert.Null(detail);
        Assert.Equal(3, places.Linked);
        Assert.Equal(1, places.Created);
        var again = places.Map("Asen");
        Assert.Equal(created, again.LocationId);
        Assert.Equal(1, places.Created);
    }

    [Theory]
    [InlineData(true, "Cabin — room 3")]
    [InlineData(false, "room 3")]
    public void Ics_export_writes_the_name_only_when_it_is_known(bool canRead, string expected)
    {
        var id = Guid.NewGuid();
        var names = canRead ? new Dictionary<Guid, string> { [id] = "Cabin" } : [];

        Assert.Equal(expected, IcsPlaces.ExportText(id, "room 3", names));
        Assert.Equal(canRead ? "Cabin" : null, IcsPlaces.ExportText(id, null, names));
    }

    private static LocationEntity Located(string name, bool archived = false) => new()
    {
        Name = name,
        Source = LocationSource.Manual,
        Archived = archived ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static LocationEntity FromAddress(string name, LocationAddress address) => With(Located(name), l =>
    {
        l.Line1 = address.Line1;
        l.City = address.City;
        l.CountryCode = address.CountryCode;
        l.AddressKey = address.Key;
    });

    private static LocationEntity With(LocationEntity location, Action<LocationEntity> edit)
    {
        edit(location);
        return location;
    }

    private static async Task<Guid> SeedAsync(Odyssey.Context.OdysseyContext context, LocationEntity location)
    {
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return location.LocationId;
    }
}
