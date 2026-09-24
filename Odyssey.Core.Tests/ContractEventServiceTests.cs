using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;
using DtoContractEventType = Odyssey.Dtos.Finance.ContractEventType;

namespace Odyssey.Core.Tests;

/// <summary>
/// The contract event log's service layer (issue #138): the writes, the server-stamped fields, the
/// future-date bound, and the list's search / filter / sort / window behaviour.
/// </summary>
/// <remarks>
/// The claim gates and the attribution label live at the API edge and are covered in
/// <c>Odyssey.Api.Tests</c>; the two foreign keys are covered in <c>Odyssey.IntegrationTests</c>,
/// because the EF InMemory provider this tier runs on enforces none.
/// </remarks>
public class ContractEventServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext context = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private ContractEventService CreateService() => new(context, new FixedTimeProvider(FixedNow));

    private async Task<Guid> SeedContractAsync(string name = "Maple St lease")
    {
        var contract = new Contract
        {
            Name = name,
            Type = ContextContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = FixedNow,
        };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static NewContractEvent New(
        string title = "Emailed landlord about the rent increase",
        DtoContractEventType type = DtoContractEventType.EmailSent,
        string? description = "Asked for the CPI basis in writing.",
        string? notes = "Chase on the 21st if no reply.",
        DateTime? occurredAt = null) => new()
    {
        Type = type,
        Title = title,
        Description = description,
        Notes = notes,
        OccurredAt = occurredAt ?? new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
    };

    private static UpdateContractEvent Update(
        string title = "Edited title",
        DtoContractEventType type = DtoContractEventType.Other,
        string? description = null,
        string? notes = null,
        DateTime? occurredAt = null) => new()
    {
        Type = type,
        Title = title,
        Description = description,
        Notes = notes,
        OccurredAt = occurredAt ?? new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Utc),
    };

    // ── Create ───────────────────────────────────────────────────────────────

    /// <summary>AC 1 — the row is created and both provenance fields come from the server.</summary>
    [Fact]
    public async Task Create_StampsTheAuthorAndTheServerClock()
    {
        var contractId = await SeedContractAsync();

        var created = await CreateService().CreateAsync(contractId, New(), "user-jane");

        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Event.ContractEventId);
        Assert.Equal(contractId, created.Event.ContractId);
        Assert.Equal(FixedNow, created.Event.CreatedAtUtc);
        Assert.Equal("user-jane", created.AuthorId);

        // The serialized DTO carries a LABEL slot and no id — the controller fills it. Nothing here
        // may put a raw user id on the wire.
        Assert.Null(created.Event.CreatedBy);
    }

    [Fact]
    public async Task Create_OnAMissingContract_ReturnsNull()
    {
        Assert.Null(await CreateService().CreateAsync(Guid.NewGuid(), New(), "user-jane"));
    }

    /// <summary>An event with a title alone is an ordinary, complete event (§8.2).</summary>
    [Fact]
    public async Task Create_WithNeitherDescriptionNorNotes_Succeeds()
    {
        var contractId = await SeedContractAsync();

        var created = await CreateService()
            .CreateAsync(contractId, New(description: null, notes: null), "user-jane");

        Assert.NotNull(created);
        Assert.Null(created!.Event.Description);
        Assert.Null(created.Event.Notes);
    }

    /// <summary>
    /// <c>[StringLength(MinimumLength = 1)]</c> accepts a string of spaces, so the service is the only
    /// thing that can reject one (§8.1).
    /// </summary>
    [Fact]
    public async Task Create_WithAWhitespaceOnlyTitle_IsRejectedAsEmpty()
    {
        var contractId = await SeedContractAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => CreateService().CreateAsync(contractId, New(title: "   "), "user-jane"));

        Assert.Equal(400, ex.StatusCode);
        Assert.True(ex.Errors!.ContainsKey("title"));
    }

    [Fact]
    public async Task Create_WithABlankDescriptionOrNotes_StoresThemAsAbsent()
    {
        var contractId = await SeedContractAsync();

        var created = await CreateService()
            .CreateAsync(contractId, New(description: "   ", notes: "\t"), "user-jane");

        Assert.Null(created!.Event.Description);
        Assert.Null(created.Event.Notes);
    }

    /// <summary>An omitted type binds to the default rather than failing (§8.4).</summary>
    [Fact]
    public async Task Create_WithNoTypeSupplied_DefaultsToOther()
    {
        var contractId = await SeedContractAsync();

        var created = await CreateService().CreateAsync(
            contractId,
            new NewContractEvent { Title = "Something happened", OccurredAt = FixedNow.AddDays(-1) },
            "user-jane");

        Assert.Equal(DtoContractEventType.Other, created!.Event.Type);
    }

    /// <summary>An anonymous or id-less caller leaves the attribution null rather than storing blank.</summary>
    [Fact]
    public async Task Create_WithNoCallerId_LeavesTheAttributionNull()
    {
        var contractId = await SeedContractAsync();

        var created = await CreateService().CreateAsync(contractId, New(), "   ");

        Assert.Null(created!.AuthorId);
    }

    /// <summary>
    /// §8.6 — archival hides a contract from the default list and is fully reversible; it is not a
    /// lock, and the party, term and file writes already permit it.
    /// </summary>
    [Fact]
    public async Task Create_OnAnArchivedContract_IsPermitted()
    {
        var contractId = await SeedContractAsync();
        var contract = await context.Contracts.FindAsync(contractId);
        contract!.Archived = FixedNow;
        await context.SaveChangesAsync();

        Assert.NotNull(await CreateService().CreateAsync(contractId, New(), "user-jane"));
    }

    // ── The future bound (§8.3, AC 8) ────────────────────────────────────────

    [Fact]
    public async Task Create_WithAnOccurredAtBeyondTheTolerance_Is400KeyedToTheField()
    {
        var contractId = await SeedContractAsync();

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => CreateService()
            .CreateAsync(contractId, New(occurredAt: FixedNow.AddSeconds(61)), "user-jane"));

        Assert.Equal(400, ex.StatusCode);
        Assert.True(ex.Errors!.ContainsKey("occurredAt"));
    }

    /// <summary>
    /// The tolerance exists so an ordinary "now" from a client whose clock runs slightly fast is not
    /// spuriously rejected. Both ends of it are pinned, or a later change to one number could pass.
    /// </summary>
    [Theory]
    [InlineData(-60)]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(60)]
    public async Task Create_WithAnOccurredAtInsideTheTolerance_IsAccepted(int offsetSeconds)
    {
        var contractId = await SeedContractAsync();

        var created = await CreateService()
            .CreateAsync(contractId, New(occurredAt: FixedNow.AddSeconds(offsetSeconds)), "user-jane");

        Assert.NotNull(created);
    }

    [Fact]
    public async Task Create_WithAPastOccurredAt_IsAccepted()
    {
        var contractId = await SeedContractAsync();

        Assert.NotNull(await CreateService()
            .CreateAsync(contractId, New(occurredAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), "user-jane"));
    }

    /// <summary>
    /// A body without a <c>Z</c> binds with <c>DateTimeKind.Unspecified</c>. Read as local time on a
    /// server east of UTC, a "now" would land in the future and be refused; the service pins the
    /// interpretation to UTC instead of leaving it to the host's zone (§8.3).
    /// </summary>
    [Fact]
    public async Task Create_WithAnUnspecifiedKindOccurredAt_IsTreatedAsUtc()
    {
        var contractId = await SeedContractAsync();
        var unspecified = new DateTime(2026, 6, 14, 9, 31, 0, DateTimeKind.Unspecified);

        var created = await CreateService().CreateAsync(contractId, New(occurredAt: unspecified), "user-jane");

        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), created!.Event.OccurredAt);
    }

    // ── Update (§5.3, AC 3 & 5) ──────────────────────────────────────────────

    /// <summary>
    /// AC 3 — a <c>PUT</c> is a FULL replacement: an omitted description or notes clears it and an
    /// omitted type resets it to <c>Other</c>. This is the opposite of <c>UpdateContract</c>.
    /// </summary>
    [Fact]
    public async Task Update_ClearsEveryOmittedField()
    {
        var contractId = await SeedContractAsync();
        var service = CreateService();
        var created = await service.CreateAsync(contractId, New(type: DtoContractEventType.TermChanged), "user-jane");

        var updated = await service.UpdateAsync(
            contractId, created!.Event.ContractEventId, Update(title: "Just the title now"));

        Assert.Equal("Just the title now", updated!.Event.Title);
        Assert.Null(updated.Event.Description);
        Assert.Null(updated.Event.Notes);
        Assert.Equal(DtoContractEventType.Other, updated.Event.Type);
    }

    /// <summary>AC 5 — editing an event must never rewrite who recorded it, or when.</summary>
    [Fact]
    public async Task Update_LeavesTheAuthorAndCreatedAtAlone()
    {
        var contractId = await SeedContractAsync();
        var service = CreateService();
        var created = await service.CreateAsync(contractId, New(), "user-jane");

        // A later clock, so a service that DID re-stamp would visibly move the timestamp.
        var later = new ContractEventService(context, new FixedTimeProvider(FixedNow.AddDays(3)));
        var updated = await later.UpdateAsync(contractId, created!.Event.ContractEventId, Update());

        Assert.Equal(created.Event.CreatedAtUtc, updated!.Event.CreatedAtUtc);
        Assert.Equal("user-jane", updated.AuthorId);
    }

    /// <summary>AC 11 — an event id on another contract is not found, never silently written.</summary>
    [Fact]
    public async Task Update_AnEventBelongingToAnotherContract_ReturnsNullAndWritesNothing()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        var otherContractId = await SeedContractAsync("Other agreement");
        var created = await service.CreateAsync(contractId, New(), "user-jane");

        var updated = await service.UpdateAsync(
            otherContractId, created!.Event.ContractEventId, Update(title: "Hijacked"));

        Assert.Null(updated);
        var stored = await context.ContractEvents.FindAsync(created.Event.ContractEventId);
        Assert.Equal(created.Event.Title, stored!.Title);
    }

    [Fact]
    public async Task Update_WithAFutureOccurredAt_Is400()
    {
        var contractId = await SeedContractAsync();
        var service = CreateService();
        var created = await service.CreateAsync(contractId, New(), "user-jane");

        await Assert.ThrowsAsync<DomainValidationException>(() => service.UpdateAsync(
            contractId, created!.Event.ContractEventId, Update(occurredAt: FixedNow.AddDays(1))));
    }

    // ── Delete (AC 6, 11) ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTheEvent()
    {
        var contractId = await SeedContractAsync();
        var service = CreateService();
        var created = await service.CreateAsync(contractId, New(), "user-jane");

        Assert.True(await service.DeleteAsync(contractId, created!.Event.ContractEventId));
        Assert.Empty(context.ContractEvents);
    }

    [Fact]
    public async Task Delete_AnEventBelongingToAnotherContract_ReturnsFalseAndKeepsIt()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        var otherContractId = await SeedContractAsync("Other agreement");
        var created = await service.CreateAsync(contractId, New(), "user-jane");

        Assert.False(await service.DeleteAsync(otherContractId, created!.Event.ContractEventId));
        Assert.Single(context.ContractEvents);
    }

    [Fact]
    public async Task Delete_AMissingEvent_ReturnsFalse()
    {
        var contractId = await SeedContractAsync();

        Assert.False(await CreateService().DeleteAsync(contractId, Guid.NewGuid()));
    }

    // ── List (AC 2) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task List_OnAMissingContract_ReturnsNull()
    {
        Assert.Null(await CreateService().ListAsync(Guid.NewGuid(), new ContractEventsQueryParams()));
    }

    [Fact]
    public async Task List_OnAContractWithNoEvents_IsAnEmptyPageAndNotADefect()
    {
        var contractId = await SeedContractAsync();

        var page = await CreateService().ListAsync(contractId, new ContractEventsQueryParams());

        Assert.NotNull(page);
        Assert.Empty(page!.Page.Items);
        Assert.Equal(0, page.Page.TotalCount);
    }

    [Fact]
    public async Task List_DefaultsToNewestFirstAndIsScopedToTheContract()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        var otherContractId = await SeedContractAsync("Other agreement");
        await service.CreateAsync(contractId, New(title: "Oldest", occurredAt: Day(1)), "u");
        await service.CreateAsync(contractId, New(title: "Newest", occurredAt: Day(3)), "u");
        await service.CreateAsync(contractId, New(title: "Middle", occurredAt: Day(2)), "u");
        await service.CreateAsync(otherContractId, New(title: "Elsewhere", occurredAt: Day(4)), "u");

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams());

        Assert.Equal(["Newest", "Middle", "Oldest"], page!.Page.Items.Select(i => i.Title));
        Assert.Equal(3, page.Page.TotalCount);
    }

    /// <summary>
    /// AC 2 — the search term spans all three free-text fields. <c>Notes</c> in particular: it is
    /// behind the same claim and returned to the same callers, so excluding it from search would make
    /// it harder to find without making it any less readable (§4.1).
    /// </summary>
    [Theory]
    [InlineData("landlord", "Titled")]
    [InlineData("CPI basis", "Described")]
    [InlineData("chase on the 21st", "Noted")]
    public async Task List_Search_SpansTitleDescriptionAndNotes(string term, string expected)
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        await service.CreateAsync(contractId, New(
            title: "Titled", description: "Emailed the landlord", notes: null, occurredAt: Day(1)), "u");
        await service.CreateAsync(contractId, New(
            title: "Described", description: "Asked for the CPI basis in writing", notes: null, occurredAt: Day(2)), "u");
        await service.CreateAsync(contractId, New(
            title: "Noted", description: null, notes: "Chase on the 21st if no reply", occurredAt: Day(3)), "u");

        // "landlord" appears in the first row's DESCRIPTION, so the title-only case is covered by the
        // other two rows not matching.
        var page = await service.ListAsync(contractId, new ContractEventsQueryParams { Search = term });

        Assert.Equal([expected], page!.Page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task List_FiltersByType()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        await service.CreateAsync(contractId, New(title: "A", type: DtoContractEventType.Signed, occurredAt: Day(1)), "u");
        await service.CreateAsync(contractId, New(title: "B", type: DtoContractEventType.Renewed, occurredAt: Day(2)), "u");
        await service.CreateAsync(contractId, New(title: "C", type: DtoContractEventType.EmailSent, occurredAt: Day(3)), "u");

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams
        {
            Types = [DtoContractEventType.Signed, DtoContractEventType.EmailSent],
        });

        Assert.Equal(["C", "A"], page!.Page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task List_FiltersByTheOccurredAtWindow_Inclusively()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        await service.CreateAsync(contractId, New(title: "Before", occurredAt: Day(1)), "u");
        await service.CreateAsync(contractId, New(title: "Inside", occurredAt: Day(2)), "u");
        await service.CreateAsync(contractId, New(title: "After", occurredAt: Day(3)), "u");

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams { From = Day(2), To = Day(2) });

        Assert.Equal(["Inside"], page!.Page.Items.Select(i => i.Title));
    }

    [Theory]
    [InlineData(ContractEventSortBy.Title, null, new[] { "Alpha", "Bravo", "Charlie" })]
    [InlineData(ContractEventSortBy.Title, SortDirection.Desc, new[] { "Charlie", "Bravo", "Alpha" })]
    [InlineData(ContractEventSortBy.OccurredAt, null, new[] { "Charlie", "Bravo", "Alpha" })]
    [InlineData(ContractEventSortBy.OccurredAt, SortDirection.Asc, new[] { "Alpha", "Bravo", "Charlie" })]
    public async Task List_HonoursEverySortKeyAndBothDirections(
        ContractEventSortBy sortBy, SortDirection? sortDir, string[] expected)
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        await service.CreateAsync(contractId, New(title: "Alpha", occurredAt: Day(1)), "u");
        await service.CreateAsync(contractId, New(title: "Bravo", occurredAt: Day(2)), "u");
        await service.CreateAsync(contractId, New(title: "Charlie", occurredAt: Day(3)), "u");

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams
        {
            SortBy = sortBy,
            SortDir = sortDir,
        });

        Assert.Equal(expected, page!.Page.Items.Select(i => i.Title));
    }

    /// <summary>
    /// Sorting by type orders by the stored ORDINAL, which is the wire contract — not by the member
    /// name, which would reorder silently if a member were renamed.
    /// </summary>
    [Fact]
    public async Task List_SortByType_OrdersByOrdinal()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        await service.CreateAsync(contractId, New(title: "Other", type: DtoContractEventType.Other, occurredAt: Day(1)), "u");
        await service.CreateAsync(contractId, New(title: "Signed", type: DtoContractEventType.Signed, occurredAt: Day(2)), "u");
        await service.CreateAsync(contractId, New(title: "Renewed", type: DtoContractEventType.Renewed, occurredAt: Day(3)), "u");

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams { SortBy = ContractEventSortBy.Type });

        Assert.Equal(["Signed", "Renewed", "Other"], page!.Page.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task List_HonoursTheOffsetLimitWindowAndReportsTheFullCount()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        for (var day = 1; day <= 5; day++)
        {
            await service.CreateAsync(contractId, New(title: $"Day {day}", occurredAt: Day(day)), "u");
        }

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams { Offset = 1, Limit = 2 });

        Assert.Equal(["Day 4", "Day 3"], page!.Page.Items.Select(i => i.Title));
        Assert.Equal(5, page.Page.TotalCount);
        Assert.Equal(1, page.Page.Offset);
        Assert.Equal(2, page.Page.Limit);
    }

    /// <summary>
    /// The author ids come out of the same window read the DTOs do, keyed by event id — that is what
    /// lets the controller label a whole page without a second query.
    /// </summary>
    [Fact]
    public async Task List_CarriesTheAuthorIdBesideEachEventAndNeverOnIt()
    {
        var service = CreateService();
        var contractId = await SeedContractAsync();
        var jane = await service.CreateAsync(contractId, New(title: "By Jane", occurredAt: Day(1)), "user-jane");
        var sam = await service.CreateAsync(contractId, New(title: "By Sam", occurredAt: Day(2)), "user-sam");

        var page = await service.ListAsync(contractId, new ContractEventsQueryParams());

        Assert.Equal("user-jane", page!.AuthorIds[jane!.Event.ContractEventId]);
        Assert.Equal("user-sam", page.AuthorIds[sam!.Event.ContractEventId]);
        Assert.All(page.Page.Items, item => Assert.Null(item.CreatedBy));
    }

    // ── Events change nothing about the contract (§4.2, AC 16) ───────────────

    /// <summary>
    /// AC 16 — an event is a user's free-form record. Making one authoritative over derived state
    /// would mean a typo in a log entry silently changes what the contract IS.
    /// </summary>
    [Theory]
    [InlineData(DtoContractEventType.Terminated)]
    [InlineData(DtoContractEventType.Renewed)]
    public async Task AnEvent_DoesNotAffectTheContractsDerivedStatusOrItsDates(DtoContractEventType type)
    {
        var contractId = await SeedContractAsync();
        var before = await context.Contracts.FindAsync(contractId);
        var (startBefore, endBefore) = (before!.StartDate, before.EndDate);

        await CreateService().CreateAsync(contractId, New(type: type), "user-jane");

        context.ChangeTracker.Clear();
        var after = await context.Contracts.FindAsync(contractId);
        Assert.Equal(startBefore, after!.StartDate);
        Assert.Equal(endBefore, after.EndDate);
        Assert.Null(after.Archived);
        Assert.Null(after.Paused);
    }

    /// <summary>
    /// <c>CreatedAtUtc</c> needs its own arrangement: every row in the shared one is stamped from the
    /// same fixed clock, so its order would fall to the id tiebreaker and the assertion would pass or
    /// fail on a GUID. Here the three rows are written by three clocks, deliberately in the opposite
    /// order to <c>OccurredAt</c> so the two keys cannot be confused for one another.
    /// </summary>
    [Theory]
    [InlineData(SortDirection.Asc, new[] { "Charlie", "Bravo", "Alpha" })]
    [InlineData(null, new[] { "Alpha", "Bravo", "Charlie" })]
    public async Task List_SortByCreatedAtUtc_OrdersByWhenTheRowWasRecorded(
        SortDirection? sortDir, string[] expected)
    {
        var contractId = await SeedContractAsync();
        await WriteAtAsync(contractId, "Alpha", recordedOn: 3, occurredOn: 1);
        await WriteAtAsync(contractId, "Bravo", recordedOn: 2, occurredOn: 2);
        await WriteAtAsync(contractId, "Charlie", recordedOn: 1, occurredOn: 3);

        var page = await CreateService().ListAsync(contractId, new ContractEventsQueryParams
        {
            SortBy = ContractEventSortBy.CreatedAtUtc,
            SortDir = sortDir,
        });

        Assert.Equal(expected, page!.Page.Items.Select(i => i.Title));
    }

    /// <summary>
    /// Writes one event with a distinct recording clock. The occurrence dates sit two months before
    /// every recording clock, so a row recorded early never trips the future bound on a late occurrence
    /// — the point of the arrangement is the two orders differing, not the dates being close.
    /// </summary>
    private Task WriteAtAsync(Guid contractId, string title, int recordedOn, int occurredOn) =>
        new ContractEventService(context, new FixedTimeProvider(Day(recordedOn).AddHours(6)))
            .CreateAsync(
                contractId,
                New(title: title, occurredAt: new DateTime(2026, 1, occurredOn, 10, 0, 0, DateTimeKind.Utc)),
                "u");

    private static DateTime Day(int day) => new(2026, 3, day, 10, 0, 0, DateTimeKind.Utc);
}
