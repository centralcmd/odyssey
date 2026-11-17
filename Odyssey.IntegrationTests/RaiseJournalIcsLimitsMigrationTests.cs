// EF1002 flags interpolation into raw SQL. Every interpolated value below is a constant this test wrote.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>RaiseJournalIcsLimits</c> against real MariaDB. It moves only a row no administrator has saved
/// (<c>UpdatedBy IS NULL</c>) and still holding the old default, and <c>Down()</c> moves only rows still at the new
/// default — so an administrator's own value survives both directions. The EF InMemory provider runs no migration
/// SQL, so none of this is observable on the fast tiers.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class RaiseJournalIcsLimitsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_raise_journal_ics_limits";
    private const string Subject = "_RaiseJournalIcsLimits";
    private const string Baseline = "_RaiseTaskIcsLimits";

    [SkippableFact]
    public async Task Up_moves_only_untouched_defaults_and_Down_reverses_only_what_it_moved()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        try
        {
            await using var context = Context();
            await MigrationSeam.MigrateToAsync(context, Baseline);

            // Untouched defaults: export size and both counts. An administrator saved the import size at 64,
            // the default value itself — "deliberately set back to the default" must not be moved.
            await SetAsync(context, SystemSettingsKeys.JournalIcsMaxImportMegabytes, "64", updatedBy: "admin-id");
            // A hand-edited, unsaved row off the old default is left alone too.
            await SetAsync(context, SystemSettingsKeys.JournalIcsMaxImportEntries, "500", updatedBy: null);

            await MigrationSeam.MigrateToAsync(context, Subject);

            Assert.Equal("10240", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportMegabytes));
            Assert.Equal("unlimited", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportRows));
            Assert.Equal("64", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportMegabytes));
            Assert.Equal("500", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportEntries));

            // An administrator then keeps the raised export size but saves it; Down() must not take it back.
            await SetAsync(context, SystemSettingsKeys.JournalIcsMaxExportMegabytes, "10240", updatedBy: "admin-id");

            await MigrationSeam.MigrateToAsync(context, Baseline);

            Assert.Equal("10240", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportMegabytes));
            Assert.Equal("2000", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportRows));
            Assert.Equal("64", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportMegabytes));
            Assert.Equal("500", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportEntries));
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task Up_and_Down_move_all_four_untouched_defaults()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        try
        {
            await using var context = Context();
            await MigrationSeam.MigrateToAsync(context, Baseline);

            await MigrationSeam.MigrateToAsync(context, Subject);

            Assert.Equal("10240", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportMegabytes));
            Assert.Equal("10240", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportMegabytes));
            Assert.Equal("unlimited", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportEntries));
            Assert.Equal("unlimited", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportRows));

            await MigrationSeam.MigrateToAsync(context, Baseline);

            Assert.Equal("64", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportMegabytes));
            Assert.Equal("64", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportMegabytes));
            Assert.Equal("2000", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxImportEntries));
            Assert.Equal("2000", await ValueAsync(context, SystemSettingsKeys.JournalIcsMaxExportRows));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static Task SetAsync(OdysseyContext context, string key, string value, string? updatedBy) =>
        context.Database.ExecuteSqlRawAsync(
            "UPDATE `SystemSettings` SET `Value` = {0}, `UpdatedBy` = {1} WHERE `Key` = {2}", [value, updatedBy!, key]);

    private static async Task<string> ValueAsync(OdysseyContext context, string key) =>
        await context.Database
            .SqlQueryRaw<string>("SELECT `Value` AS `Value` FROM `SystemSettings` WHERE `Key` = {0}", key)
            .SingleAsync();

    private OdysseyContext Context() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
