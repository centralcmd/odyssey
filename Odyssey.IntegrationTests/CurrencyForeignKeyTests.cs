using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The currency foreign keys added by <c>AddCurrencyForeignKeys</c> (issue #241), pinned at the
/// database, plus the orphan repair the migration performs over a database that already lost a
/// currency out from under its rows.
/// </summary>
/// <remarks>
/// EF InMemory enforces no foreign keys, so on the fast tiers the refusal is
/// <c>CurrencyService.CountDeleteBlockers</c> alone. These tests are what show the backstop exists:
/// the delete is issued with raw SQL, so nothing in application code can be what refuses it.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class CurrencyForeignKeyTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_currency_fk";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddTaxStatementSettlementRange";

    private const string Subject = "_AddCurrencyForeignKeys";

    /// <summary>Every currency-code column — the full set, so an omission fails as loudly as a wrong
    /// rule. Kept in step with the clauses of <c>CurrencyService.CountDeleteBlockers</c>.</summary>
    private static readonly (string Table, string Column)[] CurrencyColumns =
    [
        ("Accounts", "CurrencyCode"),
        ("Transactions", "CurrencyCode"),
        ("Budgets", "BaseCurrencyCode"),
        ("TaxStatements", "BaseCurrencyCode"),
        ("Properties", "CurrencyCode"),
        ("AccountEstimates", "CurrencyCode"),
        ("PropertyEstimates", "CurrencyCode"),
        ("Terms", "CurrencyCode"),
        ("ExchangeRates", "FromCurrencyCode"),
        ("ExchangeRates", "ToCurrencyCode"),
    ];

    [SkippableFact]
    public async Task Every_currency_column_is_a_restrict_foreign_key_to_Currencies()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await context.Database.MigrateAsync();

            var rules = await ReadCurrencyForeignKeyRulesAsync(context);

            var wrong = CurrencyColumns
                .Select(column => (column, Rule: rules.GetValueOrDefault(column)))
                .Where(row => row.Rule != "RESTRICT")
                .Select(row => $"{row.column.Table}.{row.column.Column} => {row.Rule ?? "no foreign key"}")
                .ToList();

            Assert.True(wrong.Count == 0,
                "Every currency-code column must be a RESTRICT foreign key to Currencies. Wrong: " +
                string.Join(", ", wrong));

            // Unvetted extraction output is deliberately unkeyed; it is validated on import instead.
            Assert.DoesNotContain(rules.Keys, key => key.Table == "FileAnalysisCandidateTransactions");
            Assert.Equal(CurrencyColumns.Length, rules.Count);
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task A_raw_delete_of_a_referenced_currency_is_refused_by_the_database()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await context.Database.MigrateAsync();

            var accountId = Guid.NewGuid();
            await InsertAccountAsync(context, accountId, "NOK");

            var ex = await Assert.ThrowsAsync<MySqlException>(() =>
                context.Database.ExecuteSqlRawAsync("DELETE FROM `Currencies` WHERE `CurrencyCode` = 'NOK'"));

            Assert.Equal(MySqlErrorCode.RowIsReferenced2, ex.ErrorCode);
            Assert.True(await context.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == "NOK"));

            // An unreferenced currency still deletes.
            await context.Database.ExecuteSqlRawAsync("DELETE FROM `Currencies` WHERE `CurrencyCode` = 'ISK'");
            Assert.False(await context.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == "ISK"));
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The state issue #241 left production in: rows naming a currency that was deleted. The migration
    /// must restore the missing currencies as active rows — touching no referencing row — so the keys
    /// can be added and the stranded records become editable again.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_restores_orphaned_currencies_and_leaves_the_referencing_rows_untouched()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var orphanAccount = Guid.NewGuid();
        var healthyAccount = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                // Deleted out from under its rows — possible before this migration, and the bug itself.
                await InsertAccountAsync(context, orphanAccount, "NOK");
                await InsertAccountAsync(context, healthyAccount, "USD");
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Transactions`
                        (`TransactionId`, `AccountId`, `Description`, `Amount`, `TimeStamp`, `CurrencyCode`,
                         `Status`, `StatusChangedAt`)
                    VALUES ({transactionId}, {orphanAccount}, {"Deposit"}, {100m}, {DateTime.UtcNow}, {"NOK"},
                            {0}, {DateTime.UtcNow})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Budgets` (`BudgetId`, `Name`, `StartDate`, `EndDate`, `BaseCurrencyCode`)
                    VALUES ({Guid.NewGuid()}, {"Household"}, {new DateTime(2026, 1, 1)}, {new DateTime(2026, 12, 31)}, {"ZZZ"})
                    """);
                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM `Currencies` WHERE `CurrencyCode` = 'NOK'");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

                var nok = await context.Currencies.AsNoTracking().SingleAsync(c => c.CurrencyCode == "NOK");
                Assert.Equal("Restored currency NOK", nok.Name);
                Assert.Equal(2, nok.MinorUnits);
                Assert.Null(nok.Archived);

                var zzz = await context.Currencies.AsNoTracking().SingleAsync(c => c.CurrencyCode == "ZZZ");
                Assert.Equal("Restored currency ZZZ", zzz.Name);

                // A currency that was never missing is left exactly as it was.
                var usd = await context.Currencies.AsNoTracking().SingleAsync(c => c.CurrencyCode == "USD");
                Assert.Equal("US Dollar", usd.Name);

                Assert.Equal("NOK", await MigrationSeam.ScalarAsync(context,
                    $"SELECT `CurrencyCode` FROM `Accounts` WHERE `AccountId` = '{orphanAccount}'"));
                Assert.Equal("NOK", await MigrationSeam.ScalarAsync(context,
                    $"SELECT `CurrencyCode` FROM `Transactions` WHERE `TransactionId` = '{transactionId}'"));
                Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Budgets` WHERE `BaseCurrencyCode` = 'ZZZ'"));

                // And the key now holds.
                await Assert.ThrowsAsync<MySqlException>(() =>
                    context.Database.ExecuteSqlRawAsync("DELETE FROM `Currencies` WHERE `CurrencyCode` = 'NOK'"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>Raw SQL rather than the entity, so the insert works below head too.</summary>
    private static Task InsertAccountAsync(OdysseyContext context, Guid accountId, string currencyCode) =>
        context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountType`, `CurrencyCode`)
            VALUES ({accountId}, {"Account " + currencyCode}, {"Seeded"}, {DateTime.UtcNow}, {0}, {currencyCode})
            """);

    private static async Task<Dictionary<(string Table, string Column), string>> ReadCurrencyForeignKeyRulesAsync(
        OdysseyContext context)
    {
        var rows = await context.Database
            .SqlQuery<ForeignKeyRule>($"""
                SELECT k.TABLE_NAME AS TableName, k.COLUMN_NAME AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE()
                  AND k.REFERENCED_TABLE_NAME = 'Currencies'
                """)
            .ToListAsync();

        return rows.ToDictionary(row => (row.TableName, row.ColumnName), row => row.DeleteRule);
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

    private sealed record ForeignKeyRule(string TableName, string ColumnName, string DeleteRule);
}
