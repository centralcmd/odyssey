using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using DtoContractEventType = Odyssey.Dtos.Finance.ContractEventType;

namespace Odyssey.Core.Tests;

/// <summary>
/// The signature lifecycle (issue #145): the derivation layer, the three write guards, the two
/// amended guards, the roll-up exclusion, the lifecycle sort rank and the transition log line.
///
/// <para>
/// What most of this file is really pinning is <b>where the layer sits</b>. It is above the whole
/// date chain and short-circuits it, so an unsigned contract reads <c>Draft</c>/<c>Ready</c> whatever
/// its dates say. Written as a step inside the chain instead, an unsigned contract with a future
/// start would read <c>Upcoming</c> (asserting a commitment nobody made, and re-entering the upcoming
/// charges) and one whose end had passed would read <c>Expired</c> (describing a stalled negotiation
/// as an agreement that ran its course). Both are tested directly.
/// </para>
/// </summary>
public class ContractSignatureTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The acting user every signature transition is attributed to in its log line.</summary>
    private const string TestUserId = "test-user";

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

    private sealed class RecordingLogger : ILogger<ContractService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private ContractService CreateService(OdysseyContext context, ILogger<ContractService>? logger = null) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new Caps(), logger ?? NullLogger<ContractService>.Instance);

    private static readonly DateTime ReadyOn = FixedToday.AddDays(-30);
    private static readonly DateTime SignedOn = FixedToday.AddDays(-28);

    private static NewContract New(
        DateTime? start = null, DateTime? end = null, DateTime? ready = null, DateTime? signed = null,
        DateTime? completion = null, string name = "Agreement") => new()
        {
            Name = name,
            Type = DtoContractType.Service,
            StartDate = start,
            EndDate = end,
            CompletionDate = completion,
            Ready = ready,
            Signed = signed,
        };

    /// <summary>A write that re-states the contract as it stands, overriding only what is named.</summary>
    private static UpdateContract Write(
        ExistingContract from, DateTime? ready = null, DateTime? signed = null,
        bool isArchived = false, bool isPaused = false,
        DateTime? startDate = null, DateTime? endDate = null) => new()
        {
            Name = from.Name,
            Type = from.Type,
            StartDate = startDate ?? from.StartDate,
            EndDate = endDate ?? from.EndDate,
            CompletionDate = from.CompletionDate,
            IsArchived = isArchived,
            IsPaused = isPaused,
            Ready = ready,
            Signed = signed,
        };

    // ── The derivation (AC 1–6) ──────────────────────────────────────────────────

    /// <summary>AC 1 — both stamps omitted is the normal create path, and it is a Draft.</summary>
    [Fact]
    public async Task Create_WithNeitherStamp_IsADraft()
    {
        await using var context = TestContextFactory.Create();
        var created = await CreateService(context).Create(New(FixedToday.AddDays(-10)), userId: null);

        Assert.Null(created.Ready);
        Assert.Null(created.Signed);
        Assert.Equal(ContractStatus.Draft, created.Status);
    }

    /// <summary>AC 2 — a ready stamp with no signature reads Ready, whatever the term dates say.</summary>
    [Theory]
    [InlineData(-10, 10)]   // in force
    [InlineData(5, 100)]    // starts in the future
    [InlineData(-100, -1)]  // term already run out
    public async Task Ready_WithoutSigned_IsReady_WhateverTheTermDates(int startOffset, int endOffset)
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(New(FixedToday.AddDays(startOffset), FixedToday.AddDays(endOffset)), userId: null);

        var updated = await service.Update(created.ContractId, Write(created, ready: ReadyOn), userId: null);

        Assert.Equal(ReadyOn, updated!.Ready);
        Assert.Null(updated.Signed);
        Assert.Equal(ContractStatus.Ready, updated.Status);
    }

    /// <summary>AC 3 — signing an in-force term hands the row back to the date chain.</summary>
    [Fact]
    public async Task Signed_OnAnInForceTerm_IsActive()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(New(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);

        var updated = await service.Update(
            created.ContractId, Write(created, ready: ReadyOn, signed: SignedOn), userId: null);

        Assert.Equal(ContractStatus.Active, updated!.Status);
    }

    /// <summary>
    /// AC 4 — the layer sits ABOVE the date chain and short-circuits it. These are the two readings
    /// the placement exists to prevent: a term nobody has agreed to is not <c>Upcoming</c>, and a
    /// negotiation that stalled is abandoned rather than <c>Expired</c>.
    /// </summary>
    [Theory]
    [InlineData(5, 100, false, ContractStatus.Draft)]
    [InlineData(5, 100, true, ContractStatus.Ready)]
    [InlineData(-100, -1, false, ContractStatus.Draft)]
    [InlineData(-100, -1, true, ContractStatus.Ready)]
    public async Task Unsigned_OutranksTheDateChain(
        int startOffset, int endOffset, bool markedReady, ContractStatus expected)
    {
        await using var context = TestContextFactory.Create();
        var created = await CreateService(context).Create(
            New(FixedToday.AddDays(startOffset), FixedToday.AddDays(endOffset),
                ready: markedReady ? ReadyOn : null),
            userId: null);

        Assert.Equal(expected, created.Status);
    }

    /// <summary>AC 5 — Archived still outranks both signature states.</summary>
    [Fact]
    public async Task Archived_OutranksTheSignatureStates()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(New(FixedToday.AddDays(-10)), userId: null);

        // Archiving an unsigned contract is permitted by the widened rule (AC 9); that is what makes
        // this state reachable at all.
        var archived = await service.Update(created.ContractId, Write(created, isArchived: true), userId: null);

        Assert.Null(archived!.Signed);
        Assert.Equal(ContractStatus.Archived, archived.Status);
    }

    /// <summary>
    /// AC 6 — the pause and expiry readings of a SIGNED contract are exactly as they were. The
    /// signature layer returns before the date chain only when the contract is unsigned, so nothing
    /// below it may have shifted.
    /// </summary>
    [Fact]
    public async Task SignedContracts_KeepTheirPreExistingPauseAndExpiryReadings()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(
            New(FixedToday.AddDays(-10), FixedToday.AddDays(10), ReadyOn, SignedOn), userId: null);

        var paused = await service.Update(
            created.ContractId, Write(created, ReadyOn, SignedOn, isPaused: true), userId: null);
        Assert.Equal(ContractStatus.Paused, paused!.Status);

        // Moving the end date into the past: Expired outranks the retained pause stamp.
        var expired = await service.Update(
            created.ContractId,
            Write(created, ReadyOn, SignedOn, isPaused: true, endDate: FixedToday.AddDays(-1)),
            userId: null);

        Assert.Equal(ContractStatus.Expired, expired!.Status);
        Assert.NotNull(expired.Paused);
    }

    // ── The three guards (AC 7) ──────────────────────────────────────────────────

    /// <summary>
    /// AC 7 — each guard returns its documented code and names the offending field. Both write paths
    /// run the same helper, which is why POST and PUT are asserted together here.
    /// </summary>
    [Theory]
    [InlineData(null, -1, "contract_signed_requires_ready", nameof(UpdateContract.Signed))]
    [InlineData(-1, -2, "contract_signed_before_ready", nameof(UpdateContract.Signed))]
    [InlineData(1, null, "contract_signature_date_in_future", nameof(UpdateContract.Ready))]
    [InlineData(-1, 1, "contract_signature_date_in_future", nameof(UpdateContract.Signed))]
    public async Task EachGuard_Refuses_OnCreateAndOnUpdate_WithItsCodeAndField(
        int? readyOffset, int? signedOffset, string code, string field)
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var ready = readyOffset is { } r ? FixedToday.AddDays(r) : (DateTime?)null;
        var signed = signedOffset is { } g ? FixedToday.AddDays(g) : (DateTime?)null;

        var onCreate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(New(FixedToday.AddDays(-10), ready: ready, signed: signed), userId: null));
        Assert.Equal(code, onCreate.Code);
        Assert.Equal([field], onCreate.Errors!.Keys.ToArray());

        var existing = await service.Create(New(FixedToday.AddDays(-10)), userId: null);
        var onUpdate = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(existing.ContractId, Write(existing, ready, signed), userId: null));
        Assert.Equal(code, onUpdate.Code);
        Assert.Equal([field], onUpdate.Errors!.Keys.ToArray());
    }

    /// <summary>
    /// AC 7 — G3 compares at DATE granularity, not instant. A client clock a few minutes ahead of the
    /// server must not turn an ordinary "signed just now" into a 400; only a value dated tomorrow or
    /// later is refused.
    /// </summary>
    [Fact]
    public async Task ASignatureLaterTodayThanTheServerClock_IsAccepted()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // FixedToday is midnight, so this is "a few hours ahead of the server" on the same UTC day.
        var aheadOfTheClock = FixedToday.AddHours(6);
        var created = await service.Create(
            New(FixedToday.AddDays(-10), ready: aheadOfTheClock, signed: aheadOfTheClock), userId: null);

        Assert.Equal(aheadOfTheClock, created.Signed);
        Assert.Equal(ContractStatus.Active, created.Status);
    }

    /// <summary>
    /// AC 7 — G2 compares at INSTANT granularity, unlike G3. Both values come from the same body, so
    /// there is no clock to be skewed against: a signed one second before its own ready is a
    /// self-contradiction, and rounding it away to date granularity would silently accept it.
    /// </summary>
    [Fact]
    public async Task ASignedOneSecondBeforeItsOwnReady_IsRefused()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var ready = FixedToday.AddDays(-1).AddHours(12);
        var failure = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Create(
                New(FixedToday.AddDays(-10), ready: ready, signed: ready.AddSeconds(-1)), userId: null));

        Assert.Equal("contract_signed_before_ready", failure.Code);
    }

    /// <summary>
    /// AC 8 — clearing is NEVER refused, in any state. A guard on the way out is how a row gets
    /// stranded, which is the rule the pause guard already states.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]   // archived
    [InlineData(false, true)]   // paused
    public async Task Clearing_IsNeverRefused(bool archived, bool paused)
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(
            New(FixedToday.AddDays(-10), FixedToday.AddDays(10), ReadyOn, SignedOn), userId: null);

        if (archived || paused)
        {
            created = (await service.Update(
                created.ContractId,
                Write(created, ReadyOn, SignedOn, isArchived: archived, isPaused: paused,
                    endDate: archived ? FixedToday.AddDays(-1) : created.EndDate),
                userId: null))!;
        }

        // Clearing Signed alone leaves it Ready …
        var unsigned = await service.Update(
            created.ContractId,
            Write(created, ready: ReadyOn, isArchived: archived, isPaused: paused,
                endDate: created.EndDate),
            userId: null);
        Assert.Null(unsigned!.Signed);
        Assert.Equal(archived ? ContractStatus.Archived : ContractStatus.Ready, unsigned.Status);

        // … and clearing both leaves it a Draft.
        var draft = await service.Update(
            created.ContractId,
            Write(created, isArchived: archived, isPaused: paused, endDate: created.EndDate),
            userId: null);
        Assert.Null(draft!.Ready);
        Assert.Null(draft.Signed);
        Assert.Equal(archived ? ContractStatus.Archived : ContractStatus.Draft, draft.Status);
    }

    // ── The two amended guards (AC 9, AC 21) ─────────────────────────────────────

    /// <summary>
    /// AC 9 — pausing an unsigned contract is refused under the EXISTING code and message key: the
    /// pause guard now reads the signature-aware derivation, so a pause is never stored on a contract
    /// whose status the derivation would then never report.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pausing_AnUnsignedContract_IsRefused(bool markedReady)
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(
            New(FixedToday.AddDays(-10), FixedToday.AddDays(10), ready: markedReady ? ReadyOn : null),
            userId: null);

        var failure = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(
                created.ContractId,
                Write(created, ready: markedReady ? ReadyOn : null, isPaused: true),
                userId: null));

        Assert.Equal("contract_pause_requires_active", failure.Code);
        Assert.Contains(markedReady ? "Ready" : "Draft", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 21 — and the pair that pins WHY the pause guard reads the REQUEST's stamps rather than the
    /// stored ones. A single PUT that signs a Draft contract and pauses it in the same body must
    /// succeed; judged against the still-null STORED Signed it would be refused, for a contract the
    /// very same body makes Active. Asserted in the same test as its counterpart so the two cannot
    /// drift apart.
    /// </summary>
    [Fact]
    public async Task SignAndPause_InOneCall_Succeeds_WhileLeavingItUnsignedStillRefuses()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(New(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);
        Assert.Equal(ContractStatus.Draft, created.Status);

        var paused = await service.Update(
            created.ContractId, Write(created, ReadyOn, SignedOn, isPaused: true), userId: null);

        Assert.Equal(ContractStatus.Paused, paused!.Status);
        Assert.NotNull(paused.Paused);

        // The counterpart (AC 9), on a FRESH draft: the pause guard checks only the TRANSITION, so
        // re-using the row above would early-return on its existing stamp and assert nothing.
        var stillDraft = await service.Create(New(FixedToday.AddDays(-10), FixedToday.AddDays(10)), userId: null);
        var failure = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(stillDraft.ContractId, Write(stillDraft, ready: ReadyOn, isPaused: true), userId: null));
        Assert.Equal("contract_pause_requires_active", failure.Code);
    }

    /// <summary>
    /// AC 9 — the widened archive rule. An abandoned draft with NO end date is archivable, because
    /// abandoning a negotiation is the likeliest reason to archive one and the un-widened rule would
    /// strand it forever with no step the reader could take. A SIGNED contract that has not ended is
    /// still refused, so the widening is a branch rather than a removal.
    /// </summary>
    [Fact]
    public async Task Archiving_IsAllowedForAnUnsignedContract_AndStillRefusedForASignedRunningOne()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var draft = await service.Create(New(FixedToday.AddDays(-10)), userId: null);
        var archived = await service.Update(draft.ContractId, Write(draft, isArchived: true), userId: null);
        Assert.NotNull(archived!.Archived);

        var signed = await service.Create(
            New(FixedToday.AddDays(-10), FixedToday.AddDays(10), ReadyOn, SignedOn), userId: null);
        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(signed.ContractId, Write(signed, ReadyOn, SignedOn, isArchived: true), userId: null));
    }

    // ── Full-replacement semantics (AC 15) ───────────────────────────────────────

    /// <summary>
    /// AC 15 — a PUT that omits both fields entirely CLEARS them. That is the same convention
    /// <c>IsArchived</c>/<c>IsPaused</c> already have on this DTO, and it is what makes "clearing
    /// Signed is always allowed" expressible at all.
    /// </summary>
    [Fact]
    public async Task APutOmittingBothStamps_ClearsThem()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(
            New(FixedToday.AddDays(-10), FixedToday.AddDays(10), ReadyOn, SignedOn), userId: null);

        var updated = await service.Update(
            created.ContractId,
            new UpdateContract { Name = created.Name, Type = created.Type, StartDate = created.StartDate },
            userId: null);

        Assert.Null(updated!.Ready);
        Assert.Null(updated.Signed);
        Assert.Equal(ContractStatus.Draft, updated.Status);
    }

    // ── UTC round-trip (AC 19) ───────────────────────────────────────────────────

    /// <summary>
    /// AC 19 — both stamps funnel through the shared UTC normalization, so a client sending an offset
    /// stores the corresponding instant and an unspecified-kind value is read as UTC. Without it a
    /// stored value would be off by a timezone offset, which on a date-granularity guard is a whole
    /// day either way.
    /// </summary>
    [Fact]
    public async Task BothStamps_RoundTripAsUtc()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var local = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(2));
        var unspecified = new DateTime(2026, 6, 2, 9, 30, 0, DateTimeKind.Unspecified);

        var created = await service.Create(
            New(FixedToday.AddDays(-10), ready: local.LocalDateTime, signed: unspecified), userId: null);

        Assert.Equal(local.UtcDateTime, created.Ready);
        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), created.Signed);
        Assert.Equal(DateTimeKind.Utc, created.Signed!.Value.Kind);
    }

    // ── The lifecycle rank (AC 14) ───────────────────────────────────────────────

    /// <summary>
    /// AC 14 — the persisted/wire ordinals are unchanged, and the READING order is the lifecycle rank.
    /// Asserted directly against the enum so a renumbering fails the build: an ordinal is a wire and
    /// persistence contract, and the reason the rank exists at all is that Draft and Ready had to be
    /// appended.
    /// </summary>
    [Fact]
    public void TheOrdinalsAreUnchanged_AndTheReadingOrderIsTheLifecycleRank()
    {
        Assert.Equal(0, (int)ContractStatus.Active);
        Assert.Equal(1, (int)ContractStatus.Upcoming);
        Assert.Equal(2, (int)ContractStatus.Expired);
        Assert.Equal(3, (int)ContractStatus.Archived);
        Assert.Equal(4, (int)ContractStatus.Paused);
        Assert.Equal(5, (int)ContractStatus.Draft);
        Assert.Equal(6, (int)ContractStatus.Ready);

        Assert.Equal(
            [ContractStatus.Draft, ContractStatus.Ready, ContractStatus.Upcoming, ContractStatus.Active,
             ContractStatus.Paused, ContractStatus.Expired, ContractStatus.Archived],
            ContractStatusOrder.Order.ToArray());

        // The rank covers every declared member, and an unknown one sorts LAST rather than first.
        foreach (var status in Enum.GetValues<ContractStatus>())
        {
            Assert.InRange(ContractStatusOrder.Rank(status), 0, ContractStatusOrder.Order.Count - 1);
        }

        Assert.Equal(ContractStatusOrder.Order.Count, ContractStatusOrder.Rank((ContractStatus)99));

        Assert.True(ContractStatusOrder.IsUnsigned(ContractStatus.Draft));
        Assert.True(ContractStatusOrder.IsUnsigned(ContractStatus.Ready));
        Assert.False(ContractStatusOrder.IsUnsigned(ContractStatus.Active));
    }

    /// <summary>AC 14 — the list sort follows that rank end to end.</summary>
    [Fact]
    public async Task List_SortedByStatus_OrdersDraftAndReadyFirst()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var draft = await service.Create(New(FixedToday.AddDays(-10), name: "Draft"), userId: null);
        var ready = await service.Create(New(FixedToday.AddDays(-10), ready: ReadyOn, name: "Ready"), userId: null);
        var active = await service.Create(
            New(FixedToday.AddDays(-10), ready: ReadyOn, signed: SignedOn, name: "Active"), userId: null);

        var items = (await service.ListAsync(
            new ContractsQueryParams { SortBy = ContractSortBy.Status })).Items;

        Assert.Equal(
            [draft.ContractId, ready.ContractId, active.ContractId],
            items.Select(i => i.ContractId).ToArray());
    }

    // ── The status filter (AC 13) ────────────────────────────────────────────────

    /// <summary>AC 13 — the two new members are filterable, and return exactly the unsigned set.</summary>
    [Fact]
    public async Task StatusFilter_CoversTheTwoNewMembers()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var draft = await service.Create(New(FixedToday.AddDays(-10), name: "Draft"), userId: null);
        var ready = await service.Create(New(FixedToday.AddDays(-10), ready: ReadyOn, name: "Ready"), userId: null);
        await service.Create(New(FixedToday.AddDays(-10), ready: ReadyOn, signed: SignedOn, name: "Active"), userId: null);

        var unsigned = (await service.ListAsync(new ContractsQueryParams
        {
            Statuses = [ContractStatus.Draft, ContractStatus.Ready],
        })).Items;

        Assert.Equal(
            new HashSet<Guid> { draft.ContractId, ready.ContractId },
            unsigned.Select(i => i.ContractId).ToHashSet());
    }

    // ── The transition log line (AC 22) ──────────────────────────────────────────

    /// <summary>
    /// AC 22 — one structured line per transition, over POST as well as PUT, naming the stamp, whether
    /// it was set or cleared, the contract id and the acting user. A contract created already-signed
    /// emits the line with a RESOLVED user id, not "(unknown)": a Signed transition can happen on
    /// create, so plumbing the user through only the update path would leave those unattributed.
    ///
    /// <para>
    /// The line carries no contract name, party name or other free text — every value is an opaque id,
    /// a fixed literal or a timestamp, exactly as the party line's are.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryTransition_EmitsOneAttributedLine_OnCreateAndOnUpdate()
    {
        await using var context = TestContextFactory.Create();
        var log = new RecordingLogger();
        var service = CreateService(context, log);

        var created = await service.Create(
            New(FixedToday.AddDays(-10), ready: ReadyOn, signed: SignedOn, name: "Fibre broadband"),
            userId: TestUserId);

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains(log.Lines, l => l.Contains("Ready set", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("Signed set", StringComparison.Ordinal));
        Assert.All(log.Lines, l =>
        {
            Assert.Contains(created.ContractId.ToString(), l, StringComparison.Ordinal);
            Assert.Contains(TestUserId, l, StringComparison.Ordinal);
            Assert.DoesNotContain("(unknown)", l, StringComparison.Ordinal);
            // No free text: the contract's own name never reaches the line.
            Assert.DoesNotContain("Fibre broadband", l, StringComparison.Ordinal);
        });

        // A write that changes NEITHER stamp emits nothing.
        log.Lines.Clear();
        await service.Update(created.ContractId, Write(created, ReadyOn, SignedOn), userId: TestUserId);
        Assert.Empty(log.Lines);

        // Clearing one emits one line, naming the clear.
        log.Lines.Clear();
        await service.Update(created.ContractId, Write(created, ready: ReadyOn), userId: TestUserId);
        var cleared = Assert.Single(log.Lines);
        Assert.Contains("Signed cleared", cleared, StringComparison.Ordinal);
        Assert.Contains(TestUserId, cleared, StringComparison.Ordinal);
    }

    // ── Independence from the event log (AC 18) ──────────────────────────────────

    /// <summary>
    /// AC 18, first direction — setting the stamp creates no <c>ContractEvent</c>. Two writers onto
    /// one fact is a reconciliation problem this feature deliberately does not take on.
    /// </summary>
    [Fact]
    public async Task SettingTheStamp_CreatesNoEvent()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(
            New(FixedToday.AddDays(-10), ready: ReadyOn, signed: SignedOn), userId: null);

        Assert.Empty(context.ContractEvents.Where(e => e.ContractId == created.ContractId));
    }

    /// <summary>
    /// AC 18, the OTHER direction — creating a <c>Signed</c> event leaves <c>Contract.Signed</c> null,
    /// and the contract still derives as <c>Draft</c>.
    ///
    /// <para>
    /// Worth its own test rather than being assumed from the one above: the two are written by
    /// different services against the same row, so "no coupling" is two independent claims. This is
    /// also the direction a later change is most likely to break, since "the user logged a Signed
    /// event, so the contract is signed" is a tempting convenience — and taking it would put two
    /// writers on one fact, which is exactly what the non-goal forbids.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreatingASignedEvent_LeavesTheStampNull()
    {
        await using var context = TestContextFactory.Create();
        var contracts = CreateService(context);
        var events = new ContractEventService(context, new FixedTimeProvider(FixedToday));

        var created = await contracts.Create(New(FixedToday.AddDays(-10)), userId: null);
        Assert.Equal(ContractStatus.Draft, created.Status);

        var logged = await events.CreateAsync(
            created.ContractId,
            new NewContractEvent
            {
                Title = "Countersigned copy returned",
                Type = DtoContractEventType.Signed,
                OccurredAt = FixedToday.AddDays(-1),
            },
            "user-jane");

        Assert.NotNull(logged);

        var reloaded = await contracts.Get(created.ContractId);
        Assert.Null(reloaded!.Ready);
        Assert.Null(reloaded.Signed);
        Assert.Equal(ContractStatus.Draft, reloaded.Status);
    }
}
