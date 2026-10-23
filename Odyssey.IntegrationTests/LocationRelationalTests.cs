// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid
// this test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #350 (AC 3, 18, 19): the migration, the five CHECK constraints, the unique
/// name index under the column's <c>_ci</c> collation, the duplicate-key race mapping to <c>409</c>, and
/// the attribution keys nulling on a user delete. None of it is observable on the EF InMemory provider,
/// which runs no migrations and enforces no index, key or CHECK.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class LocationRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_locations";
    private const string PreviousMigration = "_AddJournalTaskPhotoTagIcons";
    private const string ThisMigration = "_AddLocations";

    [SkippableFact]
    public async Task The_migration_creates_the_table_from_the_previous_one()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
            Assert.False(await MigrationSeam.TableExistsAsync(context, "Locations"));
            await MigrationSeam.MigrateToAsync(context, ThisMigration);
            Assert.True(await MigrationSeam.TableExistsAsync(context, "Locations"));
        }
    }

    public static TheoryData<string, string> OutOfRangeRows() => new()
    {
        { "CK_Locations_SourceKnown", "`Source` = 0" },
        { "CK_Locations_ManualHasNoReference", "`Source` = 1, `SourceReference` = 'x'" },
        { "CK_Locations_LatitudeRange", "`Latitude` = 90.5" },
        { "CK_Locations_LongitudeRange", "`Longitude` = -180.5" },
        { "CK_Locations_AltitudeRange", "`AltitudeMeters` = 9001" },
    };

    [SkippableTheory]
    [MemberData(nameof(OutOfRangeRows))]
    public async Task Each_check_constraint_rejects_an_out_of_range_row(string constraint, string assignment)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();

        var columns = new Dictionary<string, string>
        {
            ["LocationId"] = $"'{Guid.NewGuid()}'",
            ["Name"] = $"'Row {Guid.NewGuid():N}'",
            ["Latitude"] = "61.5",
            ["Longitude"] = "10.1",
            ["TimeZoneId"] = "'Europe/Oslo'",
            ["Source"] = "2",
            ["CreatedAt"] = "NOW()",
            ["UpdatedAt"] = "NOW()",
        };
        foreach (var pair in assignment.Split(", "))
        {
            var parts = pair.Split(" = ");
            columns[parts[0].Trim('`')] = parts[1];
        }

        var sql = $"INSERT INTO `Locations` ({string.Join(", ", columns.Keys.Select(k => $"`{k}`"))}) "
            + $"VALUES ({string.Join(", ", columns.Values)})";
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(sql));

        Assert.Contains(constraint, ex.GetBaseException().Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_unique_index_folds_case_and_accents_like_the_service()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        await using (var context = NewContext())
        {
            context.Locations.Add(Row("Cabin"));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            context.Locations.Add(Row("Cabín"));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(1062, (ex.GetBaseException() as MySqlException)?.Number);
        }
    }

    [SkippableFact]
    public async Task Two_concurrent_creates_of_one_name_yield_one_location_and_one_409_never_a_500()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using (var seed = NewContext())
        {
            await AttributionUsers.EnsureAsync(seed, "race-user");
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = NewContext();
            try
            {
                await new LocationService(context).Create(New("Cabin"), "race-user");
                return "created";
            }
            catch (DomainConflictException)
            {
                return "409";
            }
        }));

        Assert.Equal(["409", "created"], outcomes.Order(StringComparer.Ordinal));
        await using var verify = NewContext();
        Assert.Equal(1, await verify.Locations.CountAsync());
    }

    [SkippableFact]
    public async Task The_service_round_trips_decimals_at_five_places_on_the_real_engine()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        Guid id;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, "user");
            id = (await new LocationService(context).Create(
                New("Precise") with { Latitude = -89.999995m, Longitude = 179.999994m }, "user")).LocationId;
        }

        await using var verify = NewContext();
        var row = await verify.Locations.AsNoTracking().SingleAsync(l => l.LocationId == id);
        Assert.Equal(-90.00000m, row.Latitude);
        Assert.Equal(179.99999m, row.Longitude);
    }

    [SkippableFact]
    public async Task Deleting_a_user_nulls_the_attribution_and_keeps_the_location()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        const string userId = "location-author";

        Guid id;
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, userId);
            id = (await new LocationService(context).Create(New("Cabin"), userId)).LocationId;
        }

        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlAsync($"DELETE FROM `AspNetUsers` WHERE `Id` = {userId}");
        }

        await using var verify = NewContext();
        var row = await verify.Locations.AsNoTracking().SingleAsync(l => l.LocationId == id);
        Assert.Null(row.CreatedByUserId);
        Assert.Null(row.UpdatedByUserId);
    }

    private static NewLocation New(string name) => new()
    {
        Name = name,
        Latitude = 61.52914m,
        Longitude = 10.13802m,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Kartverket,
        SourceReference = "3439-1020-12",
    };

    private static Location Row(string name) => new()
    {
        LocationId = Guid.NewGuid(),
        Name = name,
        Latitude = 61.5m,
        Longitude = 10.1m,
        TimeZoneId = "Europe/Oslo",
        Source = LocationSource.Manual,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task MigratedAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }
}
