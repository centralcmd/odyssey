// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid this test
// generated itself — there is no external input — and the deletes are deliberately issued outside the
// services, so the ENGINE resolves the foreign keys rather than application code.
#pragma warning disable EF1002

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #167: the purely additive migration (AC 19, 28), the declared on-delete
/// behaviours (AC 20), the list's value sort on the real engine (AC 9), the shared effective-dating
/// queries translating for BOTH estimate tables (AC 21), and the duplicate-pair race on smart tags.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers: the EF InMemory provider enforces no foreign keys, runs
/// no migrations, and orders nulls by the CLR comparer rather than by the engine's rule.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class PropertyRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_properties";
    private const string PreviousMigration = "_AddTextAndDateTimeTermKinds";
    private const string ThisMigration = "_AddProperties";

    private static readonly string[] NewTables =
        ["Properties", "RealEstateDetails", "VehicleDetails", "PropertyEstimates", "PropertySmartTags"];

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Migration ────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 19 — applied to a populated database at the previous migration, this one leaves every existing
    /// table exactly as it was: same tables, same columns (name, type, nullability, key), same indexes,
    /// and the account estimate and smart-tag rows untouched. The assertion is additivity, which is the
    /// property the whole design rests on.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_is_purely_additive_over_a_populated_database()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
            await SeedAccountEstimateAndSmartTagAsync(context);
        }

        Snapshot before;
        await using (var context = NewContext())
        {
            before = await SnapshotAsync(context);
            Assert.DoesNotContain("Properties", before.Tables);
            await MigrationSeam.MigrateToAsync(context, ThisMigration);
        }

        await using (var context = NewContext())
        {
            var after = await SnapshotAsync(context);

            Assert.Equal(before.Tables.Concat(NewTables).OrderBy(t => t, StringComparer.Ordinal), after.Tables);
            Assert.Equal(before.Columns, after.Columns.Where(c => !NewTables.Contains(c.Split('.')[0])).ToList());
            Assert.Equal(before.Indexes, after.Indexes.Where(i => !NewTables.Contains(i.Split('.')[0])).ToList());
            Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `AccountEstimates`"));
            Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `AccountSmartTags`"));

            // …and the one row it inserts is the settings seed, at its shipped default.
            Assert.Equal(before.SettingsRows + 1, after.SettingsRows);
            Assert.Equal(
                SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty.ToString(),
                await context.SystemSettings.AsNoTracking()
                    .Where(row => row.Key == SystemSettingsKeys.PropertyMaxSmartTagsPerProperty)
                    .Select(row => row.Value)
                    .SingleAsync());
        }
    }

    /// <summary>
    /// AC 28 — <c>Down()</c> on a database holding properties does not throw and removes exactly what
    /// <c>Up()</c> added; re-applying <c>Up()</c> reproduces the same schema.
    /// </summary>
    [SkippableFact]
    public async Task Down_then_up_on_a_populated_database_round_trips_the_schema()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Snapshot first;
        await using (var context = NewContext())
        {
            await new PropertyService(context).Create(House("Storgata 14"), userId: null);
            first = await SnapshotAsync(context);

            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
        }

        await using (var context = NewContext())
        {
            foreach (var table in NewTables)
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table), $"{table} survived Down().");
            }

            Assert.False(await context.SystemSettings.AsNoTracking()
                .AnyAsync(row => row.Key == SystemSettingsKeys.PropertyMaxSmartTagsPerProperty));

            await context.Database.MigrateAsync();
        }

        await using (var context = NewContext())
        {
            var second = await SnapshotAsync(context);
            Assert.Equal(first.Tables, second.Tables);
            Assert.Equal(first.Columns, second.Columns);
            Assert.Equal(first.Indexes, second.Indexes);
        }
    }

    /// <summary>AC 20 — every foreign key the feature adds carries the on-delete behaviour §4 names.</summary>
    [SkippableFact]
    public async Task Every_new_foreign_key_carries_the_declared_on_delete_behaviour()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var rules = await context.Database
            .SqlQuery<ForeignKeyRule>($"""
                SELECT k.TABLE_NAME AS TableName, k.COLUMN_NAME AS ColumnName,
                       k.REFERENCED_TABLE_NAME AS ReferencedTable, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE()
                  AND k.TABLE_NAME IN ('RealEstateDetails', 'VehicleDetails', 'PropertyEstimates', 'PropertySmartTags')
                ORDER BY k.TABLE_NAME, k.COLUMN_NAME
                """)
            .ToListAsync();

        Assert.Equal(
        [
            new ForeignKeyRule("PropertyEstimates", "PropertyId", "Properties", "CASCADE"),
            new ForeignKeyRule("PropertySmartTags", "PropertyId", "Properties", "CASCADE"),
            new ForeignKeyRule("PropertySmartTags", "TransactionTagId", "TransactionTags", "RESTRICT"),
            new ForeignKeyRule("RealEstateDetails", "PropertyId", "Properties", "CASCADE"),
            new ForeignKeyRule("VehicleDetails", "PropertyId", "Properties", "CASCADE"),
        ], rules);
    }

    // ── Cascades and the RESTRICT backstop ───────────────────────────────────

    /// <summary>
    /// AC 20 — deleting a property at the ENGINE takes its detail row, estimates and smart-tag links with
    /// it, leaves another property's alone, and leaves the tag intact.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_property_cascades_details_estimates_and_links_but_keeps_the_tag()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid doomed, surviving, tagId;
        await using (var context = NewContext())
        {
            doomed = await SeedWatchedPropertyAsync(context, "Doomed", tagId = await SeedTagAsync(context, "Maintenance"));
            surviving = await SeedWatchedPropertyAsync(context, "Survivor", tagId);

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Properties` WHERE `PropertyId` = '{doomed}'");
        }

        await using var verify = NewContext();
        Assert.False(await verify.RealEstateDetails.AnyAsync(d => d.PropertyId == doomed));
        Assert.False(await verify.PropertyEstimates.AnyAsync(e => e.PropertyId == doomed));
        Assert.False(await verify.PropertySmartTags.AnyAsync(l => l.PropertyId == doomed));
        Assert.True(await verify.RealEstateDetails.AnyAsync(d => d.PropertyId == surviving));
        Assert.True(await verify.PropertyEstimates.AnyAsync(e => e.PropertyId == surviving));
        Assert.True(await verify.PropertySmartTags.AnyAsync(l => l.PropertyId == surviving));
        Assert.True(await verify.TransactionTags.AnyAsync(t => t.TransactionTagId == tagId));
    }

    /// <summary>The service's own delete agrees with the engine on the real provider.</summary>
    [SkippableFact]
    public async Task The_service_delete_removes_the_whole_aggregate()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var tagId = await SeedTagAsync(context, "Maintenance");
        var propertyId = await SeedWatchedPropertyAsync(context, "Doomed", tagId);

        Assert.True(await new PropertyService(context).Delete(propertyId, userId: null));

        await using var verify = NewContext();
        Assert.False(await verify.Properties.AnyAsync());
        Assert.False(await verify.RealEstateDetails.AnyAsync());
        Assert.False(await verify.PropertyEstimates.AnyAsync());
        Assert.False(await verify.PropertySmartTags.AnyAsync());
    }

    /// <summary>
    /// AC 20/22 — a watched tag is refused by the <c>RESTRICT</c> key at the engine (the backstop), and by
    /// the service pre-check with a count and no names (the guard every tier runs).
    /// </summary>
    [SkippableFact]
    public async Task A_watched_tag_is_refused_by_the_engine_and_by_the_pre_check()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var tagId = await SeedTagAsync(context, "Maintenance");
        await SeedWatchedPropertyAsync(context, "Storgata 14", tagId);

        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM `TransactionTags` WHERE `TransactionTagId` = '{tagId}'"));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => new TransactionTagService(context).Delete(tagId));
        Assert.Contains("1 property", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Storgata 14", conflict.Message, StringComparison.Ordinal);
    }

    /// <summary>§9 — two concurrent adds of one pair race to the composite key; exactly one row lands.</summary>
    [SkippableFact]
    public async Task Two_concurrent_adds_of_the_same_pair_yield_exactly_one_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, tagId;
        await using (var seed = NewContext())
        {
            propertyId = (await new PropertyService(seed).Create(House("Storgata 14"), userId: null)).PropertyId;
            tagId = await SeedTagAsync(seed, "Maintenance");
        }

        await using var first = NewContext();
        await using var second = NewContext();
        var limits = new StubPropertyLimitsLookup();

        var outcomes = await Task.WhenAll(
            AttemptAsync(new PropertySmartTagService(first, limits), propertyId, tagId),
            AttemptAsync(new PropertySmartTagService(second, limits), propertyId, tagId));

        Assert.Equal(1, outcomes.Count(succeeded => succeeded));
        await using var verify = NewContext();
        Assert.Equal(1, await verify.PropertySmartTags.CountAsync());

        static async Task<bool> AttemptAsync(PropertySmartTagService service, Guid propertyId, Guid tagId)
        {
            try
            {
                await service.AddSmartTag(propertyId, tagId);
                return true;
            }
            catch (Exception exception) when (exception is DbUpdateException or DomainConflictException)
            {
                return false;
            }
        }
    }

    // ── Queries on the real engine ───────────────────────────────────────────

    /// <summary>
    /// AC 9 — <c>sortBy=Value</c> translates to a correlated subquery on MariaDB, resolves "current" by the
    /// supersession rule (a later, lower estimate beats an earlier, higher one; a future one is ignored),
    /// and puts estimate-less properties last in BOTH directions — which MariaDB's own null ordering would
    /// not do for a descending sort.
    /// </summary>
    [SkippableTheory]
    [InlineData(SortDirection.Asc)]
    [InlineData(SortDirection.Desc)]
    public async Task The_value_sort_resolves_the_current_estimate_and_puts_nulls_last(SortDirection direction)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var properties = new PropertyService(context);
        var estimates = new PropertyEstimateService(context);
        var cheap = (await properties.Create(House("Cheap"), userId: null)).PropertyId;
        var dear = (await properties.Create(House("Dear"), userId: null)).PropertyId;
        await properties.Create(House("Unvalued"), userId: null);
        await estimates.Create(cheap, Estimate(5000m, Anchor.AddDays(-1)));
        await estimates.Create(cheap, Estimate(100m, Anchor));
        await estimates.Create(cheap, Estimate(99999m, DateTime.UtcNow.AddYears(1)));
        await estimates.Create(dear, Estimate(900m, Anchor));

        var page = await properties.ListAsync(new PropertiesQueryParams { SortBy = PropertySortBy.Value, SortDir = direction });

        Assert.Equal(
            direction == SortDirection.Asc ? ["Cheap", "Dear", "Unvalued"] : ["Dear", "Cheap", "Unvalued"],
            page.Items.Select(p => p.Name).ToList());
    }

    /// <summary>AC 7 — search reaches the detail rows and the status filter reads the derived columns.</summary>
    [SkippableFact]
    public async Task Search_and_status_filters_translate_on_the_real_engine()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var properties = new PropertyService(context);
        await properties.Create(House("Alpha"), userId: null);
        var sold = Car("Old car");
        sold.DisposedDate = Anchor;
        await properties.Create(sold, userId: null);

        var byPlate = await properties.ListAsync(new PropertiesQueryParams { Search = "ab 123" });
        Assert.Empty(byPlate.Items);
        Assert.Equal("Old car", Assert.Single((await properties.ListAsync(new PropertiesQueryParams { Search = "ab123" })).Items).Name);
        Assert.Equal("Alpha", Assert.Single((await properties.ListAsync(new PropertiesQueryParams { Search = "OSLO" })).Items).Name);

        var disposed = await properties.ListAsync(new PropertiesQueryParams { Statuses = [PropertyStatus.Disposed] });
        Assert.Equal("Old car", Assert.Single(disposed.Items).Name);
        Assert.Equal(PropertyStatus.Disposed, disposed.Items[0].Status);
    }

    /// <summary>
    /// AC 21 — both estimate services route their duplicate-date and current-as-of queries through
    /// <see cref="EstimateEffectiveDating"/>; this pins that the generic helper translates on MariaDB for
    /// the account table as well as the property one, with the same answers the account surface gave
    /// before the extraction.
    /// </summary>
    [SkippableFact]
    public async Task The_shared_queries_translate_for_both_estimate_tables()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var account = new Account
        {
            Name = "House account",
            Description = string.Empty,
            Opened = Anchor,
            AccountType = ContextAccountType.Property,
            CurrencyCode = "USD",
        };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        var accountEstimates = new AccountEstimateService(context);
        await accountEstimates.Create(account.AccountId, new NewAccountEstimate { Value = 1m, EffectiveFrom = Anchor });
        await accountEstimates.Create(account.AccountId, new NewAccountEstimate { Value = 2m, EffectiveFrom = Anchor.AddMonths(1) });
        await Assert.ThrowsAsync<DomainConflictException>(() =>
            accountEstimates.Create(account.AccountId, new NewAccountEstimate { Value = 3m, EffectiveFrom = Anchor }));
        Assert.Equal(1m, (await accountEstimates.GetCurrent(account.AccountId, Anchor.AddDays(1)))!.Value);
        Assert.Equal(2m, (await accountEstimates.GetCurrent(account.AccountId, Anchor.AddMonths(2)))!.Value);
        Assert.Equal([2m, 1m], (await accountEstimates.GetHistory(account.AccountId))!.Select(e => e.Value));

        var propertyId = (await new PropertyService(context).Create(House("Storgata 14"), userId: null)).PropertyId;
        var propertyEstimates = new PropertyEstimateService(context);
        await propertyEstimates.Create(propertyId, Estimate(1m, Anchor));
        await propertyEstimates.Create(propertyId, Estimate(2m, Anchor.AddMonths(1)));
        await Assert.ThrowsAsync<DomainConflictException>(() => propertyEstimates.Create(propertyId, Estimate(3m, Anchor)));
        Assert.Equal(1m, (await propertyEstimates.GetCurrent(propertyId, Anchor.AddDays(1)))!.Value);
        Assert.Equal(2m, (await propertyEstimates.GetCurrent(propertyId, Anchor.AddMonths(2)))!.Value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NewProperty House(string name) => new()
    {
        Name = name,
        Description = "Residence",
        Type = PropertyType.RealEstate,
        CurrencyCode = "NOK",
        RealEstateDetails = new RealEstateDetailsDto { Kind = RealEstateKind.House, City = "Oslo", CountryCode = "NO" },
    };

    private static NewProperty Car(string name) => new()
    {
        Name = name,
        Description = "Car",
        Type = PropertyType.Vehicle,
        CurrencyCode = "NOK",
        AcquiredDate = Anchor.AddYears(-5),
        VehicleDetails = new VehicleDetailsDto { Kind = VehicleKind.Car, RegistrationNumber = "ab 123" },
    };

    private static NewPropertyEstimate Estimate(decimal value, DateTime effectiveFrom) =>
        new() { Value = value, EffectiveFrom = effectiveFrom };

    private static async Task<Guid> SeedTagAsync(OdysseyContext context, string name)
    {
        var tag = new TransactionTag { TransactionTagId = Guid.NewGuid(), Name = name };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    private static async Task<Guid> SeedWatchedPropertyAsync(OdysseyContext context, string name, Guid tagId)
    {
        var propertyId = (await new PropertyService(context).Create(House(name), userId: null)).PropertyId;
        await new PropertyEstimateService(context).Create(propertyId, Estimate(1m, Anchor));
        context.PropertySmartTags.Add(new PropertySmartTag { PropertyId = propertyId, TransactionTagId = tagId, AddedAt = Anchor });
        await context.SaveChangesAsync();
        return propertyId;
    }

    // Raw SQL against the pre-migration schema: the model already has the property tables, so going
    // through EF entities other than the two being counted would be sound, but raw SQL keeps this seed
    // independent of anything the migration under test creates.
    private static async Task SeedAccountEstimateAndSmartTagAsync(OdysseyContext context)
    {
        var accountId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountType`, `CurrencyCode`)
            VALUES ('{accountId}', 'House', '', '2026-01-01', 6, 'USD')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `AccountEstimates` (`AccountEstimateId`, `AccountId`, `Value`, `CurrencyCode`, `EffectiveFrom`, `CreatedAtUtc`)
            VALUES ('{Guid.NewGuid()}', '{accountId}', 1, 'USD', '2026-01-01', '2026-01-01')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `TransactionTags` (`TransactionTagId`, `Name`) VALUES ('{tagId}', 'Maintenance')
            """);
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `AccountSmartTags` (`AccountId`, `TransactionTagId`, `AddedAt`)
            VALUES ('{accountId}', '{tagId}', '2026-01-01')
            """);
    }

    private sealed record Snapshot(List<string> Tables, List<string> Columns, List<string> Indexes, int SettingsRows);

    private static async Task<Snapshot> SnapshotAsync(OdysseyContext context)
    {
        var tables = await context.Database.SqlQuery<string>($"""
            SELECT TABLE_NAME AS Value FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY BINARY TABLE_NAME
            """).ToListAsync();
        var columns = await context.Database.SqlQuery<string>($"""
            SELECT CONCAT_WS('.', TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, IFNULL(COLUMN_DEFAULT, '<null>')) AS Value
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY BINARY TABLE_NAME, ORDINAL_POSITION
            """).ToListAsync();
        var indexes = await context.Database.SqlQuery<string>($"""
            SELECT CONCAT_WS('.', TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE) AS Value
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY BINARY TABLE_NAME, BINARY INDEX_NAME, SEQ_IN_INDEX
            """).ToListAsync();
        var settingsRows = (int)await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `SystemSettings`");

        return new Snapshot(tables, columns, indexes, settingsRows);
    }

    private sealed record ForeignKeyRule(string TableName, string ColumnName, string ReferencedTable, string DeleteRule);

    private sealed class StubPropertyLimitsLookup : IPropertyLimitsLookup
    {
        public Task<PropertyLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PropertyLimits(SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty, IsDegraded: false));
    }

    private async Task MigrateAsync()
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
