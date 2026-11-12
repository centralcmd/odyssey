using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Release 2 of issue #472: <c>StoreFetchedRatesInPublishedDirectionOnly</c> deletes every fetched <c>NOK → X</c>
/// row that has its fetched <c>X → NOK</c> partner, and its <c>Down()</c> re-derives them (spec AC 34–37). A data
/// migration's raw SQL runs only on the real engine, so this tier is the only one that can exercise it.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class StoreFetchedRatesMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_store_fetched_rates";
    private const string Migration = "StoreFetchedRatesInPublishedDirectionOnly";

    private static readonly DateTime Day = new(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);

    [SkippableFact] // AC 34, 35, 36
    public async Task Up_deletes_partnered_fetched_reverse_rows_only_and_Down_re_derives_them()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            var manualReverse = Guid.NewGuid();
            var partnerless = Guid.NewGuid();
            // Fetched reverse rows whose only "partner" fails one join clause each: a MANUAL X → NOK on the same
            // AsOf (p.Provider = r.Provider) and a fetched X → NOK one day later (p.AsOf = r.AsOf). Readers invert
            // only fetched rows on their own date, so deleting either would leave that pair with no rate.
            var manualPartner = Guid.NewGuid();
            var laterPartner = Guid.NewGuid();
            var partnerCreated = new Dictionary<(string Code, DateTime AsOf), DateTime>();

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync(PreviousMigration(context));

                // 2 currencies × 3 days × both directions, as the release-1 sync wrote them: one save per
                // observation, so both rows of a day share CreatedAt.
                foreach (var (code, rate) in new[] { ("USD", 9.3601m), ("EUR", 10.488m) })
                {
                    for (var day = 0; day < 3; day++)
                    {
                        var asOf = Day.AddDays(day);
                        var created = asOf.AddMinutes(5 + day);
                        partnerCreated[(code, asOf)] = created;
                        context.ExchangeRates.AddRange(
                            Fetched(code, "NOK", rate + day, asOf, created),
                            Fetched("NOK", code, Math.Round(1m / (rate + day), 8), asOf, created));
                    }
                }

                // A manual NOK → USD on the same AsOf as a fetched partner, and a fetched NOK → SEK with no partner.
                context.ExchangeRates.AddRange(
                    new ExchangeRate { ExchangeRateId = manualReverse, FromCurrencyCode = "NOK", ToCurrencyCode = "USD", Rate = 0.2m, AsOf = Day, CreatedAt = Day },
                    new ExchangeRate { ExchangeRateId = partnerless, FromCurrencyCode = "NOK", ToCurrencyCode = "SEK", Rate = 1.05m, AsOf = Day, CreatedAt = Day, Provider = ExchangeRateProvider.NorgesBank },
                    new ExchangeRate { ExchangeRateId = manualPartner, FromCurrencyCode = "NOK", ToCurrencyCode = "GBP", Rate = 0.075m, AsOf = Day, CreatedAt = Day, Provider = ExchangeRateProvider.NorgesBank },
                    new ExchangeRate { FromCurrencyCode = "GBP", ToCurrencyCode = "NOK", Rate = 13.3m, AsOf = Day, CreatedAt = Day },
                    new ExchangeRate { ExchangeRateId = laterPartner, FromCurrencyCode = "NOK", ToCurrencyCode = "DKK", Rate = 0.64m, AsOf = Day, CreatedAt = Day, Provider = ExchangeRateProvider.NorgesBank },
                    Fetched("DKK", "NOK", 1.56m, Day.AddDays(1), Day.AddDays(1)));
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());

                var rows = await context.ExchangeRates.AsNoTracking().ToListAsync();
                var kept = new[] { partnerless, manualPartner, laterPartner };
                var fetched = rows.Where(r => r.Provider != null && !kept.Contains(r.ExchangeRateId)).ToList();
                Assert.Equal(7, fetched.Count); // the 6 USD/EUR partners plus the later DKK → NOK
                Assert.All(fetched, r => Assert.Equal("NOK", r.ToCurrencyCode));
                Assert.Contains(rows, r => r.ExchangeRateId == manualReverse);
                Assert.All(kept, id => Assert.Contains(rows, r => r.ExchangeRateId == id));
                Assert.Equal(12, rows.Count);
            }

            await using (var context = NewContext())
            {
                var thisMigration = context.Database.GetMigrations().Single(id => id.EndsWith("_" + Migration, StringComparison.Ordinal));
                var all = context.Database.GetMigrations().ToList();
                await context.Database.MigrateAsync(all[all.IndexOf(thisMigration) - 1]);

                var rows = await context.ExchangeRates.AsNoTracking().ToListAsync();
                var restored = rows.Where(r => r.Provider != null && r.FromCurrencyCode == "NOK" && r.ToCurrencyCode is "USD" or "EUR").ToList();
                Assert.Equal(6, restored.Count);
                foreach (var row in restored)
                {
                    var partner = rows.Single(r => r.Provider != null && r.FromCurrencyCode == row.ToCurrencyCode && r.ToCurrencyCode == "NOK" && r.AsOf == row.AsOf);
                    Assert.Equal(Math.Round(1m / partner.Rate, 8), row.Rate);
                    Assert.Equal(partner.CreatedAt, row.CreatedAt);
                    Assert.Equal(partnerCreated[(row.ToCurrencyCode, row.AsOf)], row.CreatedAt);
                }

                // The partnerless row is not duplicated, and the manual row is untouched.
                Assert.Single(rows, r => r.FromCurrencyCode == "NOK" && r.ToCurrencyCode == "SEK");
                Assert.Equal(0.2m, rows.Single(r => r.ExchangeRateId == manualReverse).Rate);
                // NOT EXISTS: GBP's kept reverse row is not doubled (its partner is manual, so Down skips it), and the
                // later DKK partner re-derives a NOK → DKK on ITS date beside the kept one, never on top of it.
                Assert.Single(rows, r => r.FromCurrencyCode == "NOK" && r.ToCurrencyCode == "GBP");
                Assert.Equal(
                    [Day, Day.AddDays(1)],
                    rows.Where(r => r.FromCurrencyCode == "NOK" && r.ToCurrencyCode == "DKK").Select(r => r.AsOf).Order());
                Assert.Equal(19, rows.Count);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact] // AC 34, the second run
    public async Task Up_is_a_no_op_on_a_database_that_holds_only_the_published_direction()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync(PreviousMigration(context));
                context.ExchangeRates.Add(Fetched("USD", "NOK", 10m, Day, Day));
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
                var row = Assert.Single(await context.ExchangeRates.AsNoTracking().ToListAsync());
                Assert.Equal(("USD", "NOK", 10m), (row.FromCurrencyCode, row.ToCurrencyCode, row.Rate));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private static ExchangeRate Fetched(string from, string to, decimal rate, DateTime asOf, DateTime created) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = created,
        Provider = ExchangeRateProvider.NorgesBank,
    };

    private static string PreviousMigration(OdysseyContext context)
    {
        var all = context.Database.GetMigrations().ToList();
        var index = all.FindIndex(id => id.EndsWith("_" + Migration, StringComparison.Ordinal));
        Assert.True(index > 0, "The release-2 migration must exist and not be the first.");
        return all[index - 1];
    }

    private OdysseyContext NewContext()
    {
        var connection = fixture.ConnectionStringFor(Database);
        return new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection)).Options);
    }

    private async Task RecreateAsync()
    {
        await using var admin = AdminContext();
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var admin = AdminContext();
        await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext AdminContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
