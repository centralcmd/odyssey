using Odyssey.Core;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using BillingInterval = Odyssey.Dtos.Finance.BillingInterval;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// Unit coverage for SubscriptionService (issue #293): validation (contact / currency / dates),
/// ExternalId normalisation + search, the paused/archived toggle semantics (service-owned stamps,
/// preserve-on-repeat-true, clear-on-false), and the server-side list contract (search / interval /
/// derived lifecycle-status filters + allowlisted sort + pagination).
/// </summary>
public class SubscriptionServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    // Contact now lives in OdysseyContext; one journal per test backs both the seeded contacts and the
    // IContactLookup the service resolves through (xUnit creates a fresh test-class instance per test).
    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private SubscriptionService CreateService(OdysseyContext context) =>
        new(context, TestContextFactory.ContactLookup(journal), new FakeSystemSettingsLookup(),
            new FixedTimeProvider(FixedNow));

    private async Task<Guid> SeedContact(DateTime? archived = null)
    {
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "NETFLIX",
            Type = ContactType.Organization,
            Archived = archived,
            OrganizationDetails = new() { LegalName = "Netflix" },
        };
        journal.Contacts.Add(contact);
        await journal.SaveChangesAsync();
        return contact.ContactId;
    }

    private static NewSubscription NewSub(Guid? contactId = null, string name = "Streaming") => new()
    {
        Name = name,
        ContactId = contactId,
        StartDate = new DateOnly(2026, 1, 1),
        Amount = 9.99m,
        CurrencyCode = "USD",
        Interval = BillingInterval.Monthly,
        FirstBillingDate = new DateOnly(2026, 1, 15),
    };

    private static UpdateSubscription UpdateFrom(NewSubscription source, bool paused = false, bool archived = false) => new()
    {
        Name = source.Name,
        ExternalId = source.ExternalId,
        ContactId = source.ContactId,
        StartDate = source.StartDate,
        EndDate = source.EndDate,
        Amount = source.Amount,
        CurrencyCode = source.CurrencyCode,
        Interval = source.Interval,
        IntervalCount = source.IntervalCount,
        FirstBillingDate = source.FirstBillingDate,
        Notes = source.Notes,
        Paused = paused,
        Archived = archived,
    };

    // ── Create / read ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NoContact_Persists_And_IsRetrievable()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var created = await service.Create(NewSub());

        Assert.NotEqual(Guid.Empty, created.SubscriptionId);
        Assert.Null(created.Contact);
        var fetched = await service.Get(created.SubscriptionId);
        Assert.NotNull(fetched);
        Assert.Equal("Streaming", fetched!.Name);
        Assert.Equal(FixedNow, fetched.CreatedAtUtc);
    }

    [Fact]
    public async Task Create_WithContact_LinksMinimalReference()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contactId = await SeedContact();

        var created = await service.Create(NewSub(contactId));

        Assert.NotNull(created.Contact);
        Assert.Equal(contactId, created.Contact!.ContactId);
        Assert.Equal("Netflix", created.Contact.Name);
        Assert.Equal(ContactType.Organization, created.Contact.Type);
    }

    [Fact]
    public async Task Create_UnknownContact_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(NewSub(Guid.NewGuid())));
    }

    [Fact]
    public async Task Create_ArchivedContact_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contactId = await SeedContact(archived: FixedNow);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(NewSub(contactId)));
    }

    [Fact]
    public async Task Create_LowercaseCurrency_IsNormalized()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var request = NewSub();
        request.CurrencyCode = "usd";
        var created = await service.Create(request);

        Assert.Equal("USD", created.CurrencyCode);
    }

    [Fact]
    public async Task Create_UnsupportedCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var request = NewSub();
        request.CurrencyCode = "ZZZ";
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task Create_TrimsExternalId_And_StoresEmptyAsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var withValue = NewSub();
        withValue.ExternalId = "  MBR-12345  ";
        var created = await service.Create(withValue);
        Assert.Equal("MBR-12345", created.ExternalId);

        var blank = NewSub(name: "Blank");
        blank.ExternalId = "   ";
        var createdBlank = await service.Create(blank);
        Assert.Null(createdBlank.ExternalId);
    }

    [Fact]
    public async Task Create_DefaultsIntervalCountToOne_And_PersistsMultiplier()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var defaulted = await service.Create(NewSub());
        Assert.Equal(1, defaulted.IntervalCount);

        var quarterly = NewSub(name: "Quarterly");
        quarterly.Interval = BillingInterval.Monthly;
        quarterly.IntervalCount = 3;
        var created = await service.Create(quarterly);
        Assert.Equal(3, created.IntervalCount);
        Assert.Equal(3, (await service.Get(created.SubscriptionId))!.IntervalCount);
    }

    [Fact]
    public async Task Create_IntervalCountBelowOne_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var request = NewSub();
        request.IntervalCount = 0;
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    // ── Date rules ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_EndBeforeStart_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var request = NewSub();
        request.EndDate = request.StartDate.AddDays(-1);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(request));
    }

    [Fact]
    public async Task Create_EndEqualsStart_IsAccepted()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var request = NewSub();
        request.EndDate = request.StartDate;
        var created = await service.Create(request);
        Assert.Equal(request.StartDate, created.EndDate);
    }

    [Fact]
    public async Task Create_FirstBillingDate_MayPrecedeStart()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var request = NewSub();
        request.FirstBillingDate = request.StartDate.AddMonths(-1);
        var created = await service.Create(request);
        Assert.Equal(request.FirstBillingDate, created.FirstBillingDate);
    }

    // ── Derived next billing (the record card's Next billing tile) ──────────────

    [Fact]
    public async Task Get_NextBillingDate_IsDerivedFromTheAnchor()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Monthly anchored on the 15th, FixedNow = 2026-06-15 → the occurrence on today itself.
        var created = await service.Create(NewSub());
        Assert.Equal(new DateOnly(2026, 6, 15), created.NextBillingDate);
    }

    [Fact]
    public async Task Get_NextBillingDate_IsNull_WhenNothingIsDue()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Paused: the emptiness IS the derivation, not a missing value — the same suppression the
        // summary's upcoming-renewals rollup applies, so the two can never disagree.
        var pausedSub = await service.Create(NewSub(name: "Paused"));
        var paused = await service.Update(pausedSub.SubscriptionId, UpdateFrom(NewSub(name: "Paused"), paused: true));
        Assert.Null(paused!.NextBillingDate);

        // Ended: the term has lapsed.
        var endedReq = NewSub(name: "Ended");
        endedReq.EndDate = new DateOnly(2026, 5, 1);
        Assert.Null((await service.Create(endedReq)).NextBillingDate);

        // Archived (which, being ordered after Ended, carries a lapsed end date of its own).
        var archivedReq = NewSub(name: "Archived");
        var archivedSub = await service.Create(archivedReq);
        archivedReq.EndDate = new DateOnly(2026, 5, 1);
        var archived = await service.Update(archivedSub.SubscriptionId, UpdateFrom(archivedReq, archived: true));
        Assert.Null(archived!.NextBillingDate);

        // A next occurrence past the end date is no occurrence at all: monthly on the 15th with the
        // term ending on the 20th of the current month yields the 15th, but ending on the 16th of
        // last month leaves nothing due.
        var boundedReq = NewSub(name: "Bounded");
        boundedReq.EndDate = new DateOnly(2026, 6, 20);
        Assert.Equal(new DateOnly(2026, 6, 15), (await service.Create(boundedReq)).NextBillingDate);
    }

    // ── Pause / archive toggles ─────────────────────────────────────────────────

    [Fact]
    public async Task Update_Pause_StampsThenPreservesThenClears()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        var paused = await service.Update(created.SubscriptionId, UpdateFrom(NewSub(), paused: true));
        Assert.Equal(FixedNow, paused!.Paused);
        Assert.Null(paused.Archived);

        // A second pause=true preserves the original stamp even though the value is unchanged.
        var repaused = await service.Update(created.SubscriptionId, UpdateFrom(NewSub(), paused: true));
        Assert.Equal(FixedNow, repaused!.Paused);

        var resumed = await service.Update(created.SubscriptionId, UpdateFrom(NewSub(), paused: false));
        Assert.Null(resumed!.Paused);
    }

    [Fact]
    public async Task Update_Archive_RequiresAnEndedTerm()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        // Live: archiving is refused — the lifecycle is ordered, so Archived implies Ended.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(created.SubscriptionId, UpdateFrom(NewSub(), archived: true)));

        // A future end date is not an ended term either.
        var future = NewSub();
        future.EndDate = new DateOnly(2026, 12, 31);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(created.SubscriptionId, UpdateFrom(future, archived: true)));

        // One PUT may end and archive together — the check reads the request's EndDate, not the
        // stored one, so ending and archiving in a single save is not a two-step dance.
        var lapsed = NewSub();
        lapsed.EndDate = new DateOnly(2026, 5, 1); // FixedNow = 2026-06-15
        var archived = await service.Update(created.SubscriptionId, UpdateFrom(lapsed, archived: true));
        Assert.NotNull(archived!.Archived);
        Assert.Null(archived.Paused);
    }

    [Fact]
    public async Task Update_ArchivedRow_StaysEditableAndRestorable()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());
        var lapsed = NewSub();
        lapsed.EndDate = new DateOnly(2026, 5, 1);
        await service.Update(created.SubscriptionId, UpdateFrom(lapsed, archived: true));

        // Only the TRANSITION into archived is gated. Re-saving an already-archived row must not
        // re-validate, or a row archived before this rule existed could never be edited or restored
        // (every save carrying Archived = true would 400, including the one that clears it).
        var stillLive = NewSub(); // no EndDate at all
        var resaved = await service.Update(created.SubscriptionId, UpdateFrom(stillLive, archived: true));
        Assert.NotNull(resaved!.Archived);

        var restored = await service.Update(created.SubscriptionId, UpdateFrom(stillLive, archived: false));
        Assert.Null(restored!.Archived);
    }

    [Fact]
    public async Task Update_Pause_IsAllowedOnALiveSubscription()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        // Pause stays orthogonal — it is a temporary hold on a live subscription, not a terminal state.
        var paused = await service.Update(created.SubscriptionId, UpdateFrom(NewSub(), paused: true));
        Assert.NotNull(paused!.Paused);
        Assert.Null(paused.Archived);
    }

    // ── List contract ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Search_MatchesNameExternalIdAndContact()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contactId = await SeedContact();

        var withExternal = NewSub(name: "Gym");
        withExternal.ExternalId = "MBR-98765";
        await service.Create(withExternal);
        await service.Create(NewSub(contactId, name: "Video")); // contact "Netflix"
        await service.Create(NewSub(name: "Unrelated"));

        Assert.Equal("Gym", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Search = "MBR-98765" })).Items).Name);
        Assert.Equal("Video", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Search = "netflix" })).Items).Name);
        Assert.Equal("Gym", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Search = "gym" })).Items).Name);
    }

    [Fact]
    public async Task List_FiltersByIntervalAndDerivedStatus()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        var monthly = await service.Create(NewSub(name: "Monthly"));
        var yearlyRequest = NewSub(name: "Yearly");
        yearlyRequest.Interval = BillingInterval.Yearly;
        var yearly = await service.Create(yearlyRequest);

        var intervalPage = await service.ListAsync(new SubscriptionsQueryParams { Intervals = [BillingInterval.Yearly] });
        Assert.Equal("Yearly", Assert.Single(intervalPage.Items).Name);

        // Archive Monthly → it drops from the Active filter and surfaces under Archived. Archiving
        // requires an ended term, so the same PUT carries the lapsed end date.
        var monthlyEnded = NewSub(name: "Monthly");
        monthlyEnded.EndDate = new DateOnly(2026, 5, 1);
        await service.Update(monthly.SubscriptionId, UpdateFrom(monthlyEnded, archived: true));
        Assert.DoesNotContain((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Active] })).Items, s => s.Name == "Monthly");
        Assert.Contains((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Archived] })).Items, s => s.Name == "Monthly");

        // Pause Yearly → surfaced by the Paused status, and no longer by Active.
        await service.Update(yearly.SubscriptionId, UpdateFrom(yearlyRequest, paused: true));
        Assert.Equal("Yearly", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Paused] })).Items).Name);
        Assert.DoesNotContain((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Active] })).Items, s => s.Name == "Yearly");
    }

    [Fact]
    public async Task List_FiltersByEnded_DerivedStatus_SupersedesPaused()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // Open-ended → Active; lapsed → Ended (EndDate before "today", FixedNow = 2026-06-15).
        await service.Create(NewSub(name: "Open"));
        var lapsedRequest = NewSub(name: "Lapsed");
        lapsedRequest.EndDate = new DateOnly(2026, 5, 1);
        var lapsed = await service.Create(lapsedRequest);

        Assert.Equal("Lapsed", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Ended] })).Items).Name);
        Assert.Equal("Open", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Active] })).Items).Name);
        Assert.Equal(2, (await service.ListAsync(new SubscriptionsQueryParams())).Items.Count);

        // Pausing a lapsed subscription keeps it Ended (Ended supersedes Paused) — not under Paused.
        await service.Update(lapsed.SubscriptionId, UpdateFrom(lapsedRequest, paused: true));
        Assert.Contains((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Ended] })).Items, s => s.Name == "Lapsed");
        Assert.DoesNotContain((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Paused] })).Items, s => s.Name == "Lapsed");
    }

    [Fact]
    public async Task List_FiltersByStatus_MultiSelect_IsExhaustiveAndDisjoint()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        // One subscription per derived status (FixedNow = 2026-06-15).
        await service.Create(NewSub(name: "ActiveOne"));
        var pausedReq = NewSub(name: "PausedOne");
        var paused = await service.Create(pausedReq);
        await service.Update(paused.SubscriptionId, UpdateFrom(pausedReq, paused: true));
        var endedReq = NewSub(name: "EndedOne");
        endedReq.EndDate = new DateOnly(2026, 5, 1);
        await service.Create(endedReq);
        var archivedReq = NewSub(name: "ArchivedOne");
        var archived = await service.Create(archivedReq);
        archivedReq.EndDate = new DateOnly(2026, 5, 1); // archiving requires an ended term
        await service.Update(archived.SubscriptionId, UpdateFrom(archivedReq, archived: true));

        // Each single status selects exactly its one row (partitions are disjoint).
        Assert.Equal("ActiveOne", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Active] })).Items).Name);
        Assert.Equal("PausedOne", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Paused] })).Items).Name);
        Assert.Equal("EndedOne", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Ended] })).Items).Name);
        Assert.Equal("ArchivedOne", Assert.Single((await service.ListAsync(new SubscriptionsQueryParams { Statuses = [SubscriptionStatusFilter.Archived] })).Items).Name);

        // A two-status union returns exactly those two, nothing else.
        var union = await service.ListAsync(new SubscriptionsQueryParams
        {
            Statuses = [SubscriptionStatusFilter.Active, SubscriptionStatusFilter.Archived],
        });
        Assert.Equal(new[] { "ActiveOne", "ArchivedOne" }, union.Items.Select(s => s.Name).OrderBy(n => n));

        // All four == unfiltered, with no double-counting (partitions are collectively exhaustive).
        var all = await service.ListAsync(new SubscriptionsQueryParams
        {
            Statuses =
            [
                SubscriptionStatusFilter.Active, SubscriptionStatusFilter.Paused,
                SubscriptionStatusFilter.Ended, SubscriptionStatusFilter.Archived,
            ],
        });
        Assert.Equal(4, all.Items.Count);
        Assert.Equal(4, (await service.ListAsync(new SubscriptionsQueryParams())).Items.Count);
    }

    [Fact]
    public async Task List_SortByIntervalUsesEnumNumericOrder()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        foreach (var (name, interval) in new[]
        {
            ("Y", BillingInterval.Yearly),
            ("D", BillingInterval.Daily),
            ("M", BillingInterval.Monthly),
            ("W", BillingInterval.Weekly),
        })
        {
            var req = NewSub(name: name);
            req.Interval = interval;
            await service.Create(req);
        }

        var asc = await service.ListAsync(new SubscriptionsQueryParams { SortBy = SubscriptionSortBy.Interval, SortDir = SortDirection.Asc });
        Assert.Equal(["D", "W", "M", "Y"], asc.Items.Select(s => s.Name));
    }

    [Fact]
    public async Task List_SortByAmount_RespectsDirection_And_ReportsTotalCount()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        foreach (var (name, amount) in new[] { ("A", 5m), ("B", 30m), ("C", 12m) })
        {
            var req = NewSub(name: name);
            req.Amount = amount;
            await service.Create(req);
        }

        var page = await service.ListAsync(new SubscriptionsQueryParams { SortBy = SubscriptionSortBy.Amount, SortDir = SortDirection.Desc });
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["B", "C", "A"], page.Items.Select(s => s.Name));
    }

    // ── Update happy path & branch logic ────────────────────────────────────────

    [Fact]
    public async Task Update_MutatesScalarFields()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contactId = await SeedContact();
        var created = await service.Create(NewSub());

        var request = new UpdateSubscription
        {
            Name = "Renamed",
            ExternalId = "  EXT-777  ",
            ContactId = contactId,
            StartDate = new DateOnly(2025, 2, 2),
            EndDate = new DateOnly(2028, 2, 2),
            Amount = 42.50m,
            CurrencyCode = "eur",
            Interval = BillingInterval.Monthly,
            IntervalCount = 3,
            FirstBillingDate = new DateOnly(2025, 3, 3),
            Notes = "updated notes",
        };

        var updated = await service.Update(created.SubscriptionId, request);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal("EXT-777", updated.ExternalId);
        Assert.Equal(contactId, updated.Contact!.ContactId);
        Assert.Equal(new DateOnly(2025, 2, 2), updated.StartDate);
        Assert.Equal(new DateOnly(2028, 2, 2), updated.EndDate);
        Assert.Equal(42.50m, updated.Amount);
        Assert.Equal("EUR", updated.CurrencyCode);
        Assert.Equal(BillingInterval.Monthly, updated.Interval);
        Assert.Equal(3, updated.IntervalCount);
        Assert.Equal(new DateOnly(2025, 3, 3), updated.FirstBillingDate);
        Assert.Equal("updated notes", updated.Notes);
    }

    [Fact]
    public async Task Update_MissingSubscription_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        Assert.Null(await service.Update(Guid.NewGuid(), UpdateFrom(NewSub())));
    }

    [Fact]
    public async Task Update_UnchangedContact_IsNotRevalidated_EvenIfNowArchived()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contactId = await SeedContact();
        var created = await service.Create(NewSub(contactId));

        // The linked contact is archived after the subscription was created. An unrelated edit
        // that keeps the same contact must not 400 (change-only re-validation).
        var contact = (await journal.Contacts.FindAsync(contactId))!;
        contact.Archived = FixedNow;
        await journal.SaveChangesAsync();

        var request = UpdateFrom(NewSub(contactId));
        request.Name = "Still linked";
        var updated = await service.Update(created.SubscriptionId, request);

        Assert.Equal("Still linked", updated!.Name);
        Assert.Equal(contactId, updated.Contact!.ContactId);
    }

    [Fact]
    public async Task Update_ChangingToInvalidContact_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        await Assert.ThrowsAsync<DomainValidationException>(
            () => service.Update(created.SubscriptionId, UpdateFrom(NewSub(Guid.NewGuid()))));
    }

    [Fact]
    public async Task Update_UnsupportedCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        var request = UpdateFrom(NewSub());
        request.CurrencyCode = "ZZZ";
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.SubscriptionId, request));
    }

    [Fact]
    public async Task Update_EndBeforeStart_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        var request = UpdateFrom(NewSub());
        request.EndDate = request.StartDate.AddDays(-1);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(created.SubscriptionId, request));
    }

    // ── Delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesSubscription()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var created = await service.Create(NewSub());

        Assert.True(await service.Delete(created.SubscriptionId));
        Assert.Null(await service.Get(created.SubscriptionId));
    }

    [Fact]
    public async Task Delete_MissingSubscription_ReturnsFalse()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        Assert.False(await service.Delete(Guid.NewGuid()));
    }
}
