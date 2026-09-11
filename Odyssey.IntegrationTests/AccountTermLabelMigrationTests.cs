// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input — and the pre-collapse rows have to be
// inserted with fee kinds the entity model no longer carries, which is exactly what makes raw SQL
// the only way to build the "before" state.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The two migrations behind account-term series labels (issue #57): the schema change that adds
/// <c>Label</c>/<c>LabelKey</c> and swaps the term index, and the data migration that collapses the
/// four fee kinds into one.
///
/// <para>
/// Both are only observable here. The EF InMemory provider runs no migrations at all and has no
/// foreign keys, so the index-ordering rule (an FK column must keep an index throughout) and the
/// backfill-then-remap SQL are unrunnable on every fast tier by construction — which is precisely why
/// the ordering defect this pins was invisible until the Testcontainers tier ran.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class AccountTermLabelMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_account_term_labels";

    /// <summary>The migration immediately before the label columns — the last point at which the term
    /// index is still <c>(AccountId, TermKind, EffectiveFrom)</c>.</summary>
    private const string Baseline = "_DropDeprecatedColumnsAndTuneIndexes";

    private const string AddLabel = "_AddAccountTermLabel";
    private const string Collapse = "_CollapseFeeTermKinds";

    private const string TermForeignKey = "FK_AccountTerms_Accounts_AccountId";

    /// <summary>
    /// §16.17 — the index swap applies and reverts with the term foreign key intact throughout.
    /// InnoDB requires an index on an FK column at all times, and EF's scaffolded order (every
    /// <c>DropIndex</c> ahead of every <c>CreateIndex</c>) drops the only index leading with
    /// <c>AccountId</c> before its replacement exists, failing with errno 1553.
    /// </summary>
    [SkippableFact]
    public async Task The_index_swap_applies_and_reverts_with_the_term_foreign_key_intact()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.True(await ForeignKeyExistsAsync(context));
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, AddLabel);

                Assert.True(await ForeignKeyExistsAsync(context));
                Assert.True(await IndexExistsAsync(context, "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom"));
                Assert.False(await IndexExistsAsync(context, "IX_AccountTerms_AccountId_TermKind_EffectiveFrom"));
            }

            // Down() mirrors the order, so the revert has to hold the same invariant.
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                Assert.True(await ForeignKeyExistsAsync(context));
                Assert.True(await IndexExistsAsync(context, "IX_AccountTerms_AccountId_TermKind_EffectiveFrom"));
                Assert.False(await IndexExistsAsync(context, "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// §16.18 — after the collapse every fee sits at kind 10 carrying the label its old kind name
    /// supplied, and a row an operator had already labelled keeps their own wording.
    /// </summary>
    [SkippableFact]
    public async Task The_collapse_backfills_old_kind_names_and_leaves_an_existing_label_alone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var management = Guid.NewGuid();
        var service = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var other = Guid.NewGuid();
        var named = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, AddLabel);

                context.Accounts.Add(new Account
                {
                    AccountId = accountId,
                    Name = "Travel card",
                    Description = "liability",
                    Opened = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AccountType = AccountType.CreditCard,
                    CurrencyCode = "USD",
                });
                await context.SaveChangesAsync();

                // The four departed ordinals, written raw because the model no longer names them.
                await InsertTermAsync(context, management, accountId, kind: 10, value: 95m, label: null);
                await InsertTermAsync(context, service, accountId, kind: 11, value: 12m, label: null);
                await InsertTermAsync(context, transaction, accountId, kind: 12, value: 5m, label: null);
                await InsertTermAsync(context, other, accountId, kind: 99, value: 15m, label: null);
                // A fee the operator had already named: the backfill must not overwrite it.
                await InsertTermAsync(context, named, accountId, kind: 12, value: 25m, label: "ATM · abroad");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Collapse);

                var terms = await context.AccountTerms.AsNoTracking()
                    .Where(t => t.AccountId == accountId)
                    .ToListAsync();

                // Every fee ends at Fee (10) — nothing is left on a retired ordinal.
                Assert.All(terms, term => Assert.Equal(TermKind.Fee, term.TermKind));

                Assert.Equal("Management fee", terms.Single(t => t.AccountTermId == management).Label);
                Assert.Equal("Service fee", terms.Single(t => t.AccountTermId == service).Label);
                Assert.Equal("Transaction fee", terms.Single(t => t.AccountTermId == transaction).Label);
                Assert.Equal("Other fee", terms.Single(t => t.AccountTermId == other).Label);
                Assert.Equal("ATM · abroad", terms.Single(t => t.AccountTermId == named).Label);

                // The folded key is written alongside, since that is what carries the series.
                Assert.Equal("management fee", terms.Single(t => t.AccountTermId == management).LabelKey);
                Assert.Equal("atm · abroad", terms.Single(t => t.AccountTermId == named).LabelKey);

                // The backfilled names are distinct, so no two remapped rows collapse into one series.
                Assert.Equal(terms.Count, terms.Select(t => t.LabelKey).Distinct().Count());
            }
        }
        finally
        {
            await DropAsync();
        }
    }


    /// <summary>
    /// The collapse reverts as documented: a fee whose label is one of the four names <c>Up()</c>
    /// itself wrote goes back to its old ordinal with the label cleared, and a fee the operator named
    /// keeps its label and stays at <c>Fee</c>.
    ///
    /// <para>
    /// <b>This asserts the loss, not just the restore.</b> <c>Down()</c> is best-effort by
    /// construction — the old kind survives only in the exact string <c>Up()</c> wrote — so a term a
    /// user named themselves has no recoverable kind. Pinning that keeps the lossiness a stated
    /// property rather than something a later change could quietly make worse or "fix" into a wrong
    /// guess.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_collapse_reverts_the_names_it_wrote_and_leaves_a_users_own_label_at_Fee()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var management = Guid.NewGuid();
        var service = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var other = Guid.NewGuid();
        var userNamed = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, AddLabel);

                context.Accounts.Add(new Account
                {
                    AccountId = accountId,
                    Name = "Travel card",
                    Description = "liability",
                    Opened = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AccountType = AccountType.CreditCard,
                    CurrencyCode = "USD",
                });
                await context.SaveChangesAsync();

                await InsertTermAsync(context, management, accountId, kind: 10, value: 95m, label: null);
                await InsertTermAsync(context, service, accountId, kind: 11, value: 12m, label: null);
                await InsertTermAsync(context, transaction, accountId, kind: 12, value: 5m, label: null);
                await InsertTermAsync(context, other, accountId, kind: 99, value: 15m, label: null);
                await InsertTermAsync(context, userNamed, accountId, kind: 12, value: 25m, label: "ATM · abroad");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Collapse);
            }

            await using (var context = NewContext())
            {
                // Back to the migration before the collapse — its Down() runs.
                await MigrationSeam.MigrateToAsync(context, AddLabel);

                var kinds = await KindsByIdAsync(context, accountId);
                var labels = await LabelsByIdAsync(context, accountId);

                // The three remapped ordinals are restored from the names Up() wrote, and those
                // names are cleared again.
                Assert.Equal(10, kinds[management]);
                Assert.Equal(11, kinds[service]);
                Assert.Equal(12, kinds[transaction]);
                Assert.Equal(99, kinds[other]);
                Assert.Null(labels[management]);
                Assert.Null(labels[service]);
                Assert.Null(labels[transaction]);
                Assert.Null(labels[other]);

                // The documented loss: a fee the user named themselves has no recoverable kind, so it
                // stays at Fee (10) — and keeps the name its author gave it.
                Assert.Equal(10, kinds[userNamed]);
                Assert.Equal("ATM · abroad", labels[userNamed]);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// Up-then-down-then-up is stable: a second forward run lands on exactly the state the first one
    /// produced. A <c>Down()</c> that cleared too much (or too little) would show up here as a
    /// different second pass rather than as a silent divergence in someone's database.
    /// </summary>
    [SkippableFact]
    public async Task The_collapse_is_stable_across_a_down_and_up_cycle()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var service = Guid.NewGuid();
        var userNamed = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, AddLabel);

                context.Accounts.Add(new Account
                {
                    AccountId = accountId,
                    Name = "Checking",
                    Description = "asset",
                    Opened = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AccountType = AccountType.CheckingAccount,
                    CurrencyCode = "USD",
                });
                await context.SaveChangesAsync();

                await InsertTermAsync(context, service, accountId, kind: 11, value: 12m, label: null);
                await InsertTermAsync(context, userNamed, accountId, kind: 11, value: 3m, label: "Paper statement");
            }

            Dictionary<Guid, string?> first;
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Collapse);
                first = await LabelsByIdAsync(context, accountId);
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, AddLabel);
                await MigrationSeam.MigrateToAsync(context, Collapse);

                Assert.Equal(first, await LabelsByIdAsync(context, accountId));
                var kinds = await KindsByIdAsync(context, accountId);
                Assert.Equal(10, kinds[service]);
                Assert.Equal(10, kinds[userNamed]);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>The stored <c>TermKind</c> ordinal per term id, read as the raw int the column holds
    /// (the entity enum no longer names the retired values).</summary>
    private static async Task<Dictionary<Guid, int>> KindsByIdAsync(OdysseyContext context, Guid accountId)
    {
        var terms = await context.AccountTerms.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => new { t.AccountTermId, Kind = (int)t.TermKind })
            .ToListAsync();

        return terms.ToDictionary(t => t.AccountTermId, t => t.Kind);
    }

    private static async Task<Dictionary<Guid, string?>> LabelsByIdAsync(OdysseyContext context, Guid accountId)
    {
        var terms = await context.AccountTerms.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .Select(t => new { t.AccountTermId, t.Label })
            .ToListAsync();

        return terms.ToDictionary(t => t.AccountTermId, t => t.Label);
    }

    private static async Task InsertTermAsync(
        OdysseyContext context, Guid termId, Guid accountId, int kind, decimal value, string? label)
    {
        var labelSql = label is null ? "NULL" : $"'{label}'";
        var keySql = label is null ? "NULL" : $"'{label.ToLowerInvariant()}'";

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `AccountTerms` " +
            "(`AccountTermId`, `AccountId`, `TermKind`, `Label`, `LabelKey`, `ValueUnit`, `Value`, " +
            " `CurrencyCode`, `BillingPeriod`, `EffectiveFrom`, `Note`, `CreatedAtUtc`) " +
            $"VALUES ('{termId}', '{accountId}', {kind}, {labelSql}, {keySql}, 1, {value}, " +
            "'USD', NULL, '2024-01-01 00:00:00', NULL, '2024-01-01 00:00:00')");
    }

    private static async Task<bool> IndexExistsAsync(OdysseyContext context, string name) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.STATISTICS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AccountTerms' " +
                $"AND INDEX_NAME = '{name}'")
            .SingleAsync() > 0;

    private static async Task<bool> ForeignKeyExistsAsync(OdysseyContext context) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.TABLE_CONSTRAINTS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AccountTerms' " +
                $"AND CONSTRAINT_TYPE = 'FOREIGN KEY' AND CONSTRAINT_NAME = '{TermForeignKey}'")
            .SingleAsync() > 0;

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

    /// <summary>A connection to a database that always exists — the private one is being created or
    /// dropped, so it cannot be the one the statement connects through.</summary>
    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}

#pragma warning restore EF1002
