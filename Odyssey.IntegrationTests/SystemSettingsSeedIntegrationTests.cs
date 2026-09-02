using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the <c>SystemSettings</c> seed on a fresh database.
///
/// <para>
/// The migration history was squashed to a single <c>InitialCreate</c> per context, so there is no
/// longer a step-wise upgrade to verify — every install is a fresh one. What still matters, and what
/// this asserts, is that the seed the migration emits agrees with the compiled catalogue: the row set
/// must be exactly <see cref="SystemSettingsKeys.AllKeys"/>, and each value must equal the
/// <see cref="SystemSettingsDefaults"/> constant rather than a literal restated here, so a seed that
/// drifts from the compiled default fails here instead of in production.
/// </para>
///
/// <para>
/// <c>UpdatedBy</c> is asserted null across the board. That null is load-bearing: it marks a row as
/// never having been owned by an administrator, which is how the "last changed by" line renders.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class SystemSettingsSeedIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_system_settings_seed";

    /// <summary>
    /// Spot-checks that carry a behavioural contract beyond "a row exists": the three Subscriptions
    /// limits replace <c>private const</c>s on <c>SubscriptionService</c>, so a drifting seed silently
    /// changes what every deployment's Subscriptions page shows.
    ///
    /// <para>
    /// The four mail transport rows are here for a sharper reason (issue #8, AC 17). Two of them seed
    /// the EMPTY STRING, and that empty value is the whole contract: there is no configuration to adopt
    /// from and no environment fallback, so an empty host is not a placeholder awaiting a carry-over
    /// step — it IS the shipped state, and it means a fresh deployment sends no mail until an
    /// administrator configures a relay. A seed that quietly grew a default host would reverse that
    /// without anything else noticing.
    /// </para>
    /// </summary>
    private static readonly (string Key, string Value)[] ExpectedRows =
    [
        (SystemSettingsKeys.EmailSmtpHost, SystemSettingsDefaults.EmailSmtpHost),
        (SystemSettingsKeys.EmailSmtpPort, $"{SystemSettingsDefaults.EmailSmtpPort}"),
        (SystemSettingsKeys.EmailUseStartTls, SystemSettingsDefaults.EmailUseStartTls ? "true" : "false"),
        (SystemSettingsKeys.EmailClientBaseUrl, SystemSettingsDefaults.EmailClientBaseUrl),
        (SystemSettingsKeys.SubscriptionRenewalWindowDays,
            $"{SystemSettingsDefaults.SubscriptionRenewalWindowDays}"),
        (SystemSettingsKeys.SubscriptionMaxSummaryRenewals,
            $"{SystemSettingsDefaults.SubscriptionMaxSummaryRenewals}"),
        (SystemSettingsKeys.SubscriptionMaxSummarySubscriptions,
            $"{SystemSettingsDefaults.SubscriptionMaxSummarySubscriptions}"),
    ];

    [SkippableFact]
    public async Task A_fresh_database_carries_every_known_key()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateDatabaseAsync();

        await using (var context = new OdysseyContext(ApplicationOptions()))
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = new OdysseyContext(ApplicationOptions()))
        {
            var rows = await context.SystemSettings.AsNoTracking().ToDictionaryAsync(row => row.Key, row => row);

            // 62 before issue #8, +4 for the mail transport and the public link origin, +1 for the
            // insurance link cap (issue #27).
            Assert.Equal(67, rows.Count);
            Assert.Equal(SystemSettingsKeys.AllKeys.OrderBy(key => key), rows.Keys.OrderBy(key => key));

            foreach (var (key, value) in ExpectedRows)
            {
                Assert.Equal(value, rows[key].Value);
            }

            Assert.All(rows.Values, row => Assert.Null(row.UpdatedBy));
        }

        await DropDatabaseAsync();
    }

    private async Task RecreateDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropDatabaseAsync()
    {
        await using var admin = AdminContext();

        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext AdminContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(
                fixture.OdysseyConnectionString,
                ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private DbContextOptions<OdysseyContext> ApplicationOptions()
    {
        var connection = fixture.ConnectionStringFor(Database);
        return new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection)).Options;
    }
}
