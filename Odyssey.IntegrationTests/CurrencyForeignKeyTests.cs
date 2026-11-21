using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The currency foreign keys added by <c>AddCurrencyForeignKeys</c> (issue #241), pinned at the
/// database.
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
        ("Instruments", "CurrencyCode"),
    ];

    [SkippableFact]
    public async Task Every_currency_column_is_a_restrict_foreign_key_to_Currencies()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = NewContext();

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

            // The one deliberate CASCADE (issue #349 §4): a currency's sync configuration means nothing
            // once the currency is gone, and the currency cannot be deleted while any rate names it.
            Assert.Equal("CASCADE", rules.GetValueOrDefault(("ExchangeRateSyncCurrencies", "CurrencyCode")));
            Assert.Equal(CurrencyColumns.Length + 1, rules.Count);
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
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = NewContext();

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

    /// <summary>Raw SQL rather than the entity, so nothing in application code stands between the row and the key.</summary>
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

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }


    private sealed record ForeignKeyRule(string TableName, string ColumnName, string DeleteRule);
}
