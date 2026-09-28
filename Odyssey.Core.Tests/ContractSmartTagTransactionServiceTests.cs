using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using Context = Odyssey.Context;
using ContextAccountType = Odyssey.Context.AccountType;
using ContextContactType = Odyssey.Dtos.ContactType;
using ContractPartyRole = Odyssey.Context.ContractPartyRole;

namespace Odyssey.Core.Tests;

/// <summary>
/// The contract-scoped smart-tag match (issue #226): the three rules (tag, term, merchant-is-party),
/// the date window, the empty reasons and the per-currency summary, on the InMemory tier.
/// </summary>
public class ContractSmartTagTransactionServiceTests
{
    private static readonly DateTime TermStart = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TermEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InTerm = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One contract with a term, one smart tag, one contact party (the supplier), plus an unrelated tag
    /// and a non-party contact to build near-misses from.
    /// </summary>
    private sealed class Fixture
    {
        public required OdysseyContext Context { get; init; }
        public required Guid ContractId { get; init; }
        public required Guid AccountId { get; init; }
        public required Guid SmartTagId { get; init; }
        public required Guid OtherTagId { get; init; }
        public Guid SupplierId { get; set; }
        public Guid StrangerId { get; set; }

        public ContractSmartTagTransactionService Service() =>
            new(Context, new TransactionService(Context, TestContextFactory.ContactLookup(Context)));

        public async Task<Guid> AddTransaction(
            DateTime timeStamp, decimal amount = -100m, Guid? contactId = null, Guid[]? tagIds = null,
            string currency = "NOK", string description = "Power bill", bool noContact = false)
        {
            var tags = (tagIds ?? [SmartTagId])
                .Select(id => Context.TransactionTags.Single(t => t.TransactionTagId == id))
                .ToList();
            var transaction = new Transaction
            {
                Description = description,
                Amount = amount,
                TimeStamp = timeStamp,
                AccountId = AccountId,
                ContactId = noContact ? null : contactId ?? SupplierId,
                CurrencyCode = currency,
                TransactionTags = tags,
            };
            Context.Transactions.Add(transaction);
            await Context.SaveChangesAsync();
            return transaction.TransactionId;
        }

        public async Task<Guid> AddContact(string name, ContextContactType type = ContextContactType.Organization, bool archived = false)
        {
            var contact = new Contact
            {
                ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
                DisplayName = name,
                NormalizedName = name.ToLowerInvariant(),
                Type = type,
                Archived = archived ? DateTime.UtcNow : null,
            };
            Context.Contacts.Add(contact);
            await Context.SaveChangesAsync();
            return contact.ContactId;
        }

        public async Task AddParty(Guid contactId, ContractPartyRole role = ContractPartyRole.Other,
            DateTime? fromDate = null, DateTime? toDate = null)
        {
            Context.ContractParties.Add(new ContractParty
            {
                ContractId = ContractId, ContactId = contactId, Role = role, FromDate = fromDate, ToDate = toDate,
            });
            await Context.SaveChangesAsync();
        }

        public async Task SetDates(DateTime? start, DateTime? end, DateTime? completion = null)
        {
            var contract = Context.Contracts.Single(c => c.ContractId == ContractId);
            contract.StartDate = start;
            contract.EndDate = end;
            contract.CompletionDate = completion;
            await Context.SaveChangesAsync();
        }

        public async Task<ContractSmartTagTransactionsResult> List(
            ContractSmartTagTransactionsQueryParams? query = null) =>
            (await Service().ListAsync(ContractId, query ?? new ContractSmartTagTransactionsQueryParams()))!;
    }

    private static async Task<Fixture> Seed(bool withSmartTag = true, bool withParty = true)
    {
        var context = TestContextFactory.Create();
        var account = new Account
        {
            Name = "Checking",
            Description = "Daily use",
            AccountType = ContextAccountType.CheckingAccount,
            Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var contract = new Contract
        {
            Name = "Power",
            Type = Context.ContractType.Subscription,
            StartDate = TermStart,
            EndDate = TermEnd,
            CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var smartTag = new TransactionTag { Name = "Electricity" };
        var otherTag = new TransactionTag { Name = "Groceries" };
        context.Accounts.Add(account);
        context.Contracts.Add(contract);
        context.TransactionTags.AddRange(smartTag, otherTag);
        await context.SaveChangesAsync();

        var fixture = new Fixture
        {
            Context = context,
            ContractId = contract.ContractId,
            AccountId = account.AccountId,
            SmartTagId = smartTag.TransactionTagId,
            OtherTagId = otherTag.TransactionTagId,
        };
        fixture.SupplierId = await fixture.AddContact("Supplier AS");
        fixture.StrangerId = await fixture.AddContact("Old Supplier AS");

        if (withSmartTag)
        {
            context.ContractSmartTags.Add(new ContractSmartTag
            {
                ContractId = contract.ContractId, TransactionTagId = smartTag.TransactionTagId, AddedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }
        if (withParty)
            await fixture.AddParty(fixture.SupplierId);

        return fixture;
    }

    private static List<Guid> Ids(ContractSmartTagTransactionsResult result) =>
        result.Page.Items.Select(t => t.TransactionId).ToList();

    [Fact]
    public async Task ReturnsNullForMissingContract()
    {
        var f = await Seed();
        Assert.Null(await f.Service().ListAsync(Guid.NewGuid(), new ContractSmartTagTransactionsQueryParams()));
    }

    [Fact]
    public async Task AllThreeRules_Match_IsReturned()
    {
        var f = await Seed();
        var id = await f.AddTransaction(InTerm);

        var result = await f.List();

        Assert.Equal([id], Ids(result));
        Assert.Equal(ContractSmartTagEmptyReason.None, result.Scope.EmptyReason);
        Assert.Equal(1, result.Page.TotalCount);
    }

    [Fact]
    public async Task WithoutAWatchedTag_IsNotReturned()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, tagIds: [f.OtherTagId]);
        await f.AddTransaction(InTerm, tagIds: []);

        Assert.Empty((await f.List()).Page.Items);
    }

    [Fact]
    public async Task StartBoundary_IsInclusiveAtMidnight()
    {
        var f = await Seed();
        await f.AddTransaction(TermStart.AddTicks(-1));
        var onStart = await f.AddTransaction(TermStart);

        Assert.Equal([onStart], Ids(await f.List()));
    }

    [Fact]
    public async Task EndBoundary_IncludesTheWholeEndDay()
    {
        var f = await Seed();
        var lastSecond = await f.AddTransaction(TermEnd.AddDays(1).AddSeconds(-1));
        await f.AddTransaction(TermEnd.AddDays(1));

        Assert.Equal([lastSecond], Ids(await f.List()));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task NonMidnightDatesOfAnyKind_YieldTheMidnightUtcBounds(DateTimeKind kind)
    {
        var f = await Seed();
        await f.SetDates(
            new DateTime(2025, 1, 1, 15, 30, 0, kind),
            new DateTime(2025, 12, 31, 9, 45, 0, kind));

        var result = await f.List();

        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Scope.From);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Scope.ToExclusive);
        Assert.Equal(DateTimeKind.Utc, result.Scope.From!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, result.Scope.ToExclusive!.Value.Kind);
    }

    [Fact]
    public async Task MerchantNotAParty_OrNoMerchant_IsNotReturned()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, contactId: f.StrangerId);
        await f.AddTransaction(InTerm, noContact: true);

        Assert.Empty((await f.List()).Page.Items);
    }

    [Fact]
    public async Task AccountOrPropertyParty_DoesNotCountAsAMerchant()
    {
        var f = await Seed(withParty: false);
        f.Context.ContractParties.Add(new ContractParty
        {
            ContractId = f.ContractId, AccountId = f.AccountId, Role = ContractPartyRole.Borrower,
        });
        await f.Context.SaveChangesAsync();
        await f.AddTransaction(InTerm);

        var result = await f.List();

        Assert.Equal(ContractSmartTagEmptyReason.NoContactParties, result.Scope.EmptyReason);
        Assert.Empty(result.Page.Items);
    }

    [Fact]
    public async Task PersonAndOrganizationParties_BothMatch()
    {
        var f = await Seed();
        var landlord = await f.AddContact("Kari Nordmann", ContextContactType.Person);
        await f.AddParty(landlord, ContractPartyRole.Landlord);
        var fromOrganization = await f.AddTransaction(InTerm);
        var fromPerson = await f.AddTransaction(InTerm.AddDays(1), contactId: landlord);

        var result = await f.List();

        Assert.Equal([fromPerson, fromOrganization], Ids(result));
        Assert.Equal(2, result.Scope.PartyContactCount);
        Assert.All(result.Page.Items, t => Assert.NotNull(t.Contact));
    }

    [Fact]
    public async Task OnlyStartDate_HasNoUpperBound()
    {
        var f = await Seed();
        await f.SetDates(TermStart, null);
        var late = await f.AddTransaction(new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await f.AddTransaction(TermStart.AddDays(-1));

        var result = await f.List();

        Assert.Null(result.Scope.ToExclusive);
        Assert.Equal(TermStart, result.Scope.From);
        Assert.Equal([late], Ids(result));
    }

    [Fact]
    public async Task OnlyEndDate_HasNoLowerBound()
    {
        var f = await Seed();
        await f.SetDates(null, TermEnd);
        var early = await f.AddTransaction(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await f.AddTransaction(TermEnd.AddDays(2));

        var result = await f.List();

        Assert.Null(result.Scope.From);
        Assert.Equal([early], Ids(result));
    }

    [Fact]
    public async Task OneOffContract_HasNoDateBound()
    {
        var f = await Seed();
        await f.SetDates(null, null, completion: new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var early = await f.AddTransaction(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var late = await f.AddTransaction(new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await f.List();

        Assert.Null(result.Scope.From);
        Assert.Null(result.Scope.ToExclusive);
        Assert.Equal([late, early], Ids(result));
    }

    [Fact]
    public async Task NoDatesAtAll_HasNoDateBound()
    {
        var f = await Seed();
        await f.SetDates(null, null);
        await f.AddTransaction(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await f.List();

        Assert.Null(result.Scope.From);
        Assert.Null(result.Scope.ToExclusive);
        Assert.Single(result.Page.Items);
    }

    [Fact]
    public async Task StartDayAfterEndDay_IsInvalidTerm_WithTheComputedBounds()
    {
        var f = await Seed();
        await f.SetDates(TermEnd, TermStart);
        await f.AddTransaction(InTerm);

        var result = await f.List();

        Assert.Equal(ContractSmartTagEmptyReason.InvalidTerm, result.Scope.EmptyReason);
        Assert.True(result.Scope.From >= result.Scope.ToExclusive);
        Assert.Empty(result.Page.Items);
        Assert.Equal(0, result.Page.TotalCount);
    }

    [Fact]
    public async Task SameDayWithStartTimeAfterEndTime_IsNotInvalidTerm()
    {
        var f = await Seed();
        var day = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        await f.SetDates(day.AddHours(10), day.AddHours(9));
        var id = await f.AddTransaction(day.AddHours(20));

        var result = await f.List();

        Assert.Equal(ContractSmartTagEmptyReason.None, result.Scope.EmptyReason);
        Assert.Equal([id], Ids(result));
    }

    [Fact]
    public async Task NoSmartTags_IsReported()
    {
        var f = await Seed(withSmartTag: false);
        await f.AddTransaction(InTerm);

        var result = await f.List();

        Assert.Equal(ContractSmartTagEmptyReason.NoSmartTags, result.Scope.EmptyReason);
        Assert.Equal(0, result.Scope.SmartTagCount);
        Assert.Equal(1, result.Scope.PartyContactCount);
    }

    [Fact]
    public async Task NoContactParties_IsReported()
    {
        var f = await Seed(withParty: false);

        var result = await f.List();

        Assert.Equal(ContractSmartTagEmptyReason.NoContactParties, result.Scope.EmptyReason);
        Assert.Equal(1, result.Scope.SmartTagCount);
    }

    [Fact]
    public async Task NoSmartTags_TakesPrecedenceOverNoContactParties_AndInvalidTerm()
    {
        var f = await Seed(withSmartTag: false, withParty: false);
        await f.SetDates(TermEnd, TermStart);

        Assert.Equal(ContractSmartTagEmptyReason.NoSmartTags, (await f.List()).Scope.EmptyReason);
    }

    [Fact]
    public async Task EmptyReason_EchoesTheRequestedWindow()
    {
        var f = await Seed(withSmartTag: false);

        var result = await f.List(new ContractSmartTagTransactionsQueryParams { Offset = 10, Limit = 5 });

        Assert.Equal(10, result.Page.Offset);
        Assert.Equal(5, result.Page.Limit);
        Assert.Equal(0, result.Page.TotalCount);
    }

    [Fact]
    public async Task TwoWatchedTags_AppearOnce()
    {
        var f = await Seed();
        f.Context.ContractSmartTags.Add(new ContractSmartTag
        {
            ContractId = f.ContractId, TransactionTagId = f.OtherTagId, AddedAt = DateTime.UtcNow,
        });
        await f.Context.SaveChangesAsync();
        var id = await f.AddTransaction(InTerm, amount: -40m, tagIds: [f.SmartTagId, f.OtherTagId]);

        var result = await f.List();

        Assert.Equal([id], Ids(result));
        Assert.Equal(1, result.Page.TotalCount);
        Assert.Equal(1, result.Summary.TransactionCount);
        Assert.Equal(40m, Assert.Single(result.Summary.ByCurrency).TotalOut);
    }

    [Fact]
    public async Task PartyDates_AreIgnored()
    {
        var f = await Seed(withParty: false);
        await f.AddParty(f.SupplierId, fromDate: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            toDate: new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var id = await f.AddTransaction(InTerm);

        Assert.Equal([id], Ids(await f.List()));
    }

    [Fact]
    public async Task ArchivedContactAndArchivedTag_StillMatch()
    {
        var f = await Seed();
        var archived = await f.AddContact("Retired Supplier", archived: true);
        await f.AddParty(archived, ContractPartyRole.Guarantor);
        f.Context.TransactionTags.Single(t => t.TransactionTagId == f.SmartTagId).Archived = DateTime.UtcNow;
        await f.Context.SaveChangesAsync();
        var id = await f.AddTransaction(InTerm, contactId: archived);

        var result = await f.List();

        Assert.Equal([id], Ids(result));
        Assert.Equal(archived, result.Page.Items[0].Contact?.ContactId);
    }

    [Fact]
    public async Task Summary_SingleCurrency_MatchesHandComputedSums()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, amount: -1000m);
        await f.AddTransaction(InTerm.AddDays(1), amount: -250.5m);
        await f.AddTransaction(InTerm.AddDays(2), amount: 75.25m);

        var result = await f.List();

        Assert.Equal(3, result.Summary.TransactionCount);
        Assert.Equal(result.Page.TotalCount, result.Summary.TransactionCount);
        var row = Assert.Single(result.Summary.ByCurrency);
        Assert.Equal("NOK", row.CurrencyCode);
        Assert.Equal(3, row.TransactionCount);
        Assert.Equal(75.25m, row.TotalIn);
        Assert.Equal(1250.5m, row.TotalOut);
        Assert.Equal(-1175.25m, row.Net);
    }

    [Fact]
    public async Task Summary_MixedCurrencies_OneRowEachOrderedByCode()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, amount: -500m, currency: "NOK");
        await f.AddTransaction(InTerm, amount: 25m, currency: "EUR");
        await f.AddTransaction(InTerm, amount: -10m, currency: "EUR");
        await f.AddTransaction(InTerm, amount: -3m, currency: "USD");

        var summary = (await f.List()).Summary;

        Assert.Equal(["EUR", "NOK", "USD"], summary.ByCurrency.Select(r => r.CurrencyCode));
        Assert.Equal(summary.TransactionCount, summary.ByCurrency.Sum(r => r.TransactionCount));
        var eur = summary.ByCurrency[0];
        Assert.Equal((2, 25m, 10m, 15m), (eur.TransactionCount, eur.TotalIn, eur.TotalOut, eur.Net));
        Assert.Equal(500m, summary.ByCurrency[1].TotalOut);
    }

    [Fact]
    public async Task Summary_IgnoresSearchSortAndPaging()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, amount: -100m, description: "Power bill January");
        await f.AddTransaction(InTerm.AddDays(1), amount: -200m, description: "Grid fee");

        var all = await f.List();
        var narrowed = await f.List(new ContractSmartTagTransactionsQueryParams
        {
            Search = "grid", SortBy = TransactionSortBy.Amount, SortDir = Odyssey.Dtos.SortDirection.Asc,
            Offset = 0, Limit = 1,
        });

        Assert.Single(narrowed.Page.Items);
        Assert.Equal(1, narrowed.Page.TotalCount);
        Assert.Equal(all.Summary.ByCurrency, narrowed.Summary.ByCurrency);
        Assert.Equal(2, narrowed.Summary.TransactionCount);
    }

    [Fact]
    public async Task Summary_ExcludesRowsFailingAnyRule()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, amount: -100m);
        await f.AddTransaction(InTerm, amount: -1m, tagIds: [f.OtherTagId]);
        await f.AddTransaction(TermStart.AddDays(-1), amount: -2m);
        await f.AddTransaction(InTerm, amount: -4m, contactId: f.StrangerId);

        var summary = (await f.List()).Summary;

        Assert.Equal(1, summary.TransactionCount);
        Assert.Equal(100m, Assert.Single(summary.ByCurrency).TotalOut);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Summary_IsEmptyForEveryEmptyReason(bool withSmartTag, bool withParty)
    {
        var f = await Seed(withSmartTag, withParty);
        await f.AddTransaction(InTerm);

        var summary = (await f.List()).Summary;

        Assert.Equal(0, summary.TransactionCount);
        Assert.NotNull(summary.ByCurrency);
        Assert.Empty(summary.ByCurrency);
    }

    [Fact]
    public async Task Summary_IsEmptyForInvalidTerm()
    {
        var f = await Seed();
        await f.SetDates(TermEnd, TermStart);

        var summary = (await f.List()).Summary;

        Assert.Equal(0, summary.TransactionCount);
        Assert.Empty(summary.ByCurrency);
    }

    [Fact]
    public async Task ZeroAmount_CountsAsMoneyIn()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, amount: 0m);

        var row = Assert.Single((await f.List()).Summary.ByCurrency);

        Assert.Equal(1, row.TransactionCount);
        Assert.Equal(0m, row.TotalIn);
        Assert.Equal(0m, row.TotalOut);
    }

    [Fact]
    public async Task LimitZero_ReturnsTheCountAndSummaryWithAnEmptyPage()
    {
        var f = await Seed();
        await f.AddTransaction(InTerm, amount: -10m);
        await f.AddTransaction(InTerm, amount: -20m);

        var result = await f.List(new ContractSmartTagTransactionsQueryParams { Limit = 0 });

        Assert.Empty(result.Page.Items);
        Assert.Equal(2, result.Page.TotalCount);
        Assert.Equal(2, result.Summary.TransactionCount);
        Assert.Equal(30m, Assert.Single(result.Summary.ByCurrency).TotalOut);
    }

    [Fact]
    public void ComputeWindow_OneOffIgnoresTermDates()
    {
        Assert.Equal((null, null),
            ContractSmartTagTransactionService.ComputeWindow(TermStart, TermEnd, TermStart));
    }
}
