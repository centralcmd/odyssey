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
/// The relational half of issue #192 (AC 15): <c>CK_Terms_ValueMatchesUnit</c> rejects every row whose
/// value columns do not match its unit — including an unknown ordinal — and a date-time value
/// round-trips as UTC. Not observable on the fast tiers: the EF InMemory provider enforces no CHECK
/// constraint.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class TextAndDateTimeTermKindsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_term_text_datetime";

    /// <summary>
    /// <c>CK_Terms_ValueMatchesUnit</c> accepts a valid row of each fact kind and refuses every row whose
    /// value columns do not match its unit, an unknown ordinal included.
    /// </summary>
    [SkippableFact]
    public async Task The_constraint_guards_the_value_shape_of_every_unit()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var contractId = Guid.NewGuid();

        try
        {
            await using var context = NewContext();
            await SeedContractAsync(context, contractId);

            // Valid rows of both fact kinds are accepted.
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
        await fixture.CreateMigratedDatabaseAsync(Database);

        var contractId = Guid.NewGuid();
        var termId = Guid.NewGuid();
        var instant = new DateTime(2027, 3, 31, 10, 0, 0, DateTimeKind.Utc);

        try
        {
            await using (var context = NewContext())
            {
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

    // ── Seeding ────────────────────────────────────────────────────────────────

    private static Task SeedContractAsync(OdysseyContext context, Guid contractId) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Lease', 2, '{Timestamp()}');
            """);

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

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
