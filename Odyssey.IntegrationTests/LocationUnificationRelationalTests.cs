// EF1002: the database name interpolated below is this class's own constant.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Locations;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using LocationEntity = Odyssey.Context.Location;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #380's write contract: what only real MariaDB can show — the
/// <c>RESTRICT</c> keys, the unique <c>AddressKey</c> and <c>(ContactId, LocationId)</c> indexes, the
/// <c>1062</c> re-read, the <c>1452</c> mapping, row locks, merges under the execution strategy, and that
/// nothing deletes a place implicitly (AC 10, 11, 13, 15, 26). Every context here enables
/// <c>EnableRetryOnFailure</c>, as production does, so the transaction helper runs inside the strategy.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class LocationUnificationRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_unified_locations";

    // Attribution columns are real keys to AspNetUsers; no user is seeded, so writes attribute to nobody.
    private const string? Actor = null;

    private static readonly LocationClaims All = LocationClaims.Trusted;

    // ── AC 15: concurrent creates ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_create_that_loses_the_AddressKey_race_relinks_to_the_winner()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var calendar = await SeedCalendarAsync();
        var address = LocationAddress.Of("Storgata 1", null, "0150", "Oslo", null, "NO");

        await using var loser = NewContext();
        var loserResolver = new LocationResolver(loser);
        var staged = await loserResolver.FindOrCreateAsync(address, LocationSource.ContactAddress, null, All, default);
        Assert.True(staged.Created);
        var row = NewEvent(calendar, staged.Location.LocationId);
        loser.CalendarEvents.Add(row);

        Guid winnerId;
        await using (var winner = NewContext())
        {
            var winnerResolver = new LocationResolver(winner);
            winnerId = (await winnerResolver.FindOrCreateAsync(address, LocationSource.ContactAddress, null, All, default)).Location.LocationId;
            await winnerResolver.SaveChangesAsync(default);
        }

        await loserResolver.SaveChangesAsync(default);

        await using var check = NewContext();
        Assert.Equal(1, await check.Locations.CountAsync());
        Assert.Equal(winnerId, (await check.CalendarEvents.SingleAsync(e => e.CalendarEventId == row.CalendarEventId)).LocationId);
    }

    [SkippableFact]
    public async Task Concurrent_contact_addresses_at_one_address_yield_one_place()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var contacts = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            contacts.Add(await SeedContactAsync($"Contact {i}"));
        }

        await Task.WhenAll(contacts.Select(async contactId =>
        {
            await using var context = NewContext();
            await Contacts(context).CreateAddress(contactId, Address("Storgata 1", "Oslo"), claims: LocationClaims.Trusted);
        }));

        await using var check = NewContext();
        Assert.Equal(1, await check.Locations.CountAsync());
        Assert.Equal(6, await check.Addresses.CountAsync());
    }

    // ── AC 11: delete ────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_link_racing_a_delete_fails_with_422_keyed_locationId()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var calendar = await SeedCalendarAsync();
        var place = await SeedLocationAsync("Doomed");

        await using var linker = NewContext();
        var resolver = new LocationResolver(linker);
        var linked = await resolver.ResolveRecordLinkAsync(place, null, All, default);

        await using (var deleter = NewContext())
        {
            Assert.True(await new LocationService(deleter).DeleteAsync(place, Actor));
        }

        linker.CalendarEvents.Add(NewEvent(calendar, linked));
        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(() => resolver.SaveChangesAsync(default));
        Assert.Contains("locationId", refused.Errors!.Keys);
    }

    [SkippableFact]
    public async Task Delete_refuses_a_referenced_place_and_the_RESTRICT_key_backs_the_guard_up()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var calendar = await SeedCalendarAsync();
        var place = await SeedLocationAsync("Used");
        await using (var context = NewContext())
        {
            context.CalendarEvents.Add(NewEvent(calendar, place));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            await Assert.ThrowsAsync<DomainConflictException>(() => new LocationService(context).DeleteAsync(place, Actor));
        }

        await using (var context = NewContext())
        {
            var ex = await Assert.ThrowsAsync<MySqlException>(
                () => context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM `Locations` WHERE `LocationId` = {place}"));
            Assert.Equal(1451, ex.Number);
        }

        var forwarded = await SeedLocationAsync("Forwarded");
        await SeedLocationAsync("Forwarder", mergedInto: forwarded);
        await using (var context = NewContext())
        {
            await Assert.ThrowsAsync<DomainConflictException>(() => new LocationService(context).DeleteAsync(forwarded, Actor));
            Assert.True(await new LocationService(context).DeleteAsync(await SeedLocationAsync("Unused"), Actor));
        }
    }

    // ── AC 13: merge ─────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Merge_repoints_dedupes_under_the_unique_index_and_archives_the_source()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var both = await SeedContactAsync("Both");
        var target = (await AddAsync(both, Address("Storgata 2", "Oslo"))).LocationId;
        var source = (await AddAsync(both, Address("Storgata 1", "Oslo") with { IsPrimary = true })).LocationId;
        var calendar = await SeedCalendarAsync();
        await using (var context = NewContext())
        {
            context.CalendarEvents.Add(NewEvent(calendar, source));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            Assert.NotNull(await new LocationService(context).MergeAsync(source, target, All, Actor));
        }

        await using var check = NewContext();
        var kept = await check.Addresses.SingleAsync(a => a.ContactId == both);
        Assert.Equal(target, kept.LocationId);
        Assert.True(kept.IsPrimary);
        Assert.Equal(target, (await check.CalendarEvents.SingleAsync()).LocationId);
        var forwarder = await check.Locations.SingleAsync(l => l.LocationId == source);
        Assert.Equal(target, forwarder.MergedIntoLocationId);
        Assert.NotNull(forwarder.Archived);

        // A later find-or-create of the merged-away address links to the survivor.
        var later = await SeedContactAsync("Later");
        Assert.Equal(target, (await AddAsync(later, Address("Storgata 1", "Oslo"))).LocationId);
    }

    [SkippableFact]
    public async Task Opposing_concurrent_merges_do_not_deadlock()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        for (var round = 0; round < 5; round++)
        {
            var a = await SeedLocationAsync($"A {round}");
            var b = await SeedLocationAsync($"B {round}");

            async Task<Exception?> Merge(Guid from, Guid to)
            {
                await using var context = NewContext();
                try
                {
                    await new LocationService(context).MergeAsync(from, to, All, Actor);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var outcomes = await Task.WhenAll(Merge(a, b), Merge(b, a)).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Single(outcomes, outcome => outcome is null);
            Assert.All(outcomes.OfType<Exception>(), ex => Assert.IsType<DomainUnprocessableException>(ex));
        }
    }

    [SkippableFact]
    public async Task A_missing_class_claim_refuses_the_merge_and_changes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var contact = await SeedContactAsync("Holder");
        var source = (await AddAsync(contact, Address("Storgata 1", "Oslo"))).LocationId;
        var target = await SeedLocationAsync("Target");

        await using (var context = NewContext())
        {
            await Assert.ThrowsAsync<DomainForbiddenException>(() => new LocationService(context).MergeAsync(
                source, target, LocationClaims.Trusted with { ContactsUpdate = false }, Actor));
        }

        await using var check = NewContext();
        Assert.Equal(source, (await check.Addresses.SingleAsync()).LocationId);
        Assert.Null((await check.Locations.SingleAsync(l => l.LocationId == source)).MergedIntoLocationId);
    }

    // ── AC 26: AllUses under FOR UPDATE ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AllUses_recounts_under_the_lock_and_a_stale_count_writes_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var first = await SeedContactAsync("First");
        var address = await AddAsync(first, Address("Storgata 1", "Oslo"));
        await AddAsync(await SeedContactAsync("Second"), Address("Storgata 1", "Oslo"));

        await using (var context = NewContext())
        {
            var stale = await Assert.ThrowsAsync<DomainConflictException>(() => Contacts(context).UpdateAddress(
                first, address.Id, Address("Storgata 9", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0 },
                claims: All, userId: Actor));
            Assert.Contains("applyTo", stale.Errors!.Keys);
        }

        await using (var context = NewContext())
        {
            Assert.Equal("Storgata 1", (await context.Locations.SingleAsync()).Line1);
            Assert.True(await Contacts(context).UpdateAddress(
                first, address.Id, Address("Storgata 9", "Oslo") with { ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 1 },
                claims: All, userId: Actor));
        }

        await using var check = NewContext();
        var place = await check.Locations.SingleAsync();
        Assert.Equal("Storgata 9", place.Line1);
        Assert.Null(place.UpdatedByUserId);
        Assert.Equal(LocationAddressKey.Compute("NO", null, "Oslo", "Storgata 9", null, null), place.AddressKey);
    }

    [SkippableFact]
    public async Task A_property_update_with_an_AllUses_edit_commits_atomically_under_the_execution_strategy()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        ExistingProperty created;
        await using (var context = NewContext())
        {
            created = await Properties(context).Create(PropertyBody(new RealEstateDetailsDto
            {
                Kind = RealEstateKind.House, AddressLine = "Storgata 1", City = "Oslo", CountryCode = "NO",
            }), Actor, claims: LocationClaims.Trusted);
        }

        await using (var context = NewContext())
        {
            await Properties(context).Update(created.PropertyId, PropertyBody(new RealEstateDetailsDto
            {
                Kind = RealEstateKind.House, AddressLine = "Storgata 3", City = "Oslo", CountryCode = "NO",
                ApplyTo = AddressApplyTo.AllUses, ExpectedOtherUses = 0,
            }) with { Name = "Renamed" }, Actor, claims: LocationClaims.Trusted);
        }

        await using var check = NewContext();
        Assert.Equal("Storgata 3", (await check.Locations.SingleAsync()).Line1);
        Assert.Equal("Renamed", (await check.Properties.SingleAsync()).Name);
    }

    // ── AC 10: nothing deletes a place implicitly ────────────────────────────────────────────────

    /// <summary>
    /// AC 10 (senior-tester L1): no record write ever deletes a place. Every kind of release is exercised — a
    /// copy-on-write edit and an unlink, a merge, and the deletes of an event, a series, an entry, a contact
    /// and a property — and every place each of them touched is asserted BY ID still to exist, so a regression
    /// names the place it lost rather than an off-by-one count.
    /// </summary>
    [SkippableFact]
    public async Task Unlinking_editing_merging_and_deleting_records_never_removes_a_place()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var contact = await SeedContactAsync("Mover");
        var address = await AddAsync(contact, Address("Storgata 1", "Oslo"));
        ExistingProperty property;
        await using (var context = NewContext())
        {
            property = await Properties(context).Create(PropertyBody(new RealEstateDetailsDto
            {
                Kind = RealEstateKind.House, AddressLine = "Kirkegata 2", City = "Oslo", CountryCode = "NO",
            }), Actor, claims: LocationClaims.Trusted);
        }

        var calendar = await SeedCalendarAsync();
        var eventPlace = await SeedLocationAsync("Event place");
        var seriesPlace = await SeedLocationAsync("Series place");
        var entryPlace = await SeedLocationAsync("Entry place");
        var mergeSource = await SeedLocationAsync("Merge source");
        var mergeTarget = await SeedLocationAsync("Merge target");
        Guid eventId, seriesId, entryId;
        await using (var context = NewContext())
        {
            var calendarEvent = NewEvent(calendar, eventPlace);
            var series = new RecurrencePattern
            {
                CalendarId = calendar, Title = "Series", LocationId = seriesPlace, Frequency = Odyssey.Context.RecurrenceFrequency.Weekly,
                StartDateTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
                EndDateTime = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var entry = new JournalEntry
            {
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}", Title = "Entry", Content = "Body", LocationId = entryPlace,
                EntryDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            context.CalendarEvents.Add(calendarEvent);
            context.CalendarEvents.Add(NewEvent(calendar, mergeSource));
            context.RecurrencePatterns.Add(series);
            context.JournalEntries.Add(entry);
            await context.SaveChangesAsync();
            (eventId, seriesId, entryId) = (calendarEvent.CalendarEventId, series.RecurrencePatternId, entry.JournalEntryId);
        }

        var touched = new[]
        {
            address.LocationId, property.RealEstateDetails!.LocationId!.Value,
            eventPlace, seriesPlace, entryPlace, mergeSource, mergeTarget,
        };

        await using (var context = NewContext())
        {
            await Contacts(context).UpdateAddress(contact, address.Id, Address("Storgata 5", "Oslo") with { ApplyTo = AddressApplyTo.ThisRecord }, claims: LocationClaims.Trusted);
        }

        Guid copied;
        await using (var context = NewContext())
        {
            copied = (await context.Addresses.SingleAsync()).LocationId;
            Assert.NotEqual(address.LocationId, copied);
            await Properties(context).Update(property.PropertyId, PropertyBody(new RealEstateDetailsDto { Kind = RealEstateKind.House }), Actor, claims: LocationClaims.Trusted);
        }

        await using (var context = NewContext())
        {
            await new LocationService(context).MergeAsync(mergeSource, mergeTarget, LocationClaims.Trusted, Actor);
        }

        await using (var context = NewContext())
        {
            Assert.True(await new CalendarEventService(context, new StubJournalLimitsLookup()).Delete(eventId));
            Assert.True(await new RecurrencePatternService(context, new StubJournalLimitsLookup()).Delete(seriesId));
            Assert.True(await Entries(context).Delete(entryId));
            await Contacts(context).Delete(contact, detachBlockingLinks: false, null);
            await Properties(context).Delete(property.PropertyId, Actor);
        }

        await using var check = NewContext();
        var surviving = await check.Locations.AsNoTracking().Select(l => l.LocationId).ToListAsync();
        Assert.All(touched.Append(copied), id => Assert.Contains(id, surviving));
        Assert.Equal(mergeTarget, (await check.Locations.AsNoTracking().SingleAsync(l => l.LocationId == mergeSource)).MergedIntoLocationId);
    }

    private static JournalEntryService Entries(OdysseyContext context)
    {
        var files = new FileLookup(context);
        return new JournalEntryService(
            context, new ContactLookup(context), files, new PhotoLookup(context, files, null!), new StubJournalLimitsLookup());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static NewAddress Address(string line1, string city) => new()
    {
        Label = AddressLabel.Visiting, Line1 = line1, City = city, CountryCode = "NO",
    };

    private static NewProperty PropertyBody(RealEstateDetailsDto details) => new()
    {
        Name = "House",
        Description = "Family home",
        Type = PropertyType.RealEstate,
        CurrencyCode = "NOK",
        RealEstateDetails = details,
    };

    private static CalendarEvent NewEvent(Guid calendarId, Guid? locationId) => new()
    {
        CalendarId = calendarId,
        Title = "Event",
        StartDateTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc),
        EndDateTime = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc),
        LocationId = locationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static ContactService Contacts(OdysseyContext context) =>
        new(context, new ContactReferenceGuard(context), TimeProvider.System);

    private static PropertyService Properties(OdysseyContext context) => new(context, new ContactLookup(context));

    private async Task<ExistingAddress> AddAsync(Guid contactId, NewAddress address)
    {
        await using var context = NewContext();
        return (await Contacts(context).CreateAddress(contactId, address, claims: LocationClaims.Trusted))!;
    }

    private async Task<Guid> SeedContactAsync(string legalName)
    {
        await using var context = NewContext();
        return (await Contacts(context).Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new() { LegalName = legalName },
        })).ContactId;
    }

    private async Task<Guid> SeedCalendarAsync()
    {
        await using var context = NewContext();
        var calendar = new Calendar { Name = $"Calendar {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        context.Calendars.Add(calendar);
        await context.SaveChangesAsync();
        return calendar.CalendarId;
    }

    private async Task<Guid> SeedLocationAsync(string name, Guid? mergedInto = null)
    {
        await using var context = NewContext();
        var location = new LocationEntity
        {
            Name = $"{name} {Guid.NewGuid():N}",
            Source = LocationSource.Manual,
            MergedIntoLocationId = mergedInto,
            Archived = mergedInto is null ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return location.LocationId;
    }

    private async Task MigratedAsync()
    {
        await using (var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options))
        {
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
            await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(
                fixture.ConnectionStringFor(Database),
                ServerVersion.AutoDetect(fixture.OdysseyConnectionString),
                mysql => mysql.EnableRetryOnFailure())
            .Options);
}
