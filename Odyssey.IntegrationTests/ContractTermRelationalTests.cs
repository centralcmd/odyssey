// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted literal this test generated itself — there is no external input — and the rows are
// deliberately written outside the service, which is the whole point: the CHECK constraint exists as a
// backstop against exactly such a writer.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #135: the exactly-one-owner CHECK constraint, the contract foreign
/// key's cascade, and the nullability change on <c>Terms.AccountId</c>.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers. The EF InMemory provider enforces neither CHECK
/// constraints nor foreign keys, so the service guard is the only implementation those tiers see — and
/// the guard is application code, which is precisely what the constraint backstops. The cascade has an
/// application-code twin (<c>ContractService.Delete</c>'s <c>.Include(c =&gt; c.Terms)</c>) asserted in
/// <c>Odyssey.Api.Tests</c>; this is the other half of AC 13, where the database does the work.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractTermRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_terms";

    /// <summary>AC 14 — a row with BOTH owners set is refused by the constraint.</summary>
    [SkippableFact]
    public async Task A_term_naming_two_owners_is_refused_by_the_check_constraint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var (accountId, contractId) = await SeedOwnersAsync(context);

        var error = await Assert.ThrowsAsync<MySqlException>(() =>
            InsertTermAsync(context, Guid.NewGuid(), accountId: accountId, contractId: contractId));

        Assert.Contains("CK_Terms_ExactlyOneOwner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AC 14 — a row with NEITHER owner set is refused by the same constraint.</summary>
    [SkippableFact]
    public async Task A_term_naming_no_owner_is_refused_by_the_check_constraint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        await SeedOwnersAsync(context);

        var error = await Assert.ThrowsAsync<MySqlException>(() =>
            InsertTermAsync(context, Guid.NewGuid(), accountId: null, contractId: null));

        Assert.Contains("CK_Terms_ExactlyOneOwner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Either owner alone is accepted — the constraint is exactly-one, not at-most-one.</summary>
    [SkippableFact]
    public async Task A_term_naming_exactly_one_owner_is_accepted_for_either_owner()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var (accountId, contractId) = await SeedOwnersAsync(context);

        await InsertTermAsync(context, Guid.NewGuid(), accountId: accountId, contractId: null);
        await InsertTermAsync(context, Guid.NewGuid(), accountId: null, contractId: contractId);

        Assert.Equal(2, await context.Terms.CountAsync());
    }

    /// <summary>
    /// AC 13, the relational half — deleting a contract cascades its term rows away, and leaves every
    /// account term and every other contract's terms untouched.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_contract_cascades_its_terms_and_spares_every_other_owner()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid accountTerm, doomedTerm, survivingTerm;

        await using (var context = NewContext())
        {
            var (accountId, contractId) = await SeedOwnersAsync(context);
            var survivorId = Guid.NewGuid();
            await SeedContractAsync(context, survivorId, "Survivor");

            accountTerm = Guid.NewGuid();
            doomedTerm = Guid.NewGuid();
            survivingTerm = Guid.NewGuid();

            await InsertTermAsync(context, accountTerm, accountId: accountId, contractId: null);
            await InsertTermAsync(context, doomedTerm, accountId: null, contractId: contractId);
            await InsertTermAsync(context, survivingTerm, accountId: null, contractId: survivorId);

            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `Contracts` WHERE `ContractId` = '{contractId}'");
        }

        await using (var context = NewContext())
        {
            var remaining = await context.Terms.AsNoTracking().Select(t => t.TermId).ToListAsync();

            Assert.DoesNotContain(doomedTerm, remaining);
            Assert.Contains(accountTerm, remaining);
            Assert.Contains(survivingTerm, remaining);
        }
    }

    /// <summary>
    /// Deleting an ACCOUNT still cascades its terms — the behaviour that predates issue #135 and must
    /// not have been disturbed by the column becoming nullable.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_an_account_still_cascades_its_terms()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid accountTerm, contractTerm;

        await using (var context = NewContext())
        {
            var (accountId, contractId) = await SeedOwnersAsync(context);
            accountTerm = Guid.NewGuid();
            contractTerm = Guid.NewGuid();

            await InsertTermAsync(context, accountTerm, accountId: accountId, contractId: null);
            await InsertTermAsync(context, contractTerm, accountId: null, contractId: contractId);

            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `Accounts` WHERE `AccountId` = '{accountId}'");
        }

        await using (var context = NewContext())
        {
            var remaining = await context.Terms.AsNoTracking().Select(t => t.TermId).ToListAsync();

            Assert.DoesNotContain(accountTerm, remaining);
            Assert.Contains(contractTerm, remaining);
        }
    }

    /// <summary>
    /// AC 18 — the migration leaves the shipped default seeded for the new cap, in the same
    /// <c>SystemSettings</c> table every other key lives in.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_seeds_the_per_contract_term_cap()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var row = await context.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SystemSettingsKeys.ContractMaxTermsPerContract);

        Assert.NotNull(row);
        Assert.Equal("500", row!.Value);
        // Seeded rows take no owner: the provenance line reads "nobody has taken ownership yet",
        // which is exactly true.
        Assert.Null(row.UpdatedBy);
    }

    /// <summary>
    /// The contract-side index exists and mirrors the account one, column for column. A history read
    /// filtered on the contract is the query it backs, and without it every contract term read is a
    /// full scan of a table both owners share.
    /// </summary>
    [SkippableFact]
    public async Task The_contract_index_mirrors_the_account_one()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var columns = await ReadIndexColumnsAsync(context, "IX_Terms_ContractId_TermKind_LabelKey_EffectiveFrom");

        Assert.Equal(["ContractId", "TermKind", "LabelKey", "EffectiveFrom"], columns);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<string>> ReadIndexColumnsAsync(OdysseyContext context, string indexName)
    {
        var columns = new List<string>();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Terms' AND INDEX_NAME = @indexName
            ORDER BY SEQ_IN_INDEX
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@indexName";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        await context.Database.OpenConnectionAsync();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        return columns;
    }

    private static async Task<(Guid AccountId, Guid ContractId)> SeedOwnersAsync(OdysseyContext context)
    {
        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        context.Accounts.Add(new Account
        {
            AccountId = accountId,
            Name = "Savings",
            Description = "Seeded for the owner-constraint assertions.",
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AccountType = ContextAccountType.SavingsAccount,
            CurrencyCode = "USD",
        });
        await context.SaveChangesAsync();

        await SeedContractAsync(context, contractId, "Maple St lease");
        return (accountId, contractId);
    }

    private static async Task SeedContractAsync(OdysseyContext context, Guid contractId, string name)
    {
        context.Contracts.Add(new Contract
        {
            ContractId = contractId,
            Name = name,
            Type = Odyssey.Context.ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Raw SQL on purpose: the domain service cannot produce a two-owner or no-owner row, which is
    /// what makes a direct writer the only thing the constraint can be observed refusing.
    /// </summary>
    private static Task InsertTermAsync(OdysseyContext context, Guid termId, Guid? accountId, Guid? contractId)
    {
        var account = accountId is { } a ? $"'{a}'" : "NULL";
        var contract = contractId is { } c ? $"'{c}'" : "NULL";
        var effectiveFrom = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var createdAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return context.Database.ExecuteSqlRawAsync(
            $"""
             INSERT INTO `Terms`
               (`TermId`, `AccountId`, `ContractId`, `TermKind`, `Label`, `LabelKey`, `ValueUnit`,
                `Value`, `CurrencyCode`, `Interval`, `IntervalCount`, `AnchorDate`, `EffectiveFrom`,
                `Note`, `CreatedAtUtc`)
             VALUES
               ('{termId}', {account}, {contract}, 10, 'Monthly rent', 'monthly rent', 1,
                14500.000000, 'USD', NULL, NULL, NULL, '{effectiveFrom}',
                NULL, '{createdAt}')
             """);
    }

    private async Task MigrateAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
