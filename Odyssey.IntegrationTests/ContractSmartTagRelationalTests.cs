// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test generated itself — there is no external input — and the deletes are deliberately
// issued outside the services, which is the whole point: the foreign keys exist so the engine, and not
// application code, is what resolves them.
#pragma warning disable EF1002

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #166 (AC 11, 13, 16, 17, 20): the two foreign keys on
/// <c>ContractSmartTags</c>, the migration that creates them, the duplicate-pair race, and the list
/// query's shape.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers — the EF InMemory provider enforces no foreign keys at
/// all and never invokes a <see cref="DbCommandInterceptor"/>, so a cascade there is whatever
/// application code happens to do and a query count is unmeasurable. The application-code twins are in
/// <c>ContractSmartTagsApiTests</c>; the two halves are not redundant, because either one alone leaves
/// the other tier free to regress silently.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractSmartTagRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_smart_tags";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// AC 16 — the migration applies cleanly from an empty database and the table it creates carries
    /// the composite primary key, the <c>AddedAt</c> index and the settings seed row §11 names.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_creates_the_composite_key_the_index_and_the_settings_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();

        Assert.Equal(["ContractId", "TransactionTagId"], await ReadIndexColumnsAsync(context, "PRIMARY"));
        Assert.Equal(["AddedAt"], await ReadIndexColumnsAsync(context, "IX_ContractSmartTags_AddedAt"));

        var seeded = await context.SystemSettings.AsNoTracking()
            .Where(row => row.Key == SystemSettingsKeys.ContractMaxSmartTagsPerContract)
            .Select(row => row.Value)
            .SingleAsync();
        Assert.Equal(
            SystemSettingsDefaults.ContractMaxSmartTagsPerContract.ToString(),
            seeded);
    }

    /// <summary>
    /// AC 16 — both foreign keys carry the on-delete behaviour §4 names, read back from the engine
    /// rather than inferred from the model. CASCADE for the owner (the saved filter is meaningless once
    /// the contract is gone); RESTRICT for the tag (an in-use tag must not be hard-deleted out from
    /// under a contract's configuration).
    /// </summary>
    [SkippableFact]
    public async Task Both_foreign_keys_carry_the_declared_on_delete_behaviour()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var rules = await ReadDeleteRulesAsync(context);

        Assert.Equal("CASCADE", rules["ContractId"]);
        Assert.Equal("RESTRICT", rules["TransactionTagId"]);
    }

    /// <summary>
    /// AC 11 — deleting the contract takes its link rows with it, leaves another contract's alone, and
    /// leaves every linked tag intact. Issued as raw SQL so the ENGINE resolves it, not
    /// <c>ContractService.Delete</c>'s includes.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_contract_cascades_its_links_and_keeps_the_tags()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid doomed, surviving, tagId;

        await using (var context = NewContext())
        {
            doomed = await SeedContractAsync(context, "Maple St lease");
            surviving = await SeedContractAsync(context, "Survivor");
            tagId = await SeedTagAsync(context, "Streaming");

            await SeedLinkAsync(context, doomed, tagId);
            await SeedLinkAsync(context, surviving, tagId);

            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM `Contracts` WHERE `ContractId` = '{doomed}'");
        }

        await using (var verify = NewContext())
        {
            var remaining = await verify.ContractSmartTags.AsNoTracking()
                .Select(link => link.ContractId).ToListAsync();

            Assert.DoesNotContain(doomed, remaining);
            Assert.Contains(surviving, remaining);
            Assert.True(await verify.TransactionTags.AsNoTracking()
                .AnyAsync(tag => tag.TransactionTagId == tagId));
        }
    }

    /// <summary>
    /// AC 17 — the RESTRICT key refuses a raw tag delete. The pre-check in
    /// <c>TransactionTagService.Delete</c> is what turns this into a <c>409</c> that explains itself;
    /// this is the backstop under it, and the reason the pre-check is not merely cosmetic on MariaDB.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_watched_tag_is_refused_by_the_engine()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var contractId = await SeedContractAsync(context, "Maple St lease");
        var tagId = await SeedTagAsync(context, "Streaming");
        await SeedLinkAsync(context, contractId, tagId);

        await Assert.ThrowsAnyAsync<DbException>(() => context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM `TransactionTags` WHERE `TransactionTagId` = '{tagId}'"));

        Assert.True(await context.TransactionTags.AsNoTracking()
            .AnyAsync(tag => tag.TransactionTagId == tagId));
    }

    /// <summary>
    /// AC 17, the service half against a real engine — the pre-check refuses with a
    /// <c>DomainConflictException</c> naming the COUNT and no contract names, before the engine ever
    /// sees the delete.
    /// </summary>
    [SkippableFact]
    public async Task The_service_pre_check_refuses_a_watched_tag_with_a_count_and_no_names()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var contractId = await SeedContractAsync(context, "Maple St lease");
        var tagId = await SeedTagAsync(context, "Streaming");
        await SeedLinkAsync(context, contractId, tagId);

        var exception = await Assert.ThrowsAsync<DomainConflictException>(
            () => new TransactionTagService(context).Delete(tagId));

        Assert.Contains("1 contract", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Maple St lease", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 20 — two concurrent adds of the same pair yield exactly one row. The service's pre-check is
    /// advisory; the composite primary key is the control, and the loser's duplicate-key error is what
    /// <c>GlobalExceptionHandler</c> turns into the same <c>409</c> the pre-check would have produced.
    /// </summary>
    [SkippableFact]
    public async Task Two_concurrent_adds_of_the_same_pair_yield_exactly_one_row()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid contractId, tagId;
        await using (var seed = NewContext())
        {
            contractId = await SeedContractAsync(seed, "Maple St lease");
            tagId = await SeedTagAsync(seed, "Streaming");
        }

        await using var first = NewContext();
        await using var second = NewContext();
        var limits = new StubContractLimitsLookup();

        // Both services read the empty table before either writes, so both pre-checks pass.
        var outcomes = await Task.WhenAll(
            AttemptAsync(new ContractSmartTagService(first, limits), contractId, tagId),
            AttemptAsync(new ContractSmartTagService(second, limits), contractId, tagId));

        Assert.Equal(1, outcomes.Count(succeeded => succeeded));

        await using var verify = NewContext();
        Assert.Equal(1, await verify.ContractSmartTags.AsNoTracking().CountAsync());

        static async Task<bool> AttemptAsync(ContractSmartTagService service, Guid contractId, Guid tagId)
        {
            try
            {
                await service.AddSmartTag(contractId, tagId);
                return true;
            }
            catch (Exception exception) when (exception is DbUpdateException or DomainConflictException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// AC 13 — the list issues the same number of commands for many watched contracts as for one,
    /// because the smart-tag count is a fifth correlated subquery inside the one query rather than a
    /// second grouped read. Counting commands needs a <see cref="DbCommandInterceptor"/>, which the EF
    /// InMemory provider never invokes, so this can only live here.
    /// </summary>
    [SkippableFact]
    public async Task The_list_issues_the_same_command_count_for_many_watched_contracts_as_for_one()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        const int ContractCount = 25;
        const int TagsEach = 3;

        await using (var seed = NewContext())
        {
            var tagIds = new List<Guid>();
            for (var t = 0; t < TagsEach; t++)
            {
                tagIds.Add(await SeedTagAsync(seed, $"tag-{t}"));
            }

            for (var i = 0; i < ContractCount; i++)
            {
                var contractId = await SeedContractAsync(seed, $"Lease {i:D2}");
                foreach (var tagId in tagIds)
                {
                    await SeedLinkAsync(seed, contractId, tagId);
                }
            }
        }

        int manyCommands;
        await using (var counted = NewContext(out var counter))
        {
            var page = await ListAsync(counted, limit: ContractCount);

            Assert.Equal(ContractCount, page.Items.Count);
            // The counts are right AND cheap — a per-contract query would satisfy the first alone.
            Assert.All(page.Items, item => Assert.Equal(TagsEach, item.SmartTagCount));
            manyCommands = counter.Count;
        }

        await using (var counted = NewContext(out var counter))
        {
            var page = await ListAsync(counted, limit: 1);

            Assert.Single(page.Items);
            Assert.Equal(TagsEach, page.Items[0].SmartTagCount);

            // The whole criterion in one line: the cost does not move with the number of rows.
            Assert.Equal(counter.Count, manyCommands);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<PagedResult<ContractListItem>> ListAsync(OdysseyContext context, int limit) =>
        await new ContractService(
                context,
                // The REAL lookup, not a stub: no contract here links a contact, so it issues no
                // command at all — and a stub would have to be kept in step with an interface this
                // test has no stake in.
                new ContactLookup(context),
                TimeProvider.System,
                new ShippedCaps(),
                NullLogger<ContractService>.Instance)
            .ListAsync(new ContractsQueryParams { Limit = limit });

    private static async Task<Guid> SeedContractAsync(OdysseyContext context, string name)
    {
        var contract = new Contract
        {
            ContractId = Guid.NewGuid(),
            Name = name,
            Type = ContextContractType.Rental,
            StartDate = Anchor,
            CreatedAtUtc = Anchor,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedTagAsync(OdysseyContext context, string name)
    {
        var tag = new TransactionTag { TransactionTagId = Guid.NewGuid(), Name = name };
        context.TransactionTags.Add(tag);
        await context.SaveChangesAsync();
        return tag.TransactionTagId;
    }

    private static async Task SeedLinkAsync(OdysseyContext context, Guid contractId, Guid tagId)
    {
        context.ContractSmartTags.Add(new ContractSmartTag
        {
            ContractId = contractId,
            TransactionTagId = tagId,
            AddedAt = Anchor,
        });
        await context.SaveChangesAsync();
    }

    private static async Task<List<string>> ReadIndexColumnsAsync(OdysseyContext context, string indexName)
    {
        var rows = await context.Database
            .SqlQuery<IndexColumn>($"""
                SELECT COLUMN_NAME AS ColumnName
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'ContractSmartTags'
                  AND INDEX_NAME = {indexName}
                ORDER BY SEQ_IN_INDEX
                """)
            .ToListAsync();

        return [.. rows.Select(row => row.ColumnName)];
    }

    private static async Task<Dictionary<string, string>> ReadDeleteRulesAsync(OdysseyContext context)
    {
        var rows = await context.Database
            .SqlQuery<ForeignKeyRule>($"""
                SELECT k.COLUMN_NAME AS ColumnName, r.DELETE_RULE AS DeleteRule
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                  ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
                 AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_SCHEMA = DATABASE()
                  AND k.TABLE_NAME = 'ContractSmartTags'
                """)
            .ToListAsync();

        return rows.ToDictionary(row => row.ColumnName, row => row.DeleteRule, StringComparer.Ordinal);
    }

    private sealed record IndexColumn(string ColumnName);

    private sealed record ForeignKeyRule(string ColumnName, string DeleteRule);

    private sealed class StubContractLimitsLookup : IContractLimitsLookup
    {
        public Task<ContractLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractLimits(
                SystemSettingsDefaults.ContractMaxSmartTagsPerContract, IsDegraded: false));
    }

    /// <summary>The shipped cap values; the list path reads none of them, so they never vary here.</summary>
    private sealed class ShippedCaps : ISystemSettingsLookup
    {

        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, 50, 500, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

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
    }

    private async Task MigrateAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString));
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new OdysseyContext(builder.Options);
    }

    private OdysseyContext NewContext(out CommandCounter counter)
    {
        counter = new CommandCounter();
        return NewContext(counter);
    }

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
