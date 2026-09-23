// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input — and the pre-migration rows carry
// kinds the service no longer accepts, which is what makes raw SQL the only way to build them.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>FoldRateTermKindsIntoFee</c>: the two rate kinds and any unrecognised kind become labelled fees.
/// A data migration, so only observable here — the EF InMemory provider runs no migrations at all.
/// </summary>
/// <remarks>
/// Every read is raw SQL at this migration's own schema rather than through the <c>Terms</c> DbSet,
/// so the test keeps compiling and running once the <c>TermKind</c> column is dropped by a later one.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class FoldRateTermKindsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_fold_rate_term_kinds";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_DropInsurancePoliciesAndSettings";

    private const string Subject = "_FoldRateTermKindsIntoFee";

    private const int Fee = 10;

    /// <summary>
    /// Every non-fee row ends at <c>Fee</c> carrying its old kind's name as its label, the value and
    /// unit untouched; a row that already had a label and an existing fee are both left alone.
    /// </summary>
    [SkippableFact]
    public async Task Rate_and_unknown_kinds_become_labelled_fees()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var interest = Guid.NewGuid();
        var expected = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var labelledRate = Guid.NewGuid();
        var fee = Guid.NewGuid();
        var contractRate = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedOwnersAsync(context, accountId, contractId);

                await InsertTermAsync(context, interest, accountId, null, kind: 1, unit: 0, value: 0.0325m, label: null);
                await InsertTermAsync(context, expected, accountId, null, kind: 2, unit: 0, value: 0.07m, label: null);
                await InsertTermAsync(context, unknown, accountId, null, kind: 0, unit: 1, value: 3m, label: null);
                // A rate row that somehow carries a label keeps it — the backfill only fills blanks.
                await InsertTermAsync(context, labelledRate, accountId, null, kind: 1, unit: 0, value: 0.01m, label: "Promo rate");
                await InsertTermAsync(context, fee, accountId, null, kind: Fee, unit: 1, value: 5m, label: "Account fee");
                await InsertTermAsync(context, contractRate, null, contractId, kind: 1, unit: 0, value: 0.05m, label: null);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

                var rows = await ReadAsync(context);

                Assert.All(rows.Values, row => Assert.Equal(Fee, row.Kind));

                Assert.Equal(("Interest rate", "interest rate"), (rows[interest].Label, rows[interest].LabelKey));
                Assert.Equal(("Expected return", "expected return"), (rows[expected].Label, rows[expected].LabelKey));
                Assert.Equal(("Unspecified", "unspecified"), (rows[unknown].Label, rows[unknown].LabelKey));
                Assert.Equal(("Promo rate", "promo rate"), (rows[labelledRate].Label, rows[labelledRate].LabelKey));
                Assert.Equal(("Account fee", "account fee"), (rows[fee].Label, rows[fee].LabelKey));

                // Both owners are covered: the migration is over the shared table.
                Assert.Equal("Interest rate", rows[contractRate].Label);

                // The number a rate stood for is unchanged — it is still a percentage fraction.
                Assert.Equal(0.0325m, rows[interest].Value);
                Assert.Equal(0, rows[interest].Unit);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// <c>Down()</c> restores a kind only from the exact label <c>Up()</c> wrote, clearing that label
    /// again; a row the migration did not name keeps its label and stays a fee.
    /// </summary>
    [SkippableFact]
    public async Task The_fold_reverts_the_labels_it_wrote()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var interest = Guid.NewGuid();
        var expected = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var fee = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedOwnersAsync(context, accountId, contractId);

                await InsertTermAsync(context, interest, accountId, null, kind: 1, unit: 0, value: 0.0325m, label: null);
                await InsertTermAsync(context, expected, accountId, null, kind: 2, unit: 0, value: 0.07m, label: null);
                await InsertTermAsync(context, unknown, accountId, null, kind: 0, unit: 1, value: 3m, label: null);
                await InsertTermAsync(context, fee, accountId, null, kind: Fee, unit: 1, value: 5m, label: "Account fee");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                var rows = await ReadAsync(context);

                Assert.Equal((1, (string?)null), (rows[interest].Kind, rows[interest].Label));
                Assert.Equal((2, (string?)null), (rows[expected].Kind, rows[expected].Label));
                Assert.Equal((0, (string?)null), (rows[unknown].Kind, rows[unknown].Label));
                Assert.Equal((Fee, "Account fee"), (rows[fee].Kind, rows[fee].Label));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private sealed record Row(int Kind, string? Label, string? LabelKey, int Unit, decimal Value);

    private static async Task<Dictionary<Guid, Row>> ReadAsync(OdysseyContext context)
    {
        var rows = new Dictionary<Guid, Row>();

        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT `TermId`, `TermKind`, `Label`, `LabelKey`, `ValueUnit`, `Value` FROM `Terms`";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows[reader.GetGuid(0)] = new Row(
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetDecimal(5));
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        return rows;
    }

    private static Task SeedOwnersAsync(OdysseyContext context, Guid accountId, Guid contractId) =>
        context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Pre-existing agreement', 0, '2024-01-01 00:00:00');
            INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `AccountType`, `CurrencyCode`, `Opened`)
            VALUES ('{accountId}', 'Pre-existing account', 'Seeded before the fold.', 1, 'USD', '2024-01-01 00:00:00');
            """);

    private static Task InsertTermAsync(
        OdysseyContext context, Guid termId, Guid? accountId, Guid? contractId,
        int kind, int unit, decimal value, string? label)
    {
        var account = accountId is { } a ? $"'{a}'" : "NULL";
        var contract = contractId is { } c ? $"'{c}'" : "NULL";
        var labelSql = label is null ? "NULL" : $"'{label}'";
        var keySql = label is null ? "NULL" : $"'{label.ToLowerInvariant()}'";
        var currency = unit == 1 ? "'USD'" : "NULL";

        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Terms`
                (`TermId`, `AccountId`, `ContractId`, `TermKind`, `Label`, `LabelKey`, `ValueUnit`,
                 `Value`, `CurrencyCode`, `EffectiveFrom`, `CreatedAtUtc`)
            VALUES
                ('{termId}', {account}, {contract}, {kind}, {labelSql}, {keySql}, {unit},
                 {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {currency},
                 '2024-01-01 00:00:00', '2024-01-01 00:00:00');
            """);
    }

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

#pragma warning restore EF1002
