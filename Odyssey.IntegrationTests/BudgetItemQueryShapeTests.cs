using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The N+1 guard for the embedded transaction tag (issue #75 §12, AC 11).
///
/// <para>
/// Embedding the tag on every budget item is only affordable because it rides on the query that was
/// already being issued. A missed <c>Include</c>/<c>ThenInclude</c> on any of the six read paths does
/// not fail loudly — it emits a null tag on the flat paths and a per-row lazy fetch on the nested
/// ones — so the cost has to be asserted as a COMMAND COUNT rather than inferred from the shape of
/// the result.
/// </para>
///
/// <para>
/// This lives in the relational tier by necessity, not by preference: counting commands needs a
/// <see cref="DbCommandInterceptor"/>, which the EF InMemory provider never invokes — the same trap
/// CLAUDE.md records for <c>ExecuteDeleteAsync</c>. A fast-tier version of this test would pass
/// whatever the query did.
/// </para>
/// </summary>
[Collection(MariaDbCollection.Name)]
public class BudgetItemQueryShapeTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_budget_query_shape";
    private const int ItemCount = 50;

    /// <summary>
    /// <c>ToPagedResultAsync</c> issues exactly two commands — a <c>CountAsync</c> and the window
    /// query — and the tag must ride on the second. A per-row tag fetch would make it 52.
    /// </summary>
    [SkippableFact]
    public async Task Listing_fifty_budget_items_issues_exactly_two_commands()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            var budgetId = await SeedAsync();

            var counter = new CommandCounter();
            await using var context = NewContext(counter);

            var page = await new BudgetItemService(context).ListAsync(
                new BudgetItemsQueryParams { BudgetId = budgetId, Limit = ItemCount });

            Assert.Equal(ItemCount, page.Items.Count);
            // Every row carries its tag — the point of the join, and what makes a second query
            // unnecessary rather than merely absent.
            Assert.All(page.Items, item => Assert.False(string.IsNullOrEmpty(item.Tag.Name)));

            Assert.Equal(2, counter.Count);
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// <c>GET /api/budgets</c> has the same exposure through the two nested read paths
    /// (§5.3 rows 5–6), where a missing <c>ThenInclude</c> costs one query per item rather than a
    /// null. Asserted as a ceiling rather than an exact number: this path legitimately issues several
    /// commands (the budgets, their items, the per-budget transaction counts), and pinning the exact
    /// count would fail on any unrelated change to the rollup.
    /// </summary>
    [SkippableFact]
    public async Task Listing_budgets_issues_no_per_item_tag_query()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await SeedAsync();

            var counter = new CommandCounter();
            await using var context = NewContext(counter);

            var page = await new BudgetService(context, new NoContacts()).ListAsync(new BudgetsQueryParams());

            var budget = Assert.Single(page.Items);
            Assert.Equal(ItemCount, budget.BudgetItems.Count);
            Assert.All(budget.BudgetItems, item => Assert.False(string.IsNullOrEmpty(item.Tag.Name)));

            // 52 would be the N+1 signature. A handful is the honest steady state.
            Assert.True(counter.Count < 10,
                $"GET /api/budgets issued {counter.Count} commands for {ItemCount} items — a per-item "
                + "tag query is the N+1 a missing ThenInclude on BudgetService produces.");
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAsync()
    {
        await using var context = NewContext();
        await MigrationRunner.MigrateAsync(context, CancellationToken.None);

        var budget = new Budget
        {
            Name = "2026",
            Description = "Annual",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            BaseCurrencyCode = "USD",
        };
        context.Budgets.Add(budget);

        for (var i = 0; i < ItemCount; i++)
        {
            var tag = new TransactionTag { Name = $"Tag {i:D2}", Description = $"Description {i:D2}" };
            context.TransactionTags.Add(tag);
            context.BudgetItems.Add(new BudgetItem
            {
                BudgetId = budget.BudgetId,
                CategoryType = Odyssey.Context.BudgetCategoryType.Expense,
                PlannedAmount = 100m + i,
                TransactionTagId = tag.TransactionTagId,
            });
        }

        await context.SaveChangesAsync();
        return budget.BudgetId;
    }

    /// <summary>Counts every command that reaches the server, which is the unit AC 11 is written in.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int count;

        public int Count => count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// The budget read path resolves no contacts here (no budget links one), so this keeps the lookup
    /// out of the command count rather than standing in for it.
    /// </summary>
    private sealed class NoContacts : IContactLookup
    {
        public Task<IReadOnlySet<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyDictionary<Guid, ContactRef>> ResolveRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ContactRef>>(new Dictionary<Guid, ContactRef>());

        public Task<IReadOnlyDictionary<Guid, ContactEmbed>> ResolveContactsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ContactEmbed>>(new Dictionary<Guid, ContactEmbed>());

        public Task<IReadOnlyList<Guid>> SearchIdsByNameAsync(string term, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<ContactRef>> ListActiveContactRefsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ContactRef>>([]);

        public Task<IReadOnlySet<Guid>> ExistingPersonIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyDictionary<Guid, string>> ResolveExternalUidsAsync(IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyDictionary<string, Guid>> ResolveIdsByExternalUidAsync(IReadOnlyCollection<string> externalUids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(new Dictionary<string, Guid>());
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

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var connection = fixture.ConnectionStringFor(Database);
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connection, ServerVersion.AutoDetect(connection));
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }
}
