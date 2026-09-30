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
    /// must restore every missing code — from each of the eight source columns, under a distinct code
    /// per column so a column dropped from the repair's UNION fails here by name — as an active row
    /// flagged for review, touching no referencing row, so the keys can be added and the stranded
    /// records become editable again.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_restores_orphaned_currencies_from_every_column_and_leaves_the_rows_untouched()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var orphanAccount = Guid.NewGuid();
        var deletedCurrencyAccount = Guid.NewGuid();
        var healthyAccount = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                await InsertAccountAsync(context, orphanAccount, "QAA");
                await InsertAccountAsync(context, healthyAccount, "USD");

                // A real currency deleted out from under its rows — the bug itself.
                await InsertAccountAsync(context, deletedCurrencyAccount, "NOK");
                await context.Database.ExecuteSqlRawAsync("DELETE FROM `Currencies` WHERE `CurrencyCode` = 'NOK'");

                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Transactions`
                        (`TransactionId`, `AccountId`, `Description`, `Amount`, `TimeStamp`, `CurrencyCode`,
                         `Status`, `StatusChangedAt`)
                    VALUES ({transactionId}, {orphanAccount}, {"Deposit"}, {100m}, {now}, {"QTX"}, {0}, {now})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Budgets` (`BudgetId`, `Name`, `StartDate`, `EndDate`, `BaseCurrencyCode`)
                    VALUES ({Guid.NewGuid()}, {"Household"}, {new DateTime(2026, 1, 1)}, {new DateTime(2026, 12, 31)}, {"QBU"})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `TaxStatements`
                        (`TaxStatementId`, `Name`, `FiscalYear`, `StartDate`, `EndDate`, `BaseCurrencyCode`,
                         `CreatedAtUtc`, `StatusChangedAt`)
                    VALUES ({Guid.NewGuid()}, {"Return 2025"}, {2025}, {new DateTime(2025, 1, 1)},
                            {new DateTime(2025, 12, 31)}, {"QTS"}, {now}, {now})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Properties`
                        (`PropertyId`, `Name`, `Description`, `Type`, `CurrencyCode`, `CreatedAt`, `UpdatedAt`)
                    VALUES ({propertyId}, {"Cabin"}, {"Cabin"}, {0}, {"QPR"}, {now}, {now})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `AccountEstimates`
                        (`AccountEstimateId`, `AccountId`, `Value`, `EffectiveFrom`, `CurrencyCode`, `CreatedAtUtc`)
                    VALUES ({Guid.NewGuid()}, {healthyAccount}, {1m}, {now}, {"QAE"}, {now})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `PropertyEstimates`
                        (`PropertyEstimateId`, `PropertyId`, `Value`, `EffectiveFrom`, `CurrencyCode`, `CreatedAtUtc`)
                    VALUES ({Guid.NewGuid()}, {propertyId}, {1m}, {now}, {"QPE"}, {now})
                    """);
                await MigrationSeam.InsertContractAsync(context, new Contract
                {
                    ContractId = contractId,
                    Name = "Lease",
                    CreatedAtUtc = now,
                });
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Terms`
                        (`TermId`, `ContractId`, `EffectiveFrom`, `CurrencyCode`, `CreatedAtUtc`, `Direction`,
                         `ValueUnit`, `Value`)
                    VALUES ({Guid.NewGuid()}, {contractId}, {now}, {"QTE"}, {now}, {0}, {1}, {10m})
                    """);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

                foreach (var code in new[] { "QAA", "NOK", "QTX", "QBU", "QTS", "QPR", "QAE", "QPE", "QTE" })
                {
                    var restored = await context.Currencies.AsNoTracking().SingleAsync(c => c.CurrencyCode == code);
                    Assert.Equal($"Restored currency {code} (review)", restored.Name);
                    Assert.Equal(2, restored.MinorUnits);
                    Assert.Null(restored.Symbol);
                    Assert.Null(restored.Archived);

                    // Each key now holds against the restored row.
                    var refused = await Assert.ThrowsAsync<MySqlException>(() =>
                        context.Database.ExecuteSqlRawAsync(
                            "DELETE FROM `Currencies` WHERE `CurrencyCode` = {0}", code));
                    Assert.Equal(MySqlErrorCode.RowIsReferenced2, refused.ErrorCode);
                }

                // Nothing beyond the orphans was invented, and a currency never missing is untouched.
                Assert.Equal(9L, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Currencies` WHERE `Name` LIKE 'Restored currency %'"));
                var usd = await context.Currencies.AsNoTracking().SingleAsync(c => c.CurrencyCode == "USD");
                Assert.Equal("US Dollar", usd.Name);

                // No referencing row was rewritten.
                Assert.Equal("QAA", await MigrationSeam.ScalarAsync(context,
                    $"SELECT `CurrencyCode` FROM `Accounts` WHERE `AccountId` = '{orphanAccount}'"));
                Assert.Equal("NOK", await MigrationSeam.ScalarAsync(context,
                    $"SELECT `CurrencyCode` FROM `Accounts` WHERE `AccountId` = '{deletedCurrencyAccount}'"));
                Assert.Equal("QTX", await MigrationSeam.ScalarAsync(context,
                    $"SELECT `CurrencyCode` FROM `Transactions` WHERE `TransactionId` = '{transactionId}'"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The repair's de-duplication claim, pinned on the real engine: two spellings the key treats as one
    /// code (the columns' default collation is case-insensitive) must yield exactly ONE restored row, not
    /// a duplicate-key failure that would leave the upgrade permanently stuck. A blank code — which no
    /// write path produces, but which a hand edit could — is restored too, flagged like the rest, because
    /// skipping it would make the key's own addition fail.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_restores_one_row_per_code_as_the_key_compares_them_and_restores_a_blank_code()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var lower = Guid.NewGuid();
        var upper = Guid.NewGuid();
        var now = DateTime.UtcNow;

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                await InsertAccountAsync(context, lower, "qcs");
                await InsertAccountAsync(context, upper, "QCS");
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `Transactions`
                        (`TransactionId`, `AccountId`, `Description`, `Amount`, `TimeStamp`, `CurrencyCode`,
                         `Status`, `StatusChangedAt`)
                    VALUES ({Guid.NewGuid()}, {upper}, {"Deposit"}, {1m}, {now}, {"Qcs"}, {0}, {now})
                    """);
                await context.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO `AccountEstimates`
                        (`AccountEstimateId`, `AccountId`, `Value`, `EffectiveFrom`, `CurrencyCode`, `CreatedAtUtc`)
                    VALUES ({Guid.NewGuid()}, {upper}, {1m}, {now}, {""}, {now})
                    """);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);
                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));

                Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Currencies` WHERE `CurrencyCode` = 'QCS'"));
                Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Currencies` WHERE `CurrencyCode` = ''"));
                Assert.Equal("Restored currency  (review)", await MigrationSeam.ScalarAsync(context,
                    "SELECT `Name` FROM `Currencies` WHERE `CurrencyCode` = ''"));
                Assert.Equal(2L, await MigrationSeam.CountAsync(context,
                    "SELECT COUNT(*) FROM `Currencies` WHERE `Name` LIKE 'Restored currency %'"));

                // One row satisfies every spelling: the key holds it against all three referencing rows.
                await Assert.ThrowsAsync<MySqlException>(() =>
                    context.Database.ExecuteSqlRawAsync("DELETE FROM `Currencies` WHERE `CurrencyCode` = 'QCS'"));
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
