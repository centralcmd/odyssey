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

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

    private ContractService CreateService(OdysseyContext context) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new Caps(), NullLogger<ContractService>.Instance);

    // SIGNED by default (issue #145): the signature layer outranks the date chain, so an unsigned
    // contract reads Draft/Ready and could never be paused at all. Pausing is about the date chain,
    // so the fixture clears the signature layer first. The one place the two interact — sign and
    // pause in a single PUT — is pinned in ContractSignatureTests.
    private static readonly DateTime SignedOn = FixedToday.AddDays(-200);

    private static NewContract Term(DateTime? start, DateTime? end = null) => new()
    {
        Name = "Agreement",
        Type = DtoContractType.Service,
        StartDate = start,
        EndDate = end,
        Ready = SignedOn.AddDays(-1),
        Signed = SignedOn,
    };

    private static NewContract OneOff(DateTime completion) => new()
    {
        Name = "One-off agreement",
        Type = DtoContractType.Service,
        CompletionDate = completion,
        Ready = SignedOn.AddDays(-1),
        Signed = SignedOn,
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
        // Carried forward, exactly as every first-party client call site must (issue #145): PUT is a
        // full replacement, so omitting these would clear both stamps and flip the contract to Draft
        // — which would make every pause assertion below fail for the wrong reason.
        Ready = from.Ready,
        Signed = from.Signed,
    };

    // ── Setting and clearing the stamp (AC 1–3) ───────────────────────────────

    [Fact]
    public async Task Pause_OnAnActiveContract_SetsTheStampAndDerivesPaused()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);

        var paused = await service.Update(created.ContractId, Write(created, isPaused: true), userId: null);

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
        var created = await service.Create(Term(FixedToday.AddDays(-10)), userId: null);

        var first = await service.Update(created.ContractId, Write(created, isPaused: true), userId: null);

        // A later clock — only a fresh stamp could move, so a moved value is the defect.
        var later = new ContractService(
            context, TestContextFactory.ContactLookup(journal),
            new FixedTimeProvider(FixedToday.AddDays(3)), new Caps(), NullLogger<ContractService>.Instance);
        var second = await later.Update(created.ContractId, Write(created, isPaused: true), userId: null);

        Assert.Equal(first!.Paused, second!.Paused);
        Assert.Equal(FixedToday, second.Paused);
    }

    [Fact]
    public async Task Resume_ClearsTheStampAndReturnsToActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10)), userId: null);
        await service.Update(created.ContractId, Write(created, isPaused: true), userId: null);

        var resumed = await service.Update(created.ContractId, Write(created, isPaused: false), userId: null);

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
            ContractStatus.Upcoming => await service.Create(Term(FixedToday.AddDays(5)), userId: null),
            _ => await service.Create(Term(FixedToday.AddDays(-100), FixedToday.AddDays(-1)), userId: null),
        };
        if (from == ContractStatus.Archived)
        {
            created = (await service.Update(created.ContractId, Write(created, isArchived: true), userId: null))!;
        }

        Assert.Equal(from, created.Status);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(created.ContractId, Write(created, isPaused: true, isArchived: from == ContractStatus.Archived), userId: null));

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
        var created = await service.Create(Term(FixedToday.AddDays(5)), userId: null);
        Assert.Equal(ContractStatus.Upcoming, created.Status);

        var paused = await service.Update(
            created.ContractId, Write(created, isPaused: true, startDate: FixedToday.AddDays(-5)), userId: null);

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
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);
        var paused = (await service.Update(created.ContractId, Write(created, isPaused: true), userId: null))!;

        // Re-save with an end date that has lapsed: the stamp is already set, so no guard fires.
        var expired = await service.Update(
            created.ContractId, Write(paused, isPaused: true, endDate: FixedToday.AddDays(-1)), userId: null);

        Assert.NotNull(expired!.Paused);
        Assert.Equal(ContractStatus.Expired, expired.Status);
    }

    /// <summary>Clearing is never refused — a guard on the way out is how a row gets stranded.</summary>
    [Fact]
    public async Task Resume_OnAnArchivedContractCarryingAStamp_IsAllowed()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);
        var paused = (await service.Update(created.ContractId, Write(created, isPaused: true), userId: null))!;

        // End it and archive it, keeping the pause stamp.
        var archived = (await service.Update(
            created.ContractId,
            Write(paused, isPaused: true, isArchived: true, endDate: FixedToday.AddDays(-1)), userId: null))!;
        Assert.Equal(ContractStatus.Archived, archived.Status);
        Assert.NotNull(archived.Paused);

        var cleared = await service.Update(
            created.ContractId, Write(archived, isPaused: false, isArchived: true, endDate: FixedToday.AddDays(-1)), userId: null);

        Assert.Null(cleared!.Paused);
        Assert.Equal(ContractStatus.Archived, cleared.Status);
    }


    /// <summary>
    /// §8's "Interaction with archive", from a contract that carries NEITHER stamp — the case every
    /// other combined-flag test here misses, because they all start from an already-paused contract
    /// and <see cref="ContractService"/>'s early return makes the pause half a no-op there.
    ///
    /// <para>
    /// Both guards run before either stamp is written, and the two directions are refused by
    /// <b>different</b> guards: a body asserting both on a contract that has ended is refused by the
    /// pause guard (it derives <c>Expired</c>, not <c>Active</c>), and one on a contract that has not
    /// is refused by the archive guard (archiving implies ended). There is no shape in which both
    /// stamps are set in a single write, and neither guard can be reached with the other's stamp
    /// already applied.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ArchiveAndPause_InOneWrite_IsRefused_FromEitherDirection_AndWritesNeitherStamp()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Direction 1 — the contract has ended in this same request. The archive guard is satisfied,
        // so the PAUSE guard is the one that refuses: an ended contract does not derive as Active.
        var ended = await service.Create(Term(FixedToday.AddDays(-30)), userId: null);
        var byPause = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(
                ended.ContractId,
                Write(ended, isPaused: true, isArchived: true, endDate: FixedToday.AddDays(-1)), userId: null));
        Assert.Equal("contract_pause_requires_active", byPause.Code);

        // Direction 2 — the contract is still running. Now the ARCHIVE guard refuses first, so the
        // pause guard is never reached and the refusal carries no pause code.
        var running = await service.Create(Term(FixedToday.AddDays(-30), FixedToday.AddDays(30)), userId: null);
        var byArchive = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(running.ContractId, Write(running, isPaused: true, isArchived: true), userId: null));
        Assert.NotEqual("contract_pause_requires_active", byArchive.Code);
        Assert.Contains("only be archived once it has ended", byArchive.Message, StringComparison.Ordinal);

        // Neither refusal wrote either stamp: the guards run before any mutation, so a rejected
        // compound write cannot leave one half applied.
        foreach (var id in new[] { ended.ContractId, running.ContractId })
        {
            var reread = await service.Get(id);
            Assert.Null(reread!.Paused);
            Assert.Null(reread.Archived);
        }
    }

    // ── Precedence: Archived > Upcoming > Expired > Paused > Active (AC 7–8) ──

    [Fact]
    public async Task Derivation_ATerminalFactOutranksTheTemporaryOne()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        async Task<ExistingContract> PausedThen(DateTime? start, DateTime? end, bool archive)
        {
            var c = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);
            var p = (await service.Update(c.ContractId, Write(c, isPaused: true), userId: null))!;
            return (await service.Update(
                c.ContractId,
                Write(p, isPaused: true, isArchived: archive, startDate: start, endDate: end), userId: null))!;
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
        var created = await service.Create(Term(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);
        var paused = (await service.Update(created.ContractId, Write(created, isPaused: true), userId: null))!;
        var archived = (await service.Update(
            created.ContractId,
            Write(paused, isPaused: true, isArchived: true, endDate: FixedToday.AddDays(-1)), userId: null))!;

        var unarchived = await service.Update(
            created.ContractId,
            Write(archived, isPaused: true, isArchived: false, endDate: FixedToday.AddDays(-1)), userId: null);

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

        var created = await service.Create(OneOff(FixedToday.AddDays(-1)), userId: null);
        Assert.Equal(ContractStatus.Active, created.Status);

        var paused = await service.Update(created.ContractId, Write(created, isPaused: true), userId: null);

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
        var created = await service.Create(OneOff(FixedToday.AddDays(5)), userId: null);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(created.ContractId, Write(created, isPaused: true), userId: null));

        Assert.Equal("contract_pause_requires_active", refusal.Code);
    }

    // ── List filtering and sorting (AC 9–10) ─────────────────────────────────

    [Fact]
    public async Task List_WithNoStatusFilter_StillIncludesPausedContracts()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var active = await service.Create(Term(FixedToday.AddDays(-10)), userId: null);
        var toPause = await service.Create(Term(FixedToday.AddDays(-20)), userId: null);
        await service.Update(toPause.ContractId, Write(toPause, isPaused: true), userId: null);

        var items = (await service.ListAsync(new ContractsQueryParams())).Items;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ContractId == active.ContractId && i.Status == ContractStatus.Active);
        Assert.Contains(items, i => i.ContractId == toPause.ContractId && i.Status == ContractStatus.Paused);
    }

    /// <summary>
    /// Sorting by status follows the shared LIFECYCLE rank, not the enum ordinal, so Paused sorts
    /// after Active and before Archived.
    ///
    /// <para>
    /// This <b>changed</b> with issue #145 and the change is deliberate. Appending <c>Draft = 5</c>
    /// and <c>Ready = 6</c> to a wire-contract enum would otherwise have sorted the two EARLIEST
    /// lifecycle states LAST, behind Archived — a defect introduced by that change rather than a
    /// pre-existing one, which is why fixing it was in its scope. The ordinals themselves are
    /// asserted below and are never renumbered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task List_SortedByStatus_FollowsTheLifecycleRank_NotTheOrdinal()
    {
        Assert.Equal(0, (int)ContractStatus.Active);
        Assert.Equal(1, (int)ContractStatus.Upcoming);
        Assert.Equal(2, (int)ContractStatus.Expired);
        Assert.Equal(3, (int)ContractStatus.Archived);
        Assert.Equal(4, (int)ContractStatus.Paused);

        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var toPause = await service.Create(Term(FixedToday.AddDays(-20)), userId: null);
        await service.Update(toPause.ContractId, Write(toPause, isPaused: true), userId: null);
        var toArchive = await service.Create(Term(FixedToday.AddDays(-100), FixedToday.AddDays(-1)), userId: null);
        await service.Update(toArchive.ContractId, Write(toArchive, isArchived: true), userId: null);
        var active = await service.Create(Term(FixedToday.AddDays(-5)), userId: null);

        var items = (await service.ListAsync(
            new ContractsQueryParams { SortBy = ContractSortBy.Status })).Items;

        // Active → Paused → Archived: the lifecycle rank. On the old ordinal sort this read
        // Active → Archived → Paused.
        Assert.Equal(
            [active.ContractId, toPause.ContractId, toArchive.ContractId],
            items.Select(i => i.ContractId).ToArray());
    }
}
