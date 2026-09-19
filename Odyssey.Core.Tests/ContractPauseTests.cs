using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Core.Tests;

/// <summary>
/// The pause stamp (issue #140): the derivation, the transition guard and the set/clear rule.
///
/// <para>
/// The subject of most of what follows is the <b>one-off</b> shape. <c>DeriveBaseStatus</c> resolves a
/// one-off in an early return that collapses both of its outcomes, so a pause check written as a step
/// in that chain would be unreachable for a settled one-off — the stamp would store, the reads would
/// keep saying <c>Active</c>, and the contract would keep contributing to the run rate. The exclusions
/// are expressed only through the derived status, so a derivation that misses this shape misses every
/// one of them at once (issue #140 §8, AC 23–24).
/// </para>
/// </summary>
public class ContractPauseTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class Caps : ISystemSettingsLookup
    {
        public Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InsurancePolicySettings(30, 1000));

        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, 50, 500, 1000, 100, 50, 50));

        public Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubscriptionSettings(45, 6, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

    private ContractService CreateService(OdysseyContext context) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new Caps(), NullLogger<ContractService>.Instance);

    private static NewContract Term(DateTime? start, DateTime? end = null) => new()
    {
        Name = "Agreement",
        Type = DtoContractType.Service,
        StartDate = start,
        EndDate = end,
    };

    private static NewContract OneOff(DateTime completion) => new()
    {
        Name = "One-off agreement",
        Type = DtoContractType.Service,
        CompletionDate = completion,
    };

    /// <summary>A write that re-states the contract as it stands and sets the two flags.</summary>
    private static UpdateContract Write(
        ExistingContract from, bool isPaused = false, bool isArchived = false,
        DateTime? startDate = null, DateTime? endDate = null, DateTime? completionDate = null) => new()
    {
        Name = from.Name,
        Type = from.Type,
        StartDate = startDate ?? from.StartDate,
        EndDate = endDate ?? from.EndDate,
        CompletionDate = completionDate ?? from.CompletionDate,
        IsArchived = isArchived,
        IsPaused = isPaused,
    };

    // ── Setting and clearing the stamp (AC 1–3) ───────────────────────────────

    [Fact]
    public async Task Pause_OnAnActiveContract_SetsTheStampAndDerivesPaused()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)));

        var paused = await service.Update(created.ContractId, Write(created, isPaused: true));

        Assert.Equal(FixedToday, paused!.Paused);
        Assert.Equal(ContractStatus.Paused, paused.Status);

        // …and the read agrees with the write.
        var fetched = await service.Get(created.ContractId);
        Assert.Equal(FixedToday, fetched!.Paused);
        Assert.Equal(ContractStatus.Paused, fetched.Status);
    }

    /// <summary>
    /// Idempotence, the same rule the archive stamp follows: a repeated or replayed PUT keeps the
    /// ORIGINAL stamp, so "paused since" never resets.
    /// </summary>
    [Fact]
    public async Task Pause_Repeated_KeepsTheOriginalStamp()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10)));

        var first = await service.Update(created.ContractId, Write(created, isPaused: true));

        // A later clock — only a fresh stamp could move, so a moved value is the defect.
        var later = new ContractService(
            context, TestContextFactory.ContactLookup(journal),
            new FixedTimeProvider(FixedToday.AddDays(3)), new Caps(), NullLogger<ContractService>.Instance);
        var second = await later.Update(created.ContractId, Write(created, isPaused: true));

        Assert.Equal(first!.Paused, second!.Paused);
        Assert.Equal(FixedToday, second.Paused);
    }

    [Fact]
    public async Task Resume_ClearsTheStampAndReturnsToActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10)));
        await service.Update(created.ContractId, Write(created, isPaused: true));

        var resumed = await service.Update(created.ContractId, Write(created, isPaused: false));

        Assert.Null(resumed!.Paused);
        Assert.Equal(ContractStatus.Active, resumed.Status);
    }

    // ── The transition guard (AC 4–6) ─────────────────────────────────────────

    [Theory]
    [InlineData(ContractStatus.Upcoming)]
    [InlineData(ContractStatus.Expired)]
    [InlineData(ContractStatus.Archived)]
    public async Task Pause_FromAnythingButActive_IsRefused_AndWritesNothing(ContractStatus from)
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = from switch
        {
            ContractStatus.Upcoming => await service.Create(Term(FixedToday.AddDays(5))),
            _ => await service.Create(Term(FixedToday.AddDays(-100), FixedToday.AddDays(-1))),
        };
        if (from == ContractStatus.Archived)
        {
            created = (await service.Update(created.ContractId, Write(created, isArchived: true)))!;
        }

        Assert.Equal(from, created.Status);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(created.ContractId, Write(created, isPaused: true, isArchived: from == ContractStatus.Archived)));

        Assert.Equal("contract_pause_requires_active", refusal.Code);
        Assert.NotNull(refusal.Errors);
        Assert.True(refusal.Errors!.ContainsKey(nameof(UpdateContract.IsPaused)));
        // The message names the contract's own derived status and nothing else.
        Assert.Contains(from.ToString(), refusal.Message, StringComparison.Ordinal);

        var unchanged = await service.Get(created.ContractId);
        Assert.Null(unchanged!.Paused);
        Assert.Equal(from, unchanged.Status);
    }

    /// <summary>
    /// The guard reads the REQUEST's dates, not the stored ones — the same way the archive guard does
    /// — so one PUT may move a start date into the past and pause in the same write.
    /// </summary>
    [Fact]
    public async Task Pause_AgainstTheRequestsDates_LetsOnePutStartAndPause()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(5)));
        Assert.Equal(ContractStatus.Upcoming, created.Status);

        var paused = await service.Update(
            created.ContractId, Write(created, isPaused: true, startDate: FixedToday.AddDays(-5)));

        Assert.NotNull(paused!.Paused);
        Assert.Equal(ContractStatus.Paused, paused.Status);
    }

    /// <summary>
    /// Only the transition is checked. A contract already paused is never re-validated, so one that
    /// later expires is never stranded in a state it cannot be written out of.
    /// </summary>
    [Fact]
    public async Task PausedContract_IsNeverReValidated_AndStaysEditable()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)));
        var paused = (await service.Update(created.ContractId, Write(created, isPaused: true)))!;

        // Re-save with an end date that has lapsed: the stamp is already set, so no guard fires.
        var expired = await service.Update(
            created.ContractId, Write(paused, isPaused: true, endDate: FixedToday.AddDays(-1)));

        Assert.NotNull(expired!.Paused);
        Assert.Equal(ContractStatus.Expired, expired.Status);
    }

    /// <summary>Clearing is never refused — a guard on the way out is how a row gets stranded.</summary>
    [Fact]
    public async Task Resume_OnAnArchivedContractCarryingAStamp_IsAllowed()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)));
        var paused = (await service.Update(created.ContractId, Write(created, isPaused: true)))!;

        // End it and archive it, keeping the pause stamp.
        var archived = (await service.Update(
            created.ContractId,
            Write(paused, isPaused: true, isArchived: true, endDate: FixedToday.AddDays(-1))))!;
        Assert.Equal(ContractStatus.Archived, archived.Status);
        Assert.NotNull(archived.Paused);

        var cleared = await service.Update(
            created.ContractId, Write(archived, isPaused: false, isArchived: true, endDate: FixedToday.AddDays(-1)));

        Assert.Null(cleared!.Paused);
        Assert.Equal(ContractStatus.Archived, cleared.Status);
    }

    // ── Precedence: Archived > Upcoming > Expired > Paused > Active (AC 7–8) ──

    [Fact]
    public async Task Derivation_ATerminalFactOutranksTheTemporaryOne()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        async Task<ExistingContract> PausedThen(DateTime? start, DateTime? end, bool archive)
        {
            var c = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)));
            var p = (await service.Update(c.ContractId, Write(c, isPaused: true)))!;
            return (await service.Update(
                c.ContractId,
                Write(p, isPaused: true, isArchived: archive, startDate: start, endDate: end)))!;
        }

        // Paused + archived → Archived.
        var archived = await PausedThen(FixedToday.AddDays(-10), FixedToday.AddDays(-1), archive: true);
        Assert.Equal(ContractStatus.Archived, archived.Status);
        Assert.NotNull(archived.Paused);

        // Paused + end before today → Expired.
        var expired = await PausedThen(FixedToday.AddDays(-10), FixedToday.AddDays(-1), archive: false);
        Assert.Equal(ContractStatus.Expired, expired.Status);
        Assert.NotNull(expired.Paused);

        // Paused + start after today → Upcoming.
        var upcoming = await PausedThen(FixedToday.AddDays(5), FixedToday.AddDays(400), archive: false);
        Assert.Equal(ContractStatus.Upcoming, upcoming.Status);
        Assert.NotNull(upcoming.Paused);

        // Paused and otherwise running → Paused.
        var running = await PausedThen(FixedToday.AddDays(-10), FixedToday.AddDays(10), archive: false);
        Assert.Equal(ContractStatus.Paused, running.Status);
    }

    /// <summary>
    /// Unarchiving a paused contract that has ended returns it to <c>Expired</c> — archive implies
    /// ended — with the stamp still on file, so resuming it after fixing its dates is one write.
    /// </summary>
    [Fact]
    public async Task Unarchive_ReturnsToExpired_WithTheStampIntact()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)));
        var paused = (await service.Update(created.ContractId, Write(created, isPaused: true)))!;
        var archived = (await service.Update(
            created.ContractId,
            Write(paused, isPaused: true, isArchived: true, endDate: FixedToday.AddDays(-1))))!;

        var unarchived = await service.Update(
            created.ContractId,
            Write(archived, isPaused: true, isArchived: false, endDate: FixedToday.AddDays(-1)));

        Assert.Equal(ContractStatus.Expired, unarchived!.Status);
        Assert.NotNull(unarchived.Paused);
    }

    // ── The one-off shape (AC 23) ─────────────────────────────────────────────

    /// <summary>
    /// A settled one-off derives as <c>Active</c> and is therefore pausable — and once paused it must
    /// read <c>Paused</c> on the detail read, in the list projection, under the status filter and in
    /// the summary bucket. This is the case an ordered-chain reading of the derivation gets wrong: the
    /// one-off early return resolves before any later branch, so a pause check written there is
    /// unreachable and every one of these four reads would silently keep saying <c>Active</c>.
    /// </summary>
    [Fact]
    public async Task PausedSettledOneOff_ReadsPaused_Everywhere_NeverActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(OneOff(FixedToday.AddDays(-1)));
        Assert.Equal(ContractStatus.Active, created.Status);

        var paused = await service.Update(created.ContractId, Write(created, isPaused: true));

        // 1. The detail read.
        Assert.Equal(ContractStatus.Paused, paused!.Status);
        Assert.Equal(ContractStatus.Paused, (await service.Get(created.ContractId))!.Status);

        // 2. The list projection.
        var listed = Assert.Single((await service.ListAsync(new ContractsQueryParams())).Items);
        Assert.Equal(ContractStatus.Paused, listed.Status);
        Assert.Equal(FixedToday, listed.Paused);

        // 3. The status filter.
        var filtered = await service.ListAsync(new ContractsQueryParams { Statuses = [ContractStatus.Paused] });
        Assert.Equal(created.ContractId, Assert.Single(filtered.Items).ContractId);
        Assert.Empty((await service.ListAsync(new ContractsQueryParams { Statuses = [ContractStatus.Active] })).Items);

        // 4. The summary bucket.
        var summary = await service.GetSummary(baseCurrency: null);
        Assert.Equal(1, summary.CountsByStatus.Paused);
        Assert.Equal(0, summary.CountsByStatus.Active);
    }

    /// <summary>A completion date still in the future is Upcoming, so pausing it is refused.</summary>
    [Fact]
    public async Task Pause_OnAnUnsettledOneOff_IsRefused()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(OneOff(FixedToday.AddDays(5)));

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(created.ContractId, Write(created, isPaused: true)));

        Assert.Equal("contract_pause_requires_active", refusal.Code);
    }

    // ── List filtering and sorting (AC 9–10) ─────────────────────────────────

    [Fact]
    public async Task List_WithNoStatusFilter_StillIncludesPausedContracts()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var active = await service.Create(Term(FixedToday.AddDays(-10)));
        var toPause = await service.Create(Term(FixedToday.AddDays(-20)));
        await service.Update(toPause.ContractId, Write(toPause, isPaused: true));

        var items = (await service.ListAsync(new ContractsQueryParams())).Items;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ContractId == active.ContractId && i.Status == ContractStatus.Active);
        Assert.Contains(items, i => i.ContractId == toPause.ContractId && i.Status == ContractStatus.Paused);
    }

    /// <summary>
    /// Sorting by status is by enum ORDINAL, so Paused (4) sorts after Archived (3). An ordinal is a
    /// wire and persistence contract and is never renumbered to buy a nicer sort; reading order for
    /// display is the client's registry.
    /// </summary>
    [Fact]
    public async Task List_SortedByStatus_PlacesPausedAfterArchived()
    {
        Assert.Equal(0, (int)ContractStatus.Active);
        Assert.Equal(1, (int)ContractStatus.Upcoming);
        Assert.Equal(2, (int)ContractStatus.Expired);
        Assert.Equal(3, (int)ContractStatus.Archived);
        Assert.Equal(4, (int)ContractStatus.Paused);

        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var toPause = await service.Create(Term(FixedToday.AddDays(-20)));
        await service.Update(toPause.ContractId, Write(toPause, isPaused: true));
        var toArchive = await service.Create(Term(FixedToday.AddDays(-100), FixedToday.AddDays(-1)));
        await service.Update(toArchive.ContractId, Write(toArchive, isArchived: true));
        var active = await service.Create(Term(FixedToday.AddDays(-5)));

        var items = (await service.ListAsync(
            new ContractsQueryParams { SortBy = ContractSortBy.Status })).Items;

        Assert.Equal(
            [active.ContractId, toArchive.ContractId, toPause.ContractId],
            items.Select(i => i.ContractId).ToArray());
    }
}
