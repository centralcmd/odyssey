// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a constant or a Guid
// this test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #446: the migration both ways, the unique indexes (one row per instrument, date
/// and source; one priority per instrument; the widened <c>ExclusiveGuard</c>), the CHECKs, and the foreign keys'
/// on-delete behaviour. None of it is observable on EF InMemory.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class InstrumentPricesRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_instrument_prices";
    private const string PreviousMigration = "_AddPhotoOriginalBackfillJobRuns";
    private const string Migration = "_AddInstrumentPrices";

    private static readonly DateTime Now = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 11, 6);

    [SkippableFact]
    public async Task The_migration_reverts_and_reapplies_cleanly()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var instrument = await AddInstrumentAsync(context);
        await AddPriceAsync(context, instrument, Day, null);
        await AddRunAsync(context, JobRunTrigger.Scheduled);

        // Down refuses nothing about data it owns; the run row of the new type has to go first, as its CHECK does.
        await context.Database.ExecuteSqlRawAsync("DELETE FROM `JobRuns` WHERE `JobType` = 8");
        await MigrationSeam.MigrateToAsync(context, PreviousMigration);

        foreach (var table in new[] { "Instruments", "InstrumentPrices", "InstrumentPriceSourceBindings", "InstrumentPriceSourceStates" })
        {
            Assert.False(await MigrationSeam.TableExistsAsync(context, table), table);
        }

        Assert.Equal(0L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` LIKE 'InstrumentPrice%'"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context,
            $"SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = 'JobRuns' AND INDEX_NAME = 'IX_JobRuns_ExclusiveGuard' AND NON_UNIQUE = 0"));

        await context.Database.MigrateAsync();
        Assert.True(await MigrationSeam.HasRunAsync(context, Migration));
        Assert.Equal(3L, await MigrationSeam.CountAsync(context,
            "SELECT COUNT(*) FROM `SystemSettings` WHERE `Key` LIKE 'InstrumentPrice%'"));
    }

    [SkippableFact]
    public async Task One_running_instrument_sync_holds_the_exclusive_guard_and_concurrent_starts_race_cleanly()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            await using var context = NewContext();
            try
            {
                await AddRunAsync(context, i % 2 == 0 ? JobRunTrigger.Manual : JobRunTrigger.Scheduled);
                return 0;
            }
            catch (DbUpdateException exception) when ((exception.GetBaseException() as MySqlException)?.Number == 1062)
            {
                return 1062;
            }
        }));

        Assert.Equal(1, attempts.Count(code => code == 0));
        Assert.Equal(3, attempts.Count(code => code == 1062));

        // The guard is per job type: an exchange-rate run may still start.
        await using var other = NewContext();
        other.JobRuns.Add(new ExchangeRateSyncJobRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, Status = JobRunStatus.Running,
            StartedAtUtc = Now, LastActivityAtUtc = Now,
        });
        await other.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task A_date_holds_one_manual_row_and_one_row_per_source()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var instrument = await AddInstrumentAsync(context);

        await AddPriceAsync(context, instrument, Day, null);
        await AddPriceAsync(context, instrument, Day, "fake-primary");
        await AddPriceAsync(context, instrument, Day, "fake-secondary");

        await AssertDuplicateAsync(() => AddPriceAsync(NewContext(), instrument, Day, null));
        await AssertDuplicateAsync(() => AddPriceAsync(NewContext(), instrument, Day, "fake-primary"));
    }

    [SkippableFact]
    public async Task An_instrument_holds_one_binding_per_priority()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var instrument = await AddInstrumentAsync(context);
        await AddBindingAsync(context, instrument, "fake-primary", 1);

        await AssertDuplicateAsync(() => AddBindingAsync(NewContext(), instrument, "fake-secondary", 1));
    }

    [SkippableTheory]
    [InlineData("UPDATE `InstrumentPrices` SET `Close` = 0")]
    [InlineData("UPDATE `InstrumentPrices` SET `SourceKey` = 'Bad Key'")]
    [InlineData("UPDATE `InstrumentPrices` SET `SourceKey` = CONCAT('fake-primary', CHAR(10))")]
    [InlineData("UPDATE `InstrumentPriceSourceBindings` SET `SourceKey` = CONCAT('fake-primary', CHAR(10))")]
    [InlineData("UPDATE `Instruments` SET `Type` = 4")]
    [InlineData("UPDATE `InstrumentPriceSourceBindings` SET `Priority` = 0")]
    [InlineData("UPDATE `InstrumentPriceSourceBindings` SET `Priority` = 10")]
    [InlineData("UPDATE `InstrumentPriceSourceStates` SET `RequestCount` = -1")]
    public async Task The_checks_refuse_an_out_of_contract_value(string sql)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var instrument = await AddInstrumentAsync(context);
        await AddPriceAsync(context, instrument, Day, "fake-primary");
        await AddBindingAsync(context, instrument, "fake-primary", 1);
        context.InstrumentPriceSourceStates.Add(new InstrumentPriceSourceState { SourceKey = "fake-primary", UpdatedAtUtc = Now });
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(4025, exception.Number);
    }

    [SkippableFact]
    public async Task The_price_column_keeps_eight_decimals_and_eighteen_digits()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var instrument = await AddInstrumentAsync(context);
        context.InstrumentPrices.Add(new InstrumentPrice
        {
            InstrumentPriceId = Guid.NewGuid(), InstrumentId = instrument, TradingDate = Day, Close = 9999999999.99999999m, CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync();

        await using var reader = NewContext();
        Assert.Equal(9999999999.99999999m, (await reader.InstrumentPrices.SingleAsync()).Close);
    }

    [SkippableFact]
    public async Task Prices_restrict_an_instrument_delete_while_bindings_cascade_with_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var priced = await AddInstrumentAsync(context, "EQNR");
        await AddPriceAsync(context, priced, Day, null);
        var bound = await AddInstrumentAsync(context, "DNB");
        await AddBindingAsync(context, bound, "fake-primary", 1);

        var restricted = await Assert.ThrowsAsync<MySqlException>(() =>
            context.Database.ExecuteSqlRawAsync($"DELETE FROM `Instruments` WHERE `InstrumentId` = '{priced}'"));
        Assert.Equal(1451, restricted.Number);

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Instruments` WHERE `InstrumentId` = '{bound}'");
        Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `InstrumentPriceSourceBindings`"));
    }

    [SkippableFact]
    public async Task A_currency_in_use_by_an_instrument_cannot_be_deleted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        await AddInstrumentAsync(context, currency: "SEK");

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            context.Database.ExecuteSqlRawAsync("DELETE FROM `Currencies` WHERE `CurrencyCode` = 'SEK'"));
        Assert.Equal(1451, exception.Number);
    }

    [SkippableFact]
    public async Task Deleting_a_user_nulls_the_attribution_and_keeps_the_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        await using var context = NewContext();
        var userId = Guid.NewGuid().ToString();
        await InsertUserAsync(context, userId);
        var instrument = await AddInstrumentAsync(context, createdBy: userId);
        context.InstrumentPrices.Add(new InstrumentPrice
        {
            InstrumentPriceId = Guid.NewGuid(), InstrumentId = instrument, TradingDate = Day, Close = 1m, CreatedAtUtc = Now, CreatedByUserId = userId,
        });
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync($"DELETE FROM `AspNetUsers` WHERE `Id` = '{userId}'");

        Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `Instruments` WHERE `CreatedByUserId` IS NULL"));
        Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `InstrumentPrices` WHERE `CreatedByUserId` IS NULL"));
    }

    private static async Task AssertDuplicateAsync(Func<Task> write)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(write);
        Assert.Equal(1062, (exception.GetBaseException() as MySqlException)?.Number);
    }

    private static async Task<Guid> AddInstrumentAsync(OdysseyContext context, string symbol = "EQNR", string currency = "NOK", string? createdBy = null)
    {
        var id = Guid.NewGuid();
        context.Instruments.Add(new Instrument
        {
            InstrumentId = id,
            Symbol = symbol,
            MicCode = "XOSL",
            Name = symbol,
            Type = InstrumentType.Stock,
            CurrencyCode = currency,
            ListingKey = $"{symbol}@XOSL",
            CreatedAtUtc = Now,
            CreatedByUserId = createdBy,
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task AddPriceAsync(OdysseyContext context, Guid instrument, DateOnly date, string? source)
    {
        context.InstrumentPrices.Add(new InstrumentPrice
        {
            InstrumentPriceId = Guid.NewGuid(), InstrumentId = instrument, TradingDate = date, Close = 10m, SourceKey = source, CreatedAtUtc = Now,
        });
        await context.SaveChangesAsync();
    }

    private static async Task AddBindingAsync(OdysseyContext context, Guid instrument, string source, int priority)
    {
        context.InstrumentPriceSourceBindings.Add(new InstrumentPriceSourceBinding
        {
            InstrumentId = instrument, SourceKey = source, SourceSymbol = "EQNR.OL", Priority = priority, Enabled = true, UpdatedAtUtc = Now,
        });
        await context.SaveChangesAsync();
    }

    private static async Task AddRunAsync(OdysseyContext context, JobRunTrigger trigger)
    {
        context.JobRuns.Add(new InstrumentPriceSyncJobRun
        {
            JobRunId = Guid.NewGuid(), Trigger = trigger, Status = JobRunStatus.Running, StartedAtUtc = Now, LastActivityAtUtc = Now,
        });
        await context.SaveChangesAsync();
    }

    private static Task InsertUserAsync(OdysseyContext context, string id) =>
        context.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO `AspNetUsers`
                 (`Id`, `UserName`, `NormalizedUserName`, `Email`, `NormalizedEmail`, `EmailConfirmed`,
                  `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `PhoneNumberConfirmed`,
                  `TwoFactorEnabled`, `LockoutEnabled`, `AccessFailedCount`, `MustChangePassword`)
             VALUES
                 ({id}, 'prices@instruments.test', 'PRICES@INSTRUMENTS.TEST', 'prices@instruments.test', 'PRICES@INSTRUMENTS.TEST', 1,
                  'hash', 'stamp', 'concurrency', 0, 0, 1, 0, 0)
             """);

    private async Task MigratedAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
    }

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);

}
