// EF1002: the database names interpolated below are this class's own constants.
#pragma warning disable EF1002

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Locations.Conversion;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.MigrationService;
using Xunit;
using DtoPropertyType = Odyssey.Dtos.Finance.PropertyType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #380 §11 — the single-release location conversion, driven through <see cref="MigrationRunner"/>
/// against real MariaDB: stop after <c>UnifyLocationsExpand</c>, seed old-shape rows, then resume, so the
/// staged path (Expand → conversion → Contract) is what runs. The fixtures that call
/// <c>Database.MigrateAsync()</c> directly bypass the conversion by design (L3).
/// </summary>
/// <remarks>
/// The old address columns exist only between the two migrations, so <c>Addresses</c> and
/// <c>RealEstateDetails</c> are seeded with raw SQL; every other table's schema already matches the model
/// at that point and is seeded through the entities.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class LocationConversionMigrationTests(MariaDbFixture fixture)
{
    private const string Expand = "_" + ILocationConversion.ExpandMigration;
    private const string Contract = "_" + ILocationConversion.ContractMigration;

    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string LongPrefix = new('L', 120);

    /// <summary>
    /// AC 1–3 and every §11 "test shape the seed lacks" that one run can show: case variants merging, Å/A
    /// distinct, a Line1↔Line2 shift, the same address twice on one contact, city-only and country-only
    /// properties, long strings sharing a prefix, control and bidi characters, a <c>|</c> in a value,
    /// scheme-less meeting links and passcodes, keyword substrings that must convert, an archived-name
    /// match, a suffix past (6), and <c>Asen</c> beside <c>Åsen</c> seen three times.
    /// </summary>
    [SkippableFact]
    public async Task The_staged_upgrade_converts_every_address_and_place_once()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string database = "odyssey_location_conversion";
        await RecreateAsync(database);

        try
        {
            await using (var context = NewContext(database))
            {
                await MigrationSeam.MigrateToAsync(context, Expand);
                await SeedAsync(context);
            }

            var conversion = new RecordingConversion(database, fixture);
            await using (var context = NewContext(database))
            {
                await MigrationRunner.MigrateAsync(context, conversion, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }

            var result = Assert.Single(conversion.Results);
            Assert.True(result.Ran);

            await using (var context = NewContext(database))
            {
                // AC 1 — the old columns are gone, every address is linked.
                Assert.Equal(0, await MigrationSeam.CountAsync(context, """
                    SELECT COUNT(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE()
                      AND ((TABLE_NAME = 'Addresses' AND COLUMN_NAME IN ('Line1','Line2','City','PostalCode','Region','CountryCode'))
                        OR (TABLE_NAME = 'RealEstateDetails' AND COLUMN_NAME IN ('AddressLine','PostalCode','City','CountryCode')))
                    """));

                var addresses = await context.Addresses.AsNoTracking().Include(a => a.Location).ToListAsync();
                Assert.All(addresses, a => Assert.NotNull(a.Location));

                // Case variants on two contacts share one location; the duplicate on one contact is gone and
                // the primary row survived.
                var first = Assert.Single(addresses, a => a.ContactId == Id("contact-1"));
                Assert.Equal(Id("address-1a"), first.Id);
                Assert.True(first.IsPrimary);
                var second = addresses.Single(a => a.Id == Id("address-2a"));
                Assert.Equal(first.LocationId, second.LocationId);
                // Whichever spelling converted first is the one stored; the rows convert in key order.
                Assert.Equal(("STORGATA 1", "0150", "OSLO", "NO", LocationSource.ContactAddress),
                    (first.Location.Line1!.ToUpperInvariant(), first.Location.PostalCode, first.Location.City!.ToUpperInvariant(),
                        first.Location.CountryCode, first.Location.Source));
                Assert.Equal("Storgata 1, 0150 Oslo, NO", first.Location.Name, ignoreCase: true);
                Assert.Null(first.Location.Latitude);
                Assert.Null(first.Location.TimeZoneId);

                // A Line1↔Line2 shift is a different place.
                var shifted = addresses.Single(a => a.Id == Id("address-3a"));
                Assert.NotEqual(first.LocationId, shifted.LocationId);

                // Å and A stay distinct, and the colliding name is suffixed.
                var withRing = addresses.Single(a => a.Id == Id("address-2b")).Location;
                var withoutRing = addresses.Single(a => a.Id == Id("address-3b")).Location;
                Assert.NotEqual(withRing.LocationId, withoutRing.LocationId);
                // Which one converts first follows key order; the other is suffixed.
                Assert.Equal(
                    ["Asgata 2, 0150 Oslo, NO", "Åsgata 2, 0150 Oslo, NO"],
                    new[] { withRing.Name, withoutRing.Name }.Select(n => n.Replace(" (2)", "", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
                Assert.Single(new[] { withRing.Name, withoutRing.Name }, n => n.EndsWith(" (2)", StringComparison.Ordinal));

                // Control and bidi characters are cleaned out of a converted address.
                var cleaned = addresses.Single(a => a.Id == Id("address-3c")).Location;
                Assert.Equal(("Kirkegata 3", "Oslo"), (cleaned.Line1, cleaned.City));

                // Properties: same address as a contact → same place; city-only and country-only get their own.
                var details = await context.RealEstateDetails.AsNoTracking().Include(d => d.Location).ToListAsync();
                Assert.Equal(first.LocationId, details.Single(d => d.PropertyId == Id("property-same")).LocationId);
                var cityOnly = details.Single(d => d.PropertyId == Id("property-city")).Location!;
                Assert.Equal(("Hamar", null, "Hamar", LocationSource.PropertyAddress), (cityOnly.Name, cityOnly.Line1, cityOnly.City, cityOnly.Source));
                Assert.NotNull(cityOnly.AddressKey);
                var countryOnly = details.Single(d => d.PropertyId == Id("property-country")).Location!;
                Assert.Equal(("SE", "SE"), (countryOnly.Name, countryOnly.CountryCode));
                Assert.Null(countryOnly.AddressKey);
                Assert.Null(details.Single(d => d.PropertyId == Id("property-none")).LocationId);

                // AC 3 — strings.
                var events = await context.CalendarEvents.AsNoTracking().ToDictionaryAsync(e => e.CalendarEventId);
                var locations = await context.Locations.AsNoTracking().ToDictionaryAsync(l => l.LocationId);
                Location LinkedFrom(string key)
                {
                    var linked = events[Id(key)];
                    Assert.Null(linked.Location);
                    return locations[Assert.NotNull(linked.LocationId)];
                }

                // Asen beside Åsen, three times (event, series, entry): one location, one suffix, the
                // original in AddressText — and Åsen itself untouched.
                var asen = LinkedFrom("event-asen");
                Assert.Equal(("Asen (2)", "Asen", LocationSource.Converted), (asen.Name, asen.AddressText, asen.Source));
                var pattern = await context.RecurrencePatterns.AsNoTracking().SingleAsync(p => p.RecurrencePatternId == Id("pattern-asen"));
                var entry = await context.JournalEntries.AsNoTracking().SingleAsync(e => e.JournalEntryId == Id("entry-asen"));
                Assert.Equal(asen.LocationId, pattern.LocationId);
                Assert.Equal(asen.LocationId, entry.LocationId);
                Assert.Null(pattern.Location);
                Assert.Null(entry.Location);
                Assert.Single(locations.Values, l => l.Name.StartsWith("Asen", StringComparison.Ordinal));

                // Exact-letter match onto an existing place, and onto an archived one (left archived).
                Assert.Equal(Id("location-åsen"), LinkedFrom("event-åsen").LocationId);
                var archived = LinkedFrom("event-archived");
                Assert.Equal(Id("location-archived"), archived.LocationId);
                Assert.NotNull(archived.Archived);

                // Suffixes past (6): Cåbin collides with Cabin … Cabin (7) under the collation.
                Assert.Equal("Cåbin (8)", LinkedFrom("event-cabin").Name);

                // Long strings sharing a prefix: distinct, truncated names; the full value in AddressText.
                var longOne = LinkedFrom("event-long-1");
                var longTwo = LinkedFrom("event-long-2");
                Assert.NotEqual(longOne.LocationId, longTwo.LocationId);
                Assert.All(new[] { longOne, longTwo }, l => Assert.True(l.Name.Length <= LocationRules.NameMaxLength));
                Assert.Equal(LongPrefix + " one", longOne.AddressText);
                Assert.Equal(LongPrefix + " two", longTwo.AddressText);
                Assert.Single(new[] { longOne, longTwo }, l => l.Name.EndsWith(" (2)", StringComparison.Ordinal));

                // Bidi stripped; a pipe kept; keyword substrings convert.
                Assert.Equal("Moo", LinkedFrom("event-bidi").Name);
                Assert.Equal("A | B", LinkedFrom("event-pipe").Name);
                foreach (var key in new[] { "event-spinneriet", "event-camping", "event-koden" })
                {
                    Assert.NotNull(events[Id(key)].LocationId);
                }

                // Link-like values stay as detail text, unlinked.
                foreach (var (key, text) in LinkLike)
                {
                    Assert.Null(events[Id(key)].LocationId);
                    Assert.Equal(text, events[Id(key)].Location);
                }

                Assert.Equal(LinkLike.Length, result.LeftAsText);
            }

            // AC 4 — a later upgrade never re-runs the conversion.
            await using (var context = NewContext(database))
            {
                await MigrationRunner.MigrateAsync(context, conversion, CancellationToken.None);
            }

            Assert.Single(conversion.Results);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>
    /// AC 4 — interrupting the conversion mid-batch at ANY batch and restarting yields the same final state as
    /// an uninterrupted run (senior-tester M3). Batches of two, so every source table spans at least two; each
    /// table has its own batching and resume key, so each is interrupted at two positions, including the very
    /// first batch of the run (a failure before anything committed).
    /// </summary>
    [SkippableTheory]
    [InlineData("Addresses", 1)]
    [InlineData("Addresses", 3)]
    [InlineData("RealEstateDetails", 1)]
    [InlineData("RealEstateDetails", 2)]
    [InlineData("CalendarEvents", 3)]
    [InlineData("CalendarEvents", 9)]
    [InlineData("RecurrencePatterns", 1)]
    [InlineData("RecurrencePatterns", 2)]
    [InlineData("JournalEntries", 1)]
    [InlineData("JournalEntries", 2)]
    public async Task An_interrupted_conversion_resumes_to_the_uninterrupted_result(string table, int batch)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var suffix = $"{table.ToLowerInvariant()}_{batch}";
        var straight = $"odyssey_lc_straight_{suffix}";
        var interrupted = $"odyssey_lc_interrupted_{suffix}";
        await RecreateAsync(straight);
        await RecreateAsync(interrupted);

        try
        {
            foreach (var database in new[] { straight, interrupted })
            {
                await using var context = NewContext(database);
                await MigrationSeam.MigrateToAsync(context, Expand);
                await SeedAsync(context);
            }

            await using (var context = NewContext(straight))
            {
                await MigrationRunner.MigrateAsync(
                    context, new LocationConversion(context, options: new LocationConversionOptions { BatchSize = 2 }),
                    CancellationToken.None);
            }

            var reached = false;
            await using (var context = NewContext(interrupted))
            {
                var failing = new LocationConversion(context, options: new LocationConversionOptions
                {
                    BatchSize = 2,
                    BeforeCommit = (current, ordinal) =>
                    {
                        if (current == table && ordinal == batch)
                        {
                            reached = true;
                            throw new InvalidOperationException("Simulated interruption.");
                        }
                    },
                });
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => MigrationRunner.MigrateAsync(context, failing, CancellationToken.None));
                Assert.False(await MigrationSeam.HasRunAsync(context, Contract));
                Assert.True(await MigrationSeam.HasRunAsync(context, Expand));
            }

            // The case is only meaningful if the seed really spans that batch.
            Assert.True(reached, $"The seed never reached {table} batch {batch}.");

            await using (var context = NewContext(interrupted))
            {
                await MigrationRunner.MigrateAsync(
                    context, new LocationConversion(context, options: new LocationConversionOptions { BatchSize = 2 }),
                    CancellationToken.None);
            }

            Assert.Equal(await SnapshotAsync(straight), await SnapshotAsync(interrupted));
        }
        finally
        {
            await DropAsync(straight);
            await DropAsync(interrupted);
        }
    }

    /// <summary>
    /// A contact address whose six fields are all empty once cleaned (control characters and whitespace only — a
    /// shape the demo seed never produces) cannot be linked. By product decision on PR #385 the conversion deletes
    /// it and the upgrade completes: the row is gone, the contact and its other addresses survive, and the
    /// contract migration runs.
    /// </summary>
    [SkippableFact]
    public async Task An_address_with_no_usable_text_is_removed_and_the_upgrade_completes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string database = "odyssey_location_conversion_empty_address";
        await RecreateAsync(database);

        try
        {
            await using (var context = NewContext(database))
            {
                await MigrationSeam.MigrateToAsync(context, Expand);
                await SeedAsync(context);
                await AddressAsync(context, "address-empty", "contact-2", primary: false, "\u0007", null, " \t ", "\u202E", null, "\u0001");
            }

            long addressesBefore;
            await using (var context = NewContext(database))
            {
                addressesBefore = await MigrationSeam.CountAsync(context,
                    $"SELECT COUNT(*) FROM `Addresses` WHERE `ContactId` = '{Id("contact-2")}' AND `Id` <> '{Id("address-empty")}'");
                await MigrationRunner.MigrateAsync(context, new LocationConversion(context), CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }

            await using (var context = NewContext(database))
            {
                Assert.False(await context.Addresses.AsNoTracking().AnyAsync(a => a.Id == Id("address-empty")));
                Assert.Equal(addressesBefore, await context.Addresses.CountAsync(a => a.ContactId == Id("contact-2")));
                Assert.True(await context.Contacts.AsNoTracking().AnyAsync(c => c.ContactId == Id("contact-2")));
            }
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>
    /// AC 4 — a start after a partly applied Contract (one table's columns dropped, the other's not)
    /// returns from the conversion without error, and the drift guard reports the half-applied DDL.
    /// </summary>
    [SkippableFact]
    public async Task A_partly_applied_contract_skips_the_conversion_and_is_reported_as_drift()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string database = "odyssey_location_conversion_partial";
        await RecreateAsync(database);

        try
        {
            await using (var context = NewContext(database))
            {
                await MigrationSeam.MigrateToAsync(context, Expand);
                await SeedAsync(context);
                await new LocationConversion(context).ConvertAsync(CancellationToken.None);

                // What an interruption part-way through UnifyLocationsContract leaves: the index it creates
                // first, and the Addresses columns dropped while RealEstateDetails keeps its own.
                await context.Database.ExecuteSqlRawAsync("""
                    DELETE duplicate FROM `Addresses` AS duplicate
                    JOIN `Addresses` AS kept ON kept.`ContactId` = duplicate.`ContactId`
                     AND kept.`LocationId` = duplicate.`LocationId` AND kept.`Id` <> duplicate.`Id`
                     AND (kept.`IsPrimary` > duplicate.`IsPrimary` OR (kept.`IsPrimary` = duplicate.`IsPrimary` AND kept.`Id` < duplicate.`Id`));
                    """);
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX `IX_Addresses_ContactId_LocationId` ON `Addresses` (`ContactId`, `LocationId`)");
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE `Addresses` DROP COLUMN `Line1`, DROP COLUMN `Line2`, DROP COLUMN `City`, "
                    + "DROP COLUMN `PostalCode`, DROP COLUMN `Region`, DROP COLUMN `CountryCode`");
            }

            var conversion = new RecordingConversion(database, fixture);
            await using (var context = NewContext(database))
            {
                var drift = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, conversion, CancellationToken.None));
                Assert.EndsWith(Contract, drift.MigrationId, StringComparison.Ordinal);
            }

            var result = Assert.Single(conversion.Results);
            Assert.False(result.Ran);
        }
        finally
        {
            await DropAsync(database);
        }
    }

    /// <summary>AC 4 — a fresh database upgrades through all three steps; the conversion finds nothing.</summary>
    [SkippableFact]
    public async Task A_fresh_database_upgrades_through_all_three_steps()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string database = "odyssey_location_conversion_fresh";
        await RecreateAsync(database);

        try
        {
            var conversion = new RecordingConversion(database, fixture);
            await using (var context = NewContext(database))
            {
                await MigrationRunner.MigrateAsync(context, conversion, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            }

            Assert.Equal(new LocationConversionResult(true, 0, 0, 0), Assert.Single(conversion.Results));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    // ── Seed ──────────────────────────────────────────────────────────────────

    private static readonly (string Key, string Text)[] LinkLike =
    [
        ("event-url", "https://meet.example.com/abc"),
        ("event-hostpath", "meet.google.com/abc-defg-hij"),
        ("event-barehost", "whereby.com"),
        ("event-www", "www.example.org"),
        ("event-passcode", "Zoom, passcode 123456"),
        ("event-passord", "Teams møte, passord 9911"),
        ("event-meetingid", "Meeting ID 123 456 789"),
    ];

    private static async Task SeedAsync(OdysseyContext context)
    {
        // Existing places: Åsen, an archived one, and Cabin … Cabin (7) to push a suffix past (6). Raw SQL: the
        // Location entity maps a later column (Locations.WeatherPointId, issue #387) this schema does not have yet.
        await LocationAsync(context, "location-åsen", "Åsen");
        await LocationAsync(context, "location-archived", "Old Cabin", archived: Now);
        await LocationAsync(context, "location-cabin-1", "Cabin");
        for (var i = 2; i <= 7; i++)
        {
            await LocationAsync(context, $"location-cabin-{i}", $"Cabin ({i})");
        }

        // Contacts and their old-shape addresses.
        foreach (var key in new[] { "contact-1", "contact-2", "contact-3" })
        {
            await BaselineContacts.AddOrganizationAsync(context, Id(key), key);
        }

        await AddressAsync(context, "address-1a", "contact-1", primary: true, "Storgata 1", null, "Oslo", "0150", null, "NO");
        // The same address twice on one contact, case-varied: dedupes to the primary row.
        await AddressAsync(context, "address-1b", "contact-1", primary: false, "storgata  1", null, "OSLO", "0150", null, "no");
        await AddressAsync(context, "address-2a", "contact-2", primary: true, "STORGATA 1", null, "oslo", "0150", null, "NO");
        await AddressAsync(context, "address-2b", "contact-2", primary: false, "Åsgata 2", null, "Oslo", "0150", null, "NO");
        await AddressAsync(context, "address-3a", "contact-3", primary: true, "Bakgården", "Storgata 1", "Oslo", "0150", null, "NO");
        await AddressAsync(context, "address-3b", "contact-3", primary: false, "Asgata 2", null, "Oslo", "0150", null, "NO");
        await AddressAsync(context, "address-3c", "contact-3", primary: false, "Kirke\u202Egata 3", null, "Os\u0007lo", null, null, "NO");

        // Properties.
        await PropertyAsync(context, "property-same", "Storgata 1", "0150", "Oslo", "NO");
        await PropertyAsync(context, "property-city", null, null, "Hamar", null);
        await PropertyAsync(context, "property-country", null, null, null, "SE");
        await PropertyAsync(context, "property-none", null, null, null, null);

        // Free-text places.
        // Raw SQL, like the locations above: the schema here predates later Calendars columns (Icon), which
        // an EF insert through today's model would name.
        var calendarId = Id("calendar");
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO `Calendars` (`CalendarId`, `Name`, `Color`, `CreatedAt`, `UpdatedAt`)
            VALUES ({calendarId}, {"Conversion"}, {ColorPalette.CalendarDefault}, {Now}, {Now})
            """);
        var texts = new List<(string Key, string Text)>
        {
            ("event-asen", "Asen"),
            ("event-åsen", "ÅSEN"),
            ("event-archived", "old cabin"),
            ("event-cabin", "Cåbin"),
            ("event-long-1", LongPrefix + " one"),
            ("event-long-2", LongPrefix + " two"),
            ("event-bidi", "M\u202Eo\u2066o"),
            ("event-pipe", "A | B"),
            ("event-spinneriet", "Spinneriet"),
            ("event-camping", "Camping"),
            ("event-koden", "Koden"),
        };
        texts.AddRange(LinkLike);
        foreach (var (key, text) in texts)
        {
            context.CalendarEvents.Add(new CalendarEvent
            {
                CalendarEventId = Id(key), CalendarId = calendarId, Title = key, Location = text,
                StartDateTime = Now, EndDateTime = Now.AddHours(1), CreatedAt = Now, UpdatedAt = Now,
            });
        }

        // Three of each, so both tables span two batches of two (the interruption theory, M3). The extra
        // values reuse places the events already created, so they add no location of their own.
        foreach (var (key, text) in new[] { ("pattern-asen", "Asen"), ("pattern-spinneriet", "Spinneriet"), ("pattern-camping", "Camping") })
        {
            context.RecurrencePatterns.Add(new RecurrencePattern
            {
                RecurrencePatternId = Id(key), CalendarId = calendarId, Title = "Series", Location = text,
                StartDateTime = Now, EndDateTime = Now.AddHours(1), Frequency = Odyssey.Context.RecurrenceFrequency.Weekly, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        foreach (var (key, text) in new[] { ("entry-asen", "asen"), ("entry-koden", "Koden"), ("entry-pipe", "A | B") })
        {
            context.JournalEntries.Add(new JournalEntry
            {
                JournalEntryId = Id(key), ExternalUid = "urn:uuid:" + Id(key), Title = "Entry", Content = "Body",
                Location = text, EntryDate = Now, CreatedAt = Now, UpdatedAt = Now,
            });
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static Task LocationAsync(OdysseyContext context, string key, string name, DateTime? archived = null) =>
        context.Database.ExecuteSqlAsync($"""
            INSERT INTO `Locations` (`LocationId`, `Name`, `Latitude`, `Longitude`, `TimeZoneId`, `Source`, `Archived`, `CreatedAt`, `UpdatedAt`)
            VALUES ({Id(key)}, {name}, {59.9m}, {10.7m}, {"Europe/Oslo"}, {(int)LocationSource.Manual}, {archived}, {Now}, {Now})
            """);

    private static Task AddressAsync(
        OdysseyContext context, string key, string contactKey, bool primary,
        string line1, string? line2, string city, string? postalCode, string? region, string countryCode) =>
        context.Database.ExecuteSqlAsync($"""
            INSERT INTO `Addresses` (`Id`, `ContactId`, `Label`, `IsPrimary`, `Line1`, `Line2`, `City`, `PostalCode`, `Region`, `CountryCode`)
            VALUES ({Id(key)}, {Id(contactKey)}, {(int)AddressLabel.Home}, {primary}, {line1}, {line2}, {city}, {postalCode}, {region}, {countryCode})
            """);

    private static async Task PropertyAsync(
        OdysseyContext context, string key, string? addressLine, string? postalCode, string? city, string? countryCode)
    {
        context.Properties.Add(new Property
        {
            PropertyId = Id(key), Name = key, Description = key, Type = DtoPropertyType.RealEstate, CurrencyCode = "NOK",
            CreatedAt = Now, UpdatedAt = Now,
        });
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO `RealEstateDetails` (`PropertyId`, `Kind`, `AddressLine`, `PostalCode`, `City`, `CountryCode`)
            VALUES ({Id(key)}, {(int)Odyssey.Dtos.Finance.RealEstateKind.House}, {addressLine}, {postalCode}, {city}, {countryCode})
            """);
    }

    /// <summary>Every linked row as (row id → location name), plus every location's name and text.</summary>
    private async Task<string> SnapshotAsync(string database)
    {
        await using var context = NewContext(database);
        var locations = await context.Locations.AsNoTracking().ToDictionaryAsync(l => l.LocationId);
        string Name(Guid? id) => id is { } value ? locations[value].Name : "-";

        var lines = new List<string>();
        lines.AddRange((await context.Addresses.AsNoTracking().ToListAsync()).Select(a => $"A {a.Id} {Name(a.LocationId)}"));
        lines.AddRange((await context.RealEstateDetails.AsNoTracking().ToListAsync()).Select(d => $"P {d.PropertyId} {Name(d.LocationId)}"));
        lines.AddRange((await context.CalendarEvents.AsNoTracking().ToListAsync()).Select(e => $"E {e.CalendarEventId} {Name(e.LocationId)} {e.Location}"));
        lines.AddRange((await context.RecurrencePatterns.AsNoTracking().ToListAsync()).Select(p => $"R {p.RecurrencePatternId} {Name(p.LocationId)} {p.Location}"));
        lines.AddRange((await context.JournalEntries.AsNoTracking().ToListAsync()).Select(j => $"J {j.JournalEntryId} {Name(j.LocationId)} {j.Location}"));
        lines.AddRange(locations.Values.Select(l => $"L {l.Name} | {l.AddressText} | {l.AddressKey} | {l.Source}"));
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    private static Guid Id(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes("location-conversion::" + key)));

    /// <summary>Records each conversion run's result, so a test can see whether and how the runner called it.</summary>
    private sealed class RecordingConversion(string database, MariaDbFixture fixture) : ILocationConversion
    {
        public List<LocationConversionResult> Results { get; } = [];

        public async Task<LocationConversionResult> ConvertAsync(CancellationToken cancellationToken)
        {
            await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
                .UseMySql(fixture.ConnectionStringFor(database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
                .Options);
            var result = await new LocationConversion(context).ConvertAsync(cancellationToken);
            Results.Add(result);
            return result;
        }
    }

    // ── Database lifecycle ────────────────────────────────────────────────────

    private OdysseyContext NewContext(string database) =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync(string database)
    {
        await DropAsync(database);
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{database}`");
    }

    private async Task DropAsync(string database)
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
