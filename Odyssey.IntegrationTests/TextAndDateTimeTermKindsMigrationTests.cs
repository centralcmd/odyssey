// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the rows are
// deliberately written outside the service, which is the whole point: the CHECK constraint is what
// stands behind the service against a hand edit or a restore.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #192 (AC 15): <c>AddTextAndDateTimeTermKinds</c> applies over
/// existing Percentage and Amount rows with no backfill, and <c>CK_Terms_ValueMatchesUnit</c> rejects
/// every row whose value columns do not match its unit — including an unknown ordinal. Not observable
/// on the fast tiers: the EF InMemory provider runs no migrations and enforces no CHECK constraint.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class TextAndDateTimeTermKindsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_term_text_datetime";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_MoveAccountTermsToContracts";

    private const string Subject = "_AddTextAndDateTimeTermKinds";

    [SkippableFact]
    public async Task The_migration_applies_over_existing_numeric_rows_and_the_constraint_guards_the_shape()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();
        var percentageId = Guid.NewGuid();
        var amountId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedContractAsync(context, contractId);
                await InsertBaselineTermAsync(context, percentageId, contractId, unit: 0, value: "0.032500", currency: "NULL");
                await InsertBaselineTermAsync(context, amountId, contractId, unit: 1, value: "1850.000000", currency: "'USD'");
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));

                var existing = await context.Terms.AsNoTracking()
                    .Where(t => t.TermId == percentageId || t.TermId == amountId)
                    .ToListAsync();
                Assert.Equal(2, existing.Count);
                Assert.All(existing, t =>
                {
                    Assert.NotNull(t.Value);
                    Assert.Null(t.TextValue);
                    Assert.Null(t.DateTimeValue);
                });

                // Valid rows of both new kinds are accepted.
                await InsertAsync(context, contractId, unit: 2, value: "NULL", text: "'3 months'", instant: "NULL");
                await InsertAsync(context, contractId, unit: 3, value: "NULL", text: "NULL", instant: $"'{Timestamp()}'");

                // A Text row carrying a number, a Text row with no text, a DateTime row with no
                // instant, a numeric row with no number, a numeric row carrying text, and an unknown
                // ordinal: each is refused by the constraint.
                await AssertRejectedAsync(context, contractId, unit: 2, value: "1.000000", text: "'3 months'", instant: "NULL");
                await AssertRejectedAsync(context, contractId, unit: 2, value: "NULL", text: "NULL", instant: "NULL");
                await AssertRejectedAsync(context, contractId, unit: 3, value: "NULL", text: "NULL", instant: "NULL");
                await AssertRejectedAsync(context, contractId, unit: 1, value: "NULL", text: "NULL", instant: "NULL");
                await AssertRejectedAsync(context, contractId, unit: 1, value: "1.000000", text: "'x'", instant: "NULL");
                await AssertRejectedAsync(context, contractId, unit: 4, value: "NULL", text: "'x'", instant: "NULL");
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// A stored instant reads back as UTC — the provider materialises a <c>datetime(6)</c> as
    /// Unspecified, and the value converter is what keeps the wire's <c>Z</c> on it.
    /// </summary>
    [SkippableFact]
    public async Task A_date_time_value_round_trips_as_utc()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();
        var termId = Guid.NewGuid();
        var instant = new DateTime(2027, 3, 31, 10, 0, 0, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                await SeedContractAsync(context, contractId);
                context.Terms.Add(new Term
                {
                    TermId = termId,
                    ContractId = contractId,
                    Label = "Break deadline",
                    LabelKey = "break deadline",
                    ValueUnit = TermValueUnit.DateTime,
                    DateTimeValue = instant,
                    EffectiveFrom = DateTime.UtcNow.Date,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                var stored = (await context.Terms.AsNoTracking().SingleAsync(t => t.TermId == termId)).DateTimeValue;
                Assert.Equal(instant, stored);
                Assert.Equal(DateTimeKind.Utc, stored!.Value.Kind);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// <c>Down</c> refuses while Text or DateTime rows exist rather than dropping their values, and
    /// succeeds once they are gone.
    /// </summary>
    [SkippableFact]
    public async Task Reverting_is_refused_while_a_fact_term_exists()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();

        try
        {
            await using var context = NewContext();
            await context.Database.MigrateAsync();
            await SeedContractAsync(context, contractId);
            await InsertAsync(context, contractId, unit: 2, value: "NULL", text: "'3 months'", instant: "NULL");

            await Assert.ThrowsAnyAsync<Exception>(() => MigrationSeam.MigrateToAsync(context, Baseline));
            Assert.True(await MigrationSeam.HasRunAsync(context, Subject));

            await context.Database.ExecuteSqlRawAsync("DELETE FROM `Terms` WHERE `ValueUnit` NOT IN (0, 1)");
            await MigrationSeam.MigrateToAsync(context, Baseline);
            Assert.False(await MigrationSeam.HasRunAsync(context, Subject));
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Seeding ────────────────────────────────────────────────────────────────

    private static Task SeedContractAsync(OdysseyContext context, Guid contractId) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Lease', 2, '{Timestamp()}');
            """);

    /// <summary>A row in the pre-migration shape: no text or date-time column exists yet.</summary>
    private static Task InsertBaselineTermAsync(
        OdysseyContext context, Guid termId, Guid contractId, int unit, string value, string currency)
    {
        var now = Timestamp();
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Terms`
                (`TermId`, `ContractId`, `Label`, `LabelKey`, `ValueUnit`, `Value`, `CurrencyCode`,
                 `EffectiveFrom`, `CreatedAtUtc`)
            VALUES
                ('{termId}', '{contractId}', 'Term {unit}', 'term {termId}', {unit}, {value}, {currency},
                 '{now}', '{now}');
            """);
    }

    private static Task InsertAsync(OdysseyContext context, Guid contractId, int unit, string value, string text, string instant)
    {
        var now = Timestamp();
        var termId = Guid.NewGuid();
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Terms`
                (`TermId`, `ContractId`, `Label`, `LabelKey`, `ValueUnit`, `Value`, `TextValue`,
                 `DateTimeValue`, `EffectiveFrom`, `CreatedAtUtc`)
            VALUES
                ('{termId}', '{contractId}', 'Fact', 'fact {termId}', {unit}, {value}, {text},
                 {instant}, '{now}', '{now}');
            """);
    }

    private static async Task AssertRejectedAsync(
        OdysseyContext context, Guid contractId, int unit, string value, string text, string instant)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(context, contractId, unit, value, text, instant));
        Assert.Contains("CK_Terms_ValueMatchesUnit", ex.ToString(), StringComparison.Ordinal);
    }

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private OdysseyContext NewContext() =>
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
