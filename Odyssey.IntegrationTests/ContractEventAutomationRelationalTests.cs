// EF1002 flags interpolation into ExecuteSqlRawAsync. The only value interpolated below is the
// database name this test declares itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractEventSource = Odyssey.Context.ContractEventSource;
using ContextContractEventType = Odyssey.Context.ContractEventType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #154: the atomicity of a recorded event against the change it records
/// (AC 25).
/// </summary>
/// <remarks>
/// Not observable on the fast tiers. The EF InMemory provider honours neither transactions nor an
/// execution strategy, so a failed save leaves whatever the change tracker happened to apply. The
/// <em>staging</em> behaviour — one save, N rows — is verifiable on the fast tiers and is asserted there
/// (<c>ContractEventAutomationTests</c>); this is the half where the engine does the work.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractEventAutomationRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_event_source";

    private const string ActorUserId = "contract-event-automation-actor";

    /// <summary>
    /// AC 25, the contract half — a <c>PUT</c> whose save fails commits <b>neither</b> the stamp
    /// change nor any event row.
    /// </summary>
    /// <remarks>
    /// The save is made to fail by attributing the write to a user id that does not exist: every
    /// attribution column is a real foreign key to <c>AspNetUsers</c>, so the event <c>INSERT</c> is
    /// refused by the engine. That is the shape the property is actually about — the event and the
    /// change go out in ONE <c>SaveChangesAsync</c>, which EF wraps in one transaction, so a failure on
    /// either statement rolls the other back. On the InMemory tier neither the key nor the transaction
    /// exists, which is why this cannot live there.
    /// </remarks>
    [SkippableFact]
    public async Task A_failed_contract_save_commits_neither_the_stamp_nor_the_event()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        try
        {
            Guid contractId;
            await using (var context = NewContext())
            {
                await AttributionUsers.EnsureAsync(context, ActorUserId);
                var created = await Contracts(context).Create(NewRentalContract(), ActorUserId);
                contractId = created.ContractId;
            }

            await using (var context = NewContext())
            {
                await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
                    Contracts(context).Update(contractId, PauseWrite(), "no-such-user-id"));
            }

            await using (var context = NewContext())
            {
                var contract = await context.Contracts.AsNoTracking()
                    .SingleAsync(c => c.ContractId == contractId);
                Assert.Null(contract.Paused);

                Assert.Empty(await context.ContractEvents.AsNoTracking()
                    .Where(e => e.ContractId == contractId
                        && e.Type == ContextContractEventType.Paused)
                    .ToListAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 25, the term half — the same property through the §8.7 staging seam. The delegate stages
    /// onto the change tracker the private method is about to save, so the term write and its
    /// <c>TermChanged</c> event are one transaction; a refused event insert takes the term with it.
    /// </summary>
    [SkippableFact]
    public async Task A_failed_term_save_commits_neither_the_term_nor_the_event()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        try
        {
            Guid contractId;
            await using (var context = NewContext())
            {
                await AttributionUsers.EnsureAsync(context, ActorUserId);
                contractId = (await Contracts(context).Create(NewRentalContract(), ActorUserId)).ContractId;
            }

            await using (var context = NewContext())
            {
                await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
                    Terms(context).CreateForContract(contractId, NewRentTerm(), "no-such-user-id"));
            }

            await using (var context = NewContext())
            {
                Assert.Empty(await context.Terms.AsNoTracking()
                    .Where(t => t.ContractId == contractId).ToListAsync());
                Assert.Empty(await context.ContractEvents.AsNoTracking()
                    .Where(e => e.ContractId == contractId
                        && e.Type == ContextContractEventType.TermChanged)
                    .ToListAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The happy path on a real engine: the contract change and its events land together, in one save,
    /// with the acting user's real foreign key resolved.
    /// </summary>
    [SkippableFact]
    public async Task A_successful_write_commits_the_stamp_and_its_events_together()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        try
        {
            Guid contractId;
            await using (var context = NewContext())
            {
                await AttributionUsers.EnsureAsync(context, ActorUserId);
                contractId = (await Contracts(context).Create(NewRentalContract(), ActorUserId)).ContractId;
            }

            await using (var context = NewContext())
            {
                await Contracts(context).Update(contractId, PauseWrite(), ActorUserId);
            }

            await using (var context = NewContext())
            {
                var contract = await context.Contracts.AsNoTracking().SingleAsync(c => c.ContractId == contractId);
                Assert.NotNull(contract.Paused);

                var recorded = await context.ContractEvents.AsNoTracking()
                    .Where(e => e.ContractId == contractId && e.Type == ContextContractEventType.Paused)
                    .ToListAsync();
                var single = Assert.Single(recorded);
                Assert.Equal(ContextContractEventSource.System, single.Source);
                Assert.Equal(ActorUserId, single.CreatedByUserId);

                // No index on the column: the source filter is applied inside the contract-scoped
                // window the (ContractId, OccurredAt) index already serves, so a second one would buy
                // nothing against a log that is always read one contract at a time. Asserted because
                // an index added later without that reasoning is exactly what this records.
                Assert.False(await IndexExistsOnAsync(context, "Events", "Source"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static readonly DateTime StartedOn = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReadyOn = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SignedOn = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private static NewContract NewRentalContract() => new()
    {
        Name = "Maple St lease",
        Type = Odyssey.Dtos.Finance.ContractType.Rental,
        StartDate = StartedOn,
        // Signed, so the contract derives Active and EnsurePausable permits the pause below.
        Ready = ReadyOn,
        Signed = SignedOn,
    };

    private static UpdateContract PauseWrite() => new()
    {
        Name = "Maple St lease",
        Type = Odyssey.Dtos.Finance.ContractType.Rental,
        StartDate = StartedOn,
        IsPaused = true,
        Ready = ReadyOn,
        Signed = SignedOn,
    };

    private static NewTerm NewRentTerm() => new()
    {
        Label = "Monthly rent",
        ValueUnit = Odyssey.Dtos.Finance.TermValueUnit.Amount,
        Value = 14500m,
        CurrencyCode = "USD",
        Interval = Odyssey.Dtos.Finance.Interval.Monthly,
        EffectiveFrom = StartedOn,
    };

    private static ContractService Contracts(OdysseyContext context) =>
        new(context, new ContactLookup(context), TimeProvider.System,
            NullLogger<ContractService>.Instance);

    private static TermService Terms(OdysseyContext context) =>
        new(context, TimeProvider.System, new ShippedCaps(), NullLogger<TermService>.Instance);

    private sealed class ShippedCaps : ISystemSettingsLookup
    {

        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, 50, 500, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

    // ── Schema introspection ───────────────────────────────────────────────────

    private static async Task<bool> IndexExistsOnAsync(OdysseyContext context, string table, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'
            """) > 0;

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task MigrateAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
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
