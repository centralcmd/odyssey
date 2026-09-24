using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using Context = Odyssey.Context;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using EventType = Odyssey.Context.ContractEventType;
using EventSource = Odyssey.Context.ContractEventSource;
using ContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Contract event automation (issue #154): transition detection, the generated-text catalogue and the
/// structured-log safety net, all at the tier that can exercise them.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs on the EF InMemory provider. That is a deliberate claim rather than a
/// convenience: <c>ChangeTracker</c> and <c>EntityEntry.OriginalValues</c> — which §8.7's term seam
/// rests on — are provider-agnostic core EF, unlike the relational-only
/// <c>ExecuteDeleteAsync</c>/<c>ExecuteUpdateAsync</c> CLAUDE.md warns about. What InMemory cannot
/// verify is the <b>atomicity</b> itself, since it honours neither transactions nor an execution
/// strategy; that lives in <c>Odyssey.IntegrationTests</c> against real MariaDB (AC 25).
/// </para>
/// </remarks>
public class ContractEventAutomationTests
{
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
    private const string TestUserId = "test-user";

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    /// <summary>Captures the formatted line so the no-free-text assertions can read it back.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private ContractService Contracts(
        OdysseyContext context, ILogger<ContractService>? logger = null, ISystemSettingsLookup? caps = null) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            caps ?? new FakeSystemSettingsLookup(), logger ?? NullLogger<ContractService>.Instance);

    private static TermService Terms(
        OdysseyContext context, ILogger<TermService>? logger = null, ISystemSettingsLookup? caps = null) =>
        new(context, new FixedTimeProvider(FixedToday), caps ?? new FakeSystemSettingsLookup(),
            logger ?? NullLogger<TermService>.Instance);

    private static NewContract New(
        DateTime? start = null, DateTime? end = null, DateTime? ready = null, DateTime? signed = null) => new()
        {
            Name = "Agreement",
            Type = DtoContractType.Rental,
            StartDate = start ?? FixedToday.AddDays(-30),
            EndDate = end,
            Ready = ready,
            Signed = signed,
        };

    /// <summary>A write that re-states the contract as it stands, overriding only what is named.</summary>
    private static UpdateContract Write(
        ExistingContract from, bool isPaused = false, bool isArchived = false,
        DateTime? ready = null, DateTime? signed = null, DateTime? endDate = null) => new()
        {
            Name = from.Name,
            Type = from.Type,
            StartDate = from.StartDate,
            EndDate = endDate ?? from.EndDate,
            CompletionDate = from.CompletionDate,
            IsPaused = isPaused,
            IsArchived = isArchived,
            Ready = ready,
            Signed = signed,
        };

    private static List<ContractEvent> EventsOf(OdysseyContext context, Guid contractId) =>
        context.ContractEvents
            .Where(e => e.ContractId == contractId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToList();

    private static ContractEvent SingleSystemEvent(OdysseyContext context, Guid contractId)
    {
        var events = EventsOf(context, contractId);
        var single = Assert.Single(events);
        Assert.Equal(EventSource.System, single.Source);
        return single;
    }

    // ── Stamp transitions (AC 1–10) ──────────────────────────────────────────────

    /// <summary>AC 1 — pausing an active contract records exactly one <c>Paused</c> system event.</summary>
    [Fact]
    public async Task Pausing_RecordsOnePausedSystemEvent()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(created.ContractId, Write(created, isPaused: true,
            ready: created.Ready, signed: created.Signed), TestUserId);

        var recorded = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Paused, recorded.Type);
        Assert.Equal("Contract paused", recorded.Title);
        Assert.Equal(TestUserId, recorded.CreatedByUserId);
        // Notes is the user's own field and is always null on a system event.
        Assert.Null(recorded.Notes);
    }

    /// <summary>
    /// AC 2 — a replayed, identical <c>PUT</c> keeps the ORIGINAL stamp and adds no second event.
    /// Without transition detection, a client that re-saves an unchanged form would add a row on every
    /// save.
    /// </summary>
    [Fact]
    public async Task RepeatingThePauseWrite_AddsNoSecondEvent()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);
        ClearEvents(context, created.ContractId);

        var write = Write(created, isPaused: true, ready: created.Ready, signed: created.Signed);
        var paused = await service.Update(created.ContractId, write, TestUserId);
        var stamp = paused!.Paused;

        await service.Update(created.ContractId, write, TestUserId);

        var again = await service.Get(created.ContractId);
        Assert.Equal(stamp, again!.Paused);
        Assert.Single(EventsOf(context, created.ContractId));
    }

    /// <summary>AC 3 — clearing the pause records exactly one <c>Unpaused</c> event.</summary>
    [Fact]
    public async Task Unpausing_RecordsOneUnpausedEvent()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);
        await service.Update(created.ContractId,
            Write(created, isPaused: true, ready: created.Ready, signed: created.Signed), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(created.ContractId,
            Write(created, isPaused: false, ready: created.Ready, signed: created.Signed), TestUserId);

        var recorded = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Unpaused, recorded.Type);
        Assert.Equal("Contract resumed", recorded.Title);
    }

    /// <summary>AC 4 — the same pair holds for the archive stamp.</summary>
    [Fact]
    public async Task ArchivingAndRestoring_RecordOneEventEach()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        // Unsigned, so EnsureArchivable permits archiving a draft whatever its dates say.
        var created = await service.Create(New(), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(created.ContractId, Write(created, isArchived: true), TestUserId);
        var archived = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Archived, archived.Type);
        Assert.Equal("Contract archived", archived.Title);

        ClearEvents(context, created.ContractId);
        await service.Update(created.ContractId, Write(created, isArchived: false), TestUserId);
        var restored = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Unarchived, restored.Type);
        Assert.Equal("Contract restored from the archive", restored.Title);
    }

    /// <summary>AC 5 — setting and clearing the ready stamp.</summary>
    [Fact]
    public async Task SettingAndClearingReady_RecordOneEventEach()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(New(), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(created.ContractId, Write(created, ready: FixedToday.AddDays(-5)), TestUserId);
        var ready = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Ready, ready.Type);
        Assert.Equal("Marked ready for signature", ready.Title);

        ClearEvents(context, created.ContractId);
        await service.Update(created.ContractId, Write(created), TestUserId);
        var unready = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Unready, unready.Type);
    }

    /// <summary>
    /// AC 6 — signing reuses ordinal 0. There is deliberately no new <c>Signed</c> member, and clearing
    /// the stamp is a distinct <c>Unsigned</c>.
    /// </summary>
    [Fact]
    public async Task SigningAndClearing_RecordSignedThenUnsigned()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(New(), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(created.ContractId,
            Write(created, ready: FixedToday.AddDays(-5), signed: FixedToday.AddDays(-4)), TestUserId);
        var events = EventsOf(context, created.ContractId);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Type == EventType.Ready);
        var signed = Assert.Single(events, e => e.Type == EventType.Signed);
        Assert.Equal(0, (int)signed.Type);
        Assert.Equal("Contract signed", signed.Title);

        ClearEvents(context, created.ContractId);
        await service.Update(created.ContractId, Write(created, ready: FixedToday.AddDays(-5)), TestUserId);
        var cleared = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.Unsigned, cleared.Type);
        Assert.Equal("Signed date cleared", cleared.Title);
    }

    /// <summary>
    /// AC 7 — a RE-DATE writes nothing. <c>Ready</c> and <c>Signed</c> are full replacements carrying a
    /// caller-supplied moment, so non-null → <em>different</em> non-null is possible; that is a
    /// correction to the record, not a signing.
    /// </summary>
    [Fact]
    public async Task ReDatingAnExistingSignature_RecordsNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(created.ContractId,
            Write(created, ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-10)), TestUserId);

        Assert.Empty(EventsOf(context, created.ContractId));
    }

    /// <summary>AC 8 — one write that both signs and archives records exactly two events, sharing a
    /// <c>CreatedAtUtc</c>.</summary>
    [Fact]
    public async Task OneWriteThatSignsAndArchives_RecordsExactlyTwoEvents()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var created = await service.Create(New(end: FixedToday.AddDays(-1)), TestUserId);
        ClearEvents(context, created.ContractId);

        await service.Update(
            created.ContractId,
            Write(created, isArchived: true, ready: FixedToday.AddDays(-5), signed: FixedToday.AddDays(-4),
                endDate: FixedToday.AddDays(-1)),
            TestUserId);

        var events = EventsOf(context, created.ContractId);
        // Ready, Signed and Archived: three stamps moved, so three events.
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Type == EventType.Signed);
        Assert.Contains(events, e => e.Type == EventType.Archived);
        Assert.Single(events.Select(e => e.CreatedAtUtc).Distinct());
    }

    /// <summary>
    /// AC 9 — <c>POST</c> fires the same detector against an all-null "before", and records NO event
    /// describing the creation itself (Non-Goal 6).
    /// </summary>
    [Fact]
    public async Task CreatingAnAlreadySignedContract_RecordsTwoEventsAndNothingForTheCreation()
    {
        await using var context = TestContextFactory.Create();
        var created = await Contracts(context).Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);

        var events = EventsOf(context, created.ContractId);
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(EventSource.System, e.Source);
            Assert.Equal(TestUserId, e.CreatedByUserId);
        });
        // Ordinal order, not reading order: Signed is 0 and Ready is 11.
        Assert.Equal(
            [EventType.Signed, EventType.Ready],
            events.Select(e => e.Type).Order().ToArray());
    }

    /// <summary>AC 10 — a refused write leaves the event count unchanged: staging happens after
    /// validation and the save never runs.</summary>
    [Fact]
    public async Task ARefusedPause_LeavesTheEventCountUnchanged()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        // Never signed, so it derives Draft and EnsurePausable refuses the pause.
        var created = await service.Create(New(), TestUserId);
        ClearEvents(context, created.ContractId);

        var failure = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(created.ContractId, Write(created, isPaused: true), TestUserId));

        Assert.Equal("contract_pause_requires_active", failure.Code);
        Assert.Empty(EventsOf(context, created.ContractId));
    }

    /// <summary>
    /// AC 26 — the §8.4 clamp. A same-day signature must not produce an event <c>occurredAt</c> ahead
    /// of the write, because <c>ContractEventService</c>'s own bound is an INSTANT plus a 60-second
    /// tolerance while the stamp is validated at DATE granularity. Clamping is the fix; taking
    /// <c>now</c> unconditionally is not, because a contract signed last March must date its event
    /// last March.
    /// </summary>
    [Fact]
    public async Task ASameDaySignature_IsClampedToTheWriteInstant_AndAPastOneIsNot()
    {
        await using var context = TestContextFactory.Create();
        // The clock sits at 09:00 while the stamp claims 23:00 the same day — the shape a client in a
        // later timezone produces, and one NormalizeSignature accepts because the DATE is not future.
        var at9am = FixedToday.AddHours(9);
        var service = new ContractService(
            context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(at9am),
            new FakeSystemSettingsLookup(), NullLogger<ContractService>.Instance);

        var sameDay = await service.Create(
            New(ready: FixedToday.AddHours(22), signed: FixedToday.AddHours(23)), TestUserId);
        var sameDayEvents = EventsOf(context, sameDay.ContractId);
        Assert.All(sameDayEvents, e =>
        {
            Assert.True(e.OccurredAt <= e.CreatedAtUtc,
                $"occurredAt {e.OccurredAt:O} must not be later than createdAtUtc {e.CreatedAtUtc:O}.");
            Assert.True(e.OccurredAt <= at9am + ContractEventService.FutureTolerance);
        });

        var lastMarch = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var backdated = await service.Create(
            New(ready: lastMarch.AddDays(-1), signed: lastMarch), TestUserId);
        var signedEvent = Assert.Single(
            EventsOf(context, backdated.ContractId), e => e.Type == EventType.Signed);
        Assert.Equal(lastMarch, signedEvent.OccurredAt);
    }

    // ── Party transitions (AC 11–14, 19–20) ──────────────────────────────────────

    /// <summary>AC 11 and AC 19 — the role, never the target's NAME, and no GUID.</summary>
    [Fact]
    public async Task AddingAParty_RecordsPartyAdded_NamingTheRoleAndNotTheContact()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var contactId = await SeedContactAsync("Acme AS");
        // Employment, because ContractPartyRoleMatrix refuses Employer on a Rental contract.
        var created = await service.Create(New() with { Type = DtoContractType.Employment }, TestUserId);
        ClearEvents(context, created.ContractId);

        await service.AddParty(
            created.ContractId,
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer },
            TestUserId);

        var recorded = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.PartyAdded, recorded.Type);
        Assert.Equal("Employer added as a party", recorded.Title);
        Assert.Null(recorded.Description);

        // §7.3 — a name written into an event title is a REVOCATION-PROOF copy of personal data: it
        // survives the contact's archival, its deletion and an erasure request, and stays reachable by
        // anyone holding contracts.read forever. The id is excluded on the same reasoning.
        Assert.DoesNotContain("Acme", recorded.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(contactId.ToString(), recorded.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC 20 — archiving the contact afterwards changes nothing about the event's text, and the event
    /// stays readable. The point is the converse of AC 19: because nothing was baked in, nothing has to
    /// be revoked.
    /// </summary>
    [Fact]
    public async Task ArchivingTheContactAfterwards_ChangesNothingAboutTheEventText()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var contactId = await SeedContactAsync("Acme AS");
        var created = await service.Create(New() with { Type = DtoContractType.Employment }, TestUserId);
        await service.AddParty(
            created.ContractId,
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Employer },
            TestUserId);
        var before = SingleSystemEvent(context, created.ContractId).Title;

        var contact = await journal.Contacts.FirstAsync(c => c.ContactId == contactId);
        contact.Archived = FixedToday;
        await journal.SaveChangesAsync();

        Assert.Equal(before, SingleSystemEvent(context, created.ContractId).Title);
    }

    /// <summary>AC 12 — detaching a party records exactly one <c>PartyRemoved</c>.</summary>
    [Fact]
    public async Task DeletingAParty_RecordsPartyRemoved()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var contactId = await SeedContactAsync("Acme AS");
        var created = await service.Create(New(), TestUserId);
        var party = await service.AddParty(
            created.ContractId,
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Landlord },
            TestUserId);
        ClearEvents(context, created.ContractId);

        Assert.True(await service.DeleteParty(created.ContractId, party!.ContractPartyId, TestUserId));

        var recorded = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.PartyRemoved, recorded.Type);
        Assert.Equal("Landlord removed as a party", recorded.Title);
    }

    /// <summary>
    /// AC 13 — repointing the link at a different contact records a <c>PartyRemoved</c> then a
    /// <c>PartyAdded</c>. The row is updated in place and the party "stays one party" (#121), but from
    /// the agreement's point of view one party left and another joined — recording nothing would let a
    /// party vanish from the tiles with a silent log.
    /// </summary>
    [Fact]
    public async Task RepointingAPartyAtADifferentContact_RecordsRemovedThenAdded()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var first = await SeedContactAsync("Acme AS");
        var second = await SeedContactAsync("Globex AS");
        var created = await service.Create(New(), TestUserId);
        var party = await service.AddParty(
            created.ContractId,
            new ContractPartyRequest { ContactId = first, Role = ContractPartyRole.Landlord },
            TestUserId);
        ClearEvents(context, created.ContractId);

        await service.UpdateParty(
            created.ContractId, party!.ContractPartyId,
            new ContractPartyRequest { ContactId = second, Role = ContractPartyRole.Landlord },
            TestUserId);

        var events = EventsOf(context, created.ContractId);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Type == EventType.PartyRemoved);
        Assert.Contains(events, e => e.Type == EventType.PartyAdded);
        Assert.All(events, e => Assert.Equal(EventSource.System, e.Source));
    }

    /// <summary>
    /// AC 14 — a re-dating records NOTHING. It changes how an existing party is described, not who is
    /// party to the agreement; <c>LogPartyWrite</c> already covers it in the application log.
    /// </summary>
    [Fact]
    public async Task ReDatingAParty_RecordsNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = Contracts(context);
        var contactId = await SeedContactAsync("Acme AS");
        var created = await service.Create(New(), TestUserId);
        var party = await service.AddParty(
            created.ContractId,
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Landlord },
            TestUserId);
        ClearEvents(context, created.ContractId);

        await service.UpdateParty(
            created.ContractId, party!.ContractPartyId,
            new ContractPartyRequest
            {
                ContactId = contactId,
                Role = ContractPartyRole.Landlord,
                FromDate = FixedToday.AddDays(-10),
            },
            TestUserId);

        Assert.Empty(EventsOf(context, created.ContractId));
    }

    // ── Term writes (AC 15–18, 29) ───────────────────────────────────────────────

    private static NewTerm Rent(decimal value, DateTime effectiveFrom, string? label = "Monthly rent") => new()
    {
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
        Interval = Odyssey.Dtos.Finance.Interval.Monthly,
        EffectiveFrom = effectiveFrom,
    };

    /// <summary>AC 15 and AC 17 — one <c>TermChanged</c> per term verb, each attributed to the caller.</summary>
    [Fact]
    public async Task EachTermVerb_RecordsOneTermChangedEventAttributedToTheCaller()
    {
        await using var context = TestContextFactory.Create();
        var created = await Contracts(context).Create(New(), TestUserId);
        var terms = Terms(context);
        ClearEvents(context, created.ContractId);

        var term = await terms.CreateForContract(
            created.ContractId, Rent(14500m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), TestUserId);
        var added = SingleSystemEvent(context, created.ContractId);
        Assert.Equal(EventType.TermChanged, added.Type);
        Assert.Equal("Term added (Monthly rent)", added.Title);
        Assert.Equal("USD 14500 per month effective 1 January 2026.", added.Description);
        Assert.Equal(TestUserId, added.CreatedByUserId);

        ClearEvents(context, created.ContractId);
        Assert.True(await terms.UpdateForContract(
            created.ContractId, term.TermId,
            Rent(15000m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), TestUserId));
        var changed = SingleSystemEvent(context, created.ContractId);
        Assert.Equal("Term changed (Monthly rent)", changed.Title);
        Assert.Equal("USD 15000 per month effective 1 January 2026.", changed.Description);
        Assert.Equal(TestUserId, changed.CreatedByUserId);

        ClearEvents(context, created.ContractId);
        Assert.True(await terms.DeleteForContract(created.ContractId, term.TermId, TestUserId));
        var removed = SingleSystemEvent(context, created.ContractId);
        Assert.Equal("Term removed (Monthly rent)", removed.Title);
        Assert.Equal("Effective 1 January 2026 — removed.", removed.Description);
        Assert.Equal(TestUserId, removed.CreatedByUserId);
    }

    /// <summary>
    /// AC 18 — a term write refused by the cap leaves the event count unchanged and logs no line. The
    /// reason is structural: in <c>CreateFor</c> the cap check throws before <c>context.Terms.Add</c>
    /// and therefore before the delegate.
    /// </summary>
    [Fact]
    public async Task ATermWriteRefusedByTheCap_RecordsNothingAndLogsNothing()
    {
        await using var context = TestContextFactory.Create();
        var caps = new FakeSystemSettingsLookup
        {
            Caps = new FinanceRequestCaps(25, 50, MaxTermsPerContract: 1, 1000),
        };
        var created = await Contracts(context, caps: caps).Create(New(), TestUserId);
        var log = new RecordingLogger<TermService>();
        var terms = Terms(context, log, caps);

        await terms.CreateForContract(
            created.ContractId, Rent(1m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "First"),
            TestUserId);
        ClearEvents(context, created.ContractId);
        log.Lines.Clear();

        await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            terms.CreateForContract(
                created.ContractId, Rent(2m, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "Second"),
                TestUserId));

        Assert.Empty(EventsOf(context, created.ContractId));
        Assert.Empty(log.Lines);
    }

    /// <summary>
    /// AC 29 — a maximum-length term label never aborts the write it is describing. The literal
    /// "exactly 256 characters" of the acceptance criterion is <b>unreachable through the API</b>:
    /// <c>Term.Label</c> is capped at <see cref="Odyssey.Dtos.Finance.TermLabel.MaxLength"/> (64), and
    /// the longest title the catalogue can build from one is well under 256. So this asserts what the
    /// rule actually guarantees — the write succeeds and the generated strings are within the entity's
    /// bounds — and <see cref="Catalogue_TruncatesAnOverLongTitleToExactlyTheBound"/> pins the
    /// truncation behaviour itself against an input that can reach the cap.
    /// </summary>
    [Fact]
    public async Task ATermWithAMaximumLengthLabel_Succeeds_AndItsEventStaysWithinBounds()
    {
        await using var context = TestContextFactory.Create();
        var created = await Contracts(context).Create(New(), TestUserId);
        ClearEvents(context, created.ContractId);

        var label = new string('L', Odyssey.Dtos.Finance.TermLabel.MaxLength);
        await Terms(context).CreateForContract(
            created.ContractId,
            Rent(14500m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), label),
            TestUserId);

        var recorded = SingleSystemEvent(context, created.ContractId);
        Assert.Contains(label, recorded.Title, StringComparison.Ordinal);
        Assert.True(recorded.Title.Length <= ContractEventCatalogue.MaxTitleLength);
        Assert.True((recorded.Description?.Length ?? 0) <= ContractEventCatalogue.MaxDescriptionLength);
    }

    /// <summary>
    /// The truncation rule itself: a generated title must never be able to fail validation and abort
    /// the write it is describing, so the catalogue truncates rather than throwing — to EXACTLY the
    /// bound, since the ellipsis is one character.
    /// </summary>
    [Fact]
    public void Catalogue_TruncatesAnOverLongTitleToExactlyTheBound()
    {
        var over = new string('x', ContractEventCatalogue.MaxTitleLength + 50);

        var bounded = ContractEventCatalogue.Bound(over, ContractEventCatalogue.MaxTitleLength);

        Assert.Equal(ContractEventCatalogue.MaxTitleLength, bounded.Length);
        Assert.EndsWith("…", bounded, StringComparison.Ordinal);
        Assert.Equal(
            ContractEventCatalogue.MaxTitleLength,
            ContractEventCatalogue.Bound(
                new string('y', ContractEventCatalogue.MaxTitleLength),
                ContractEventCatalogue.MaxTitleLength).Length);
    }

    // ── The structured-log safety net (AC 30–32) ─────────────────────────────────

    /// <summary>
    /// AC 30 — pausing and then unpausing emits two <c>Information</c> lines naming the stamp, both
    /// values and the acting user. Before issue #154 the log covered the two SIGNATURE stamps only, so
    /// pausing a contract and deleting the event that recorded it would have left zero trace.
    /// </summary>
    [Fact]
    public async Task PausingThenUnpausing_EmitsTwoStampLines()
    {
        await using var context = TestContextFactory.Create();
        var log = new RecordingLogger<ContractService>();
        var service = Contracts(context, log);
        var created = await service.Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);
        log.Lines.Clear();

        await service.Update(created.ContractId,
            Write(created, isPaused: true, ready: created.Ready, signed: created.Signed), TestUserId);
        await service.Update(created.ContractId,
            Write(created, isPaused: false, ready: created.Ready, signed: created.Signed), TestUserId);

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("Paused set", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("(none) ->", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("Paused cleared", log.Lines[1], StringComparison.Ordinal);
        Assert.Contains("-> (none)", log.Lines[1], StringComparison.Ordinal);
        Assert.All(log.Lines, line =>
        {
            Assert.Contains(created.ContractId.ToString(), line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(TestUserId, line, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// AC 31 — the three term verbs each emit one line, in the shapes that make the line useful.
    /// </summary>
    /// <remarks>
    /// The update's "before" half is the load-bearing one: once the resulting <c>TermChanged</c> event
    /// is deleted, this line is the only record of what the term used to say. A line reading
    /// <c>X -&gt; X</c> means <c>OriginalValues</c> was not read from a TRACKED entity and is a
    /// failure, not a pass — so the two halves are asserted to differ. The create's <c>(none)</c> is
    /// asserted explicitly because it is HARDCODED rather than derived: an entity in the <c>Added</c>
    /// state has no prior row, and a later refactor unifying the three cases would regress it silently.
    /// </remarks>
    [Fact]
    public async Task TheThreeTermVerbs_EachEmitOneLine_WithTheRightHalvesPopulated()
    {
        await using var context = TestContextFactory.Create();
        var created = await Contracts(context).Create(New(), TestUserId);
        var log = new RecordingLogger<TermService>();
        var terms = Terms(context, log);

        var term = await terms.CreateForContract(
            created.ContractId, Rent(14500m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), TestUserId);
        var addedLine = Assert.Single(log.Lines);
        Assert.Contains("Contract term added", addedLine, StringComparison.Ordinal);
        Assert.Contains("(none) (none) from (none) ->", addedLine, StringComparison.Ordinal);
        Assert.Contains("14500 USD", addedLine, StringComparison.Ordinal);

        log.Lines.Clear();
        Assert.True(await terms.UpdateForContract(
            created.ContractId, term.TermId,
            Rent(15000m, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)), TestUserId));
        var changedLine = Assert.Single(log.Lines);
        Assert.Contains("Contract term changed", changedLine, StringComparison.Ordinal);
        // BOTH halves, and they must differ — the whole point of reading OriginalValues.
        Assert.Contains("14500 USD from 2026-01-01", changedLine, StringComparison.Ordinal);
        Assert.Contains("15000 USD from 2026-02-01", changedLine, StringComparison.Ordinal);

        log.Lines.Clear();
        Assert.True(await terms.DeleteForContract(created.ContractId, term.TermId, TestUserId));
        var removedLine = Assert.Single(log.Lines);
        Assert.Contains("Contract term removed", removedLine, StringComparison.Ordinal);
        Assert.Contains("15000 USD from 2026-02-01", removedLine, StringComparison.Ordinal);
        Assert.Contains("-> (none) (none) from (none)", removedLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 32 — no log line from the stamp or term families carries a contact name, an account name, or
    /// the term's user-supplied <c>Label</c>. Ids, closed enums, dates and money amounts only: a
    /// <c>Guid</c> or a <c>decimal</c> cannot carry the CR/LF a forged log line would need, which is
    /// what makes them safe to record verbatim.
    /// </summary>
    [Fact]
    public async Task NoLogLine_CarriesANameOrTheTermLabel()
    {
        await using var context = TestContextFactory.Create();
        const string ContactName = "Acme AS";
        const string TermLabelText = "Monthly rent for the annexe";

        var contractLog = new RecordingLogger<ContractService>();
        var termLog = new RecordingLogger<TermService>();
        var service = Contracts(context, contractLog);
        var contactId = await SeedContactAsync(ContactName);
        var created = await service.Create(
            New(ready: FixedToday.AddDays(-20), signed: FixedToday.AddDays(-19)), TestUserId);

        await service.AddParty(
            created.ContractId,
            new ContractPartyRequest { ContactId = contactId, Role = ContractPartyRole.Landlord },
            TestUserId);
        await service.Update(created.ContractId,
            Write(created, isPaused: true, ready: created.Ready, signed: created.Signed), TestUserId);

        var terms = Terms(context, termLog);
        var term = await terms.CreateForContract(
            created.ContractId,
            Rent(14500m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TermLabelText),
            TestUserId);
        await terms.UpdateForContract(
            created.ContractId, term.TermId,
            Rent(15000m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TermLabelText), TestUserId);
        await terms.DeleteForContract(created.ContractId, term.TermId, TestUserId);

        var lines = contractLog.Lines.Concat(termLog.Lines).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(ContactName, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Acme", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TermLabelText, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("annexe", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Agreement", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The currency slot is reduced to three ASCII letters or <c>(none)</c> at the LOG SITE, so a
    /// stored code outside that shape never reaches an operator's log — the <c>cs/log-forging</c>
    /// alert CodeQL raised on the first cut of this line.
    /// </summary>
    /// <remarks>
    /// The write path already refuses an unsupported currency, so this state is unreachable through
    /// the API today; it is reached here by writing the row directly, which is exactly the case the
    /// guard exists for — a row written by an earlier build, or by a later change to a validator three
    /// call frames away. Asserting through the validator would test the validator instead.
    /// </remarks>
    [Fact]
    public async Task ATermCurrencyOutsideTheThreeLetterShape_NeverReachesTheLogLine()
    {
        await using var context = TestContextFactory.Create();
        var created = await Contracts(context).Create(New(), TestUserId);
        var log = new RecordingLogger<TermService>();
        var terms = Terms(context, log);

        var term = await terms.CreateForContract(
            created.ContractId, Rent(14500m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), TestUserId);

        // Written straight onto the row, bypassing the write path's currency validation.
        var stored = await context.Terms.FirstAsync(t => t.TermId == term.TermId);
        stored.CurrencyCode = "U\r\nS";
        await context.SaveChangesAsync();
        log.Lines.Clear();

        Assert.True(await terms.DeleteForContract(created.ContractId, term.TermId, TestUserId));

        var line = Assert.Single(log.Lines);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.Contains("(none)", line, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Empties the log so an assertion reads only what the write under test produced. Creating a
    /// contract may itself record transitions (AC 9), which is the behaviour, not noise.
    /// </summary>
    private static void ClearEvents(OdysseyContext context, Guid contractId)
    {
        context.ContractEvents.RemoveRange(
            context.ContractEvents.Where(e => e.ContractId == contractId).ToList());
        context.SaveChanges();
    }

    private async Task<Guid> SeedContactAsync(string legalName)
    {
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = legalName.ToLowerInvariant(),
            Type = Odyssey.Dtos.ContactType.Organization,
            OrganizationDetails = new() { LegalName = legalName },
        };
        journal.Contacts.Add(contact);
        await journal.SaveChangesAsync();
        return contact.ContactId;
    }
}
