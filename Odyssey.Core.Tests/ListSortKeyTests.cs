using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Xunit;
using DtoBudgetCategoryType = Odyssey.Dtos.Finance.BudgetCategoryType;
using Odyssey.Core.Finance;
using Context = Odyssey.Context;

namespace Odyssey.Core.Tests;

/// <summary>
/// Locks the per-resource server-side sort contract (issue #277). Every value in each
/// <c>*SortBy</c> enum must actually be honoured by its service — a key that is missing from the
/// service's <c>switch</c> silently falls through to the default column, which is exactly the class
/// of bug fixed for transaction-tags in #285 (and which was still latent in the contact and
/// currency lists until these tests). Each case seeds rows whose order by the key under test differs
/// from the resource's default order, so a fall-through is caught rather than masked.
///
/// Negative binding cases (an unknown sort key or direction → 400) are shared across every list
/// endpoint by the generic query-string binding and are covered in <c>ListContractApiTests</c>.
/// </summary>
public class ListSortKeyTests
{
    private static readonly DateTime D1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D2 = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D3 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D4 = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D5 = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D6 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // Assert that the subsequence of the seeded ids (ignoring any reference rows) matches expectation.
    private static void AssertOrder<TId>(IEnumerable<TId> actual, ISet<TId> seeded, params TId[] expected) =>
        Assert.Equal(expected, actual.Where(seeded.Contains));

    // ── Transaction tags: Name (default), Description, Status ────────────────────

    [Fact]
    public async Task TransactionTags_EverySortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new TransactionTagService(context);

        var t1 = new TransactionTag { Name = "Charlie", Description = "apple", Archived = null };
        var t2 = new TransactionTag { Name = "Alpha", Description = "banana", Archived = D1 };
        var t3 = new TransactionTag { Name = "Bravo", Description = "cherry", Archived = null };
        context.TransactionTags.AddRange(t1, t2, t3);
        await context.SaveChangesAsync();
        var ids = new HashSet<Guid> { t1.TransactionTagId, t2.TransactionTagId, t3.TransactionTagId };

        async Task<List<Guid>> List(TransactionTagSortBy key, SortDirection dir) =>
            (await service.ListAsync(new TransactionTagsQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.TransactionTagId).ToList();

        AssertOrder(await List(TransactionTagSortBy.Name, SortDirection.Asc), ids, t2.TransactionTagId, t3.TransactionTagId, t1.TransactionTagId);
        AssertOrder(await List(TransactionTagSortBy.Name, SortDirection.Desc), ids, t1.TransactionTagId, t3.TransactionTagId, t2.TransactionTagId);
        AssertOrder(await List(TransactionTagSortBy.Description, SortDirection.Asc), ids, t1.TransactionTagId, t2.TransactionTagId, t3.TransactionTagId);
        AssertOrder(await List(TransactionTagSortBy.Description, SortDirection.Desc), ids, t3.TransactionTagId, t2.TransactionTagId, t1.TransactionTagId);

        // Status (archival): the single archived row sorts last ascending / first descending. The two
        // active rows tie on the flag and fall to the id tiebreak, so only the archived position is asserted.
        var statusAsc = (await List(TransactionTagSortBy.Status, SortDirection.Asc)).Where(ids.Contains).ToList();
        var statusDesc = (await List(TransactionTagSortBy.Status, SortDirection.Desc)).Where(ids.Contains).ToList();
        Assert.Equal(t2.TransactionTagId, statusAsc[^1]);
        Assert.Equal(t2.TransactionTagId, statusDesc[0]);
    }

    // ── Contacts: Name (default), Type, NormalizedName, Status ─────────────
    // NormalizedName and Status were the keys silently ignored before the fix.

    [Fact]
    public async Task Contacts_EverySortKey_IsHonoured()
    {
        await using var journal = TestContextFactory.CreateJournal();
        var service = new ContactService(journal, new NoopContactReferenceGuard());

        // Resolved display names Zeta/Yankee/Xray are deliberately decoupled from the NormalizedName
        // keys aaa/bbb/ccc, so the Name sort (by resolved value) and NormalizedName sort differ.
        var c1 = new Contact { ExternalUid = $"urn:uuid:{Guid.NewGuid()}", NormalizedName = "aaa", Type = ContactType.Person, Archived = null, PersonDetails = new() { FirstName = "Zeta", LastName = "Person" } };
        var c2 = new Contact { ExternalUid = $"urn:uuid:{Guid.NewGuid()}", NormalizedName = "bbb", Type = ContactType.Organization, Archived = D1, OrganizationDetails = new() { LegalName = "Yankee" } };
        var c3 = new Contact { ExternalUid = $"urn:uuid:{Guid.NewGuid()}", NormalizedName = "ccc", Type = ContactType.Organization, Archived = null, OrganizationDetails = new() { LegalName = "Xray" } };
        journal.Contacts.AddRange(c1, c2, c3);
        await journal.SaveChangesAsync();
        var ids = new HashSet<Guid> { c1.ContactId, c2.ContactId, c3.ContactId };

        async Task<List<Guid>> List(ContactSortBy key, SortDirection dir) =>
            (await service.ListAsync(new ContactsQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.ContactId).ToList();

        // Name (default, by resolved display name): Xray, Yankee, Zeta
        AssertOrder(await List(ContactSortBy.Name, SortDirection.Asc), ids, c3.ContactId, c2.ContactId, c1.ContactId);
        // Type: Person(1) sorts before Organization(2), so the one Person leads ascending.
        var typeAsc = (await List(ContactSortBy.Type, SortDirection.Asc)).Where(ids.Contains).ToList();
        Assert.Equal(c1.ContactId, typeAsc[0]);
        var typeDesc = (await List(ContactSortBy.Type, SortDirection.Desc)).Where(ids.Contains).ToList();
        Assert.Equal(c1.ContactId, typeDesc[^1]);
        // NormalizedName: aaa, bbb, ccc — order differs from Name.
        AssertOrder(await List(ContactSortBy.NormalizedName, SortDirection.Asc), ids, c1.ContactId, c2.ContactId, c3.ContactId);
        AssertOrder(await List(ContactSortBy.NormalizedName, SortDirection.Desc), ids, c3.ContactId, c2.ContactId, c1.ContactId);

        var statusAsc = (await List(ContactSortBy.Status, SortDirection.Asc)).Where(ids.Contains).ToList();
        var statusDesc = (await List(ContactSortBy.Status, SortDirection.Desc)).Where(ids.Contains).ToList();
        Assert.Equal(c2.ContactId, statusAsc[^1]);
        Assert.Equal(c2.ContactId, statusDesc[0]);
    }

    // ── Currencies: Code (default), Name, Symbol, MinorUnits, Status ─────────────
    // Symbol, MinorUnits and Status were the keys silently ignored before the fix.

    [Fact]
    public async Task Currencies_EverySortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new CurrencyService(context);

        // Use codes outside the seeded set (USD/EUR/SEK) and filter to them, so the reference rows
        // don't interfere with the asserted order.
        var a = new Currency { CurrencyCode = "ZZA", Name = "Yen-ish", Symbol = "a", MinorUnits = 3, Archived = null };
        var b = new Currency { CurrencyCode = "ZZB", Name = "Xeno", Symbol = "b", MinorUnits = 1, Archived = D1 };
        var c = new Currency { CurrencyCode = "ZZC", Name = "Wonka", Symbol = "c", MinorUnits = 2, Archived = null };
        context.Currencies.AddRange(a, b, c);
        await context.SaveChangesAsync();
        var ids = new HashSet<string> { "ZZA", "ZZB", "ZZC" };

        async Task<List<string>> List(CurrencySortBy key, SortDirection dir) =>
            (await service.ListAsync(new CurrenciesQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.CurrencyCode).ToList();

        // Code (default): ZZA, ZZB, ZZC
        AssertOrder(await List(CurrencySortBy.Code, SortDirection.Asc), ids, "ZZA", "ZZB", "ZZC");
        // Name: Wonka(ZZC), Xeno(ZZB), Yen-ish(ZZA) — order differs from Code.
        AssertOrder(await List(CurrencySortBy.Name, SortDirection.Asc), ids, "ZZC", "ZZB", "ZZA");
        // Symbol: a, b, c → ZZA, ZZB, ZZC (matches Code here, but the reverse must still reverse).
        AssertOrder(await List(CurrencySortBy.Symbol, SortDirection.Desc), ids, "ZZC", "ZZB", "ZZA");
        // MinorUnits: 1(ZZB), 2(ZZC), 3(ZZA) — order differs from Code, so a fall-through would fail.
        AssertOrder(await List(CurrencySortBy.MinorUnits, SortDirection.Asc), ids, "ZZB", "ZZC", "ZZA");
        AssertOrder(await List(CurrencySortBy.MinorUnits, SortDirection.Desc), ids, "ZZA", "ZZC", "ZZB");

        var statusAsc = (await List(CurrencySortBy.Status, SortDirection.Asc)).Where(ids.Contains).ToList();
        var statusDesc = (await List(CurrencySortBy.Status, SortDirection.Desc)).Where(ids.Contains).ToList();
        Assert.Equal("ZZB", statusAsc[^1]);   // the archived row sorts last ascending
        Assert.Equal("ZZB", statusDesc[0]);
    }

    // ── Exchange rates: AsOf (default), Pair, Rate, Inverse, CreatedAt, Status ──

    [Fact]
    public async Task ExchangeRates_NonStatusSortKeys_AreHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        // Distinct pairs so each row is the current rate for its pair (isolates the non-status keys).
        var r1 = new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", AsOf = D1, Rate = 2m, CreatedAt = D3 };
        var r2 = new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "SEK", AsOf = D2, Rate = 3m, CreatedAt = D1 };
        var r3 = new ExchangeRate { FromCurrencyCode = "EUR", ToCurrencyCode = "SEK", AsOf = D3, Rate = 1m, CreatedAt = D2 };
        context.ExchangeRates.AddRange(r1, r2, r3);
        await context.SaveChangesAsync();
        var ids = new HashSet<Guid> { r1.ExchangeRateId, r2.ExchangeRateId, r3.ExchangeRateId };

        async Task<List<Guid>> List(ExchangeRateSortBy key, SortDirection dir) =>
            (await service.ListAsync(new ExchangeRatesQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.ExchangeRateId).ToList();

        // AsOf (default): D1, D2, D3
        AssertOrder(await List(ExchangeRateSortBy.AsOf, SortDirection.Asc), ids, r1.ExchangeRateId, r2.ExchangeRateId, r3.ExchangeRateId);
        // Pair (From, then To): EUR/SEK, USD/EUR, USD/SEK
        AssertOrder(await List(ExchangeRateSortBy.Pair, SortDirection.Asc), ids, r3.ExchangeRateId, r1.ExchangeRateId, r2.ExchangeRateId);
        // Rate: 1, 2, 3
        AssertOrder(await List(ExchangeRateSortBy.Rate, SortDirection.Asc), ids, r3.ExchangeRateId, r1.ExchangeRateId, r2.ExchangeRateId);
        // Inverse (1 / Rate): 1/3, 1/2, 1 — the Rate order reversed, in both directions.
        AssertOrder(await List(ExchangeRateSortBy.Inverse, SortDirection.Asc), ids, r2.ExchangeRateId, r1.ExchangeRateId, r3.ExchangeRateId);
        AssertOrder(await List(ExchangeRateSortBy.Inverse, SortDirection.Desc), ids, r3.ExchangeRateId, r1.ExchangeRateId, r2.ExchangeRateId);
        // CreatedAt: D1, D2, D3
        AssertOrder(await List(ExchangeRateSortBy.CreatedAt, SortDirection.Asc), ids, r2.ExchangeRateId, r3.ExchangeRateId, r1.ExchangeRateId);
        AssertOrder(await List(ExchangeRateSortBy.CreatedAt, SortDirection.Desc), ids, r1.ExchangeRateId, r3.ExchangeRateId, r2.ExchangeRateId);
    }

    [Fact]
    public async Task ExchangeRates_StatusSortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        // Two rates for the same pair: the newer AsOf is "current", the older is "historical".
        var older = new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", AsOf = D1, Rate = 1m, CreatedAt = D1 };
        var newer = new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", AsOf = D2, Rate = 2m, CreatedAt = D2 };
        context.ExchangeRates.AddRange(older, newer);
        await context.SaveChangesAsync();

        var asc = (await service.ListAsync(new ExchangeRatesQueryParams { SortBy = ExchangeRateSortBy.Status, SortDir = SortDirection.Asc })).Items;
        var desc = (await service.ListAsync(new ExchangeRatesQueryParams { SortBy = ExchangeRateSortBy.Status, SortDir = SortDirection.Desc })).Items;

        Assert.Equal(newer.ExchangeRateId, asc[0].ExchangeRateId);   // current sorts before historical ascending
        Assert.Equal(older.ExchangeRateId, desc[0].ExchangeRateId);
    }

    // ── Budgets: StartDate (default), Name, EndDate ──────────────────────────────

    [Fact]
    public async Task Budgets_EverySortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetService(context, TestContextFactory.EmptyContactLookup());

        var b1 = new Budget { Name = "Charlie", StartDate = D3, EndDate = D5 };
        var b2 = new Budget { Name = "Alpha", StartDate = D2, EndDate = D6 };
        var b3 = new Budget { Name = "Bravo", StartDate = D1, EndDate = D4 };
        context.Budgets.AddRange(b1, b2, b3);
        await context.SaveChangesAsync();
        var ids = new HashSet<Guid> { b1.BudgetId, b2.BudgetId, b3.BudgetId };

        async Task<List<Guid>> List(BudgetSortBy key, SortDirection dir) =>
            (await service.ListAsync(new BudgetsQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.BudgetId).ToList();

        // StartDate (default): D1(b3), D2(b2), D3(b1)
        AssertOrder(await List(BudgetSortBy.StartDate, SortDirection.Asc), ids, b3.BudgetId, b2.BudgetId, b1.BudgetId);
        // Name: Alpha(b2), Bravo(b3), Charlie(b1) — differs from StartDate.
        AssertOrder(await List(BudgetSortBy.Name, SortDirection.Asc), ids, b2.BudgetId, b3.BudgetId, b1.BudgetId);
        AssertOrder(await List(BudgetSortBy.Name, SortDirection.Desc), ids, b1.BudgetId, b3.BudgetId, b2.BudgetId);
        // EndDate: D4(b3), D5(b1), D6(b2) — differs from StartDate.
        AssertOrder(await List(BudgetSortBy.EndDate, SortDirection.Asc), ids, b3.BudgetId, b1.BudgetId, b2.BudgetId);
    }

    // ── Budget items: Name (default), PlannedAmount, Category ────────────────────

    [Fact]
    public async Task BudgetItems_EverySortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new BudgetItemService(context);

        var budgetId = Guid.NewGuid();
        var i1 = new BudgetItem { BudgetId = budgetId, Name = "Alpha", PlannedAmount = 20m, CategoryType = Context.BudgetCategoryType.Income };
        var i2 = new BudgetItem { BudgetId = budgetId, Name = "Bravo", PlannedAmount = 10m, CategoryType = Context.BudgetCategoryType.Expense };
        context.BudgetItems.AddRange(i1, i2);
        await context.SaveChangesAsync();
        var ids = new HashSet<Guid> { i1.BudgetItemId, i2.BudgetItemId };

        async Task<List<Guid>> List(BudgetItemSortBy key, SortDirection dir) =>
            (await service.ListAsync(new BudgetItemsQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.BudgetItemId).ToList();

        // Name (default): Alpha(i1), Bravo(i2)
        AssertOrder(await List(BudgetItemSortBy.Name, SortDirection.Asc), ids, i1.BudgetItemId, i2.BudgetItemId);
        // PlannedAmount: 10(i2), 20(i1) — differs from Name, so a fall-through would fail.
        AssertOrder(await List(BudgetItemSortBy.PlannedAmount, SortDirection.Asc), ids, i2.BudgetItemId, i1.BudgetItemId);
        AssertOrder(await List(BudgetItemSortBy.PlannedAmount, SortDirection.Desc), ids, i1.BudgetItemId, i2.BudgetItemId);
        // Category: Expense(i2) before Income(i1) — differs from Name.
        AssertOrder(await List(BudgetItemSortBy.Category, SortDirection.Asc), ids, i2.BudgetItemId, i1.BudgetItemId);
    }

    // ── Tax statements: FiscalYear (default), Name, Status ───────────────────────

    [Fact]
    public async Task TaxStatements_EverySortKey_IsHonoured()
    {
        await using var context = TestContextFactory.Create();
        var service = new TaxStatementService(context);

        var s1 = new TaxStatement { Name = "Charlie", FiscalYear = 2020, StartDate = D1, EndDate = D6, Status = TaxStatementStatus.Flagged, CreatedAtUtc = D1 };
        var s2 = new TaxStatement { Name = "Alpha", FiscalYear = 2022, StartDate = D1, EndDate = D6, Status = TaxStatementStatus.New, CreatedAtUtc = D1 };
        var s3 = new TaxStatement { Name = "Bravo", FiscalYear = 2021, StartDate = D1, EndDate = D6, Status = TaxStatementStatus.Approved, CreatedAtUtc = D1 };
        context.TaxStatements.AddRange(s1, s2, s3);
        await context.SaveChangesAsync();
        var ids = new HashSet<Guid> { s1.TaxStatementId, s2.TaxStatementId, s3.TaxStatementId };

        async Task<List<Guid>> List(TaxStatementSortBy key, SortDirection dir) =>
            (await service.ListAsync(new TaxStatementsQueryParams { SortBy = key, SortDir = dir }))
            .Items.Select(i => i.TaxStatementId).ToList();

        // FiscalYear (default): 2020(s1), 2021(s3), 2022(s2)
        AssertOrder(await List(TaxStatementSortBy.FiscalYear, SortDirection.Asc), ids, s1.TaxStatementId, s3.TaxStatementId, s2.TaxStatementId);
        // Name: Alpha(s2), Bravo(s3), Charlie(s1) — differs from FiscalYear.
        AssertOrder(await List(TaxStatementSortBy.Name, SortDirection.Asc), ids, s2.TaxStatementId, s3.TaxStatementId, s1.TaxStatementId);
        AssertOrder(await List(TaxStatementSortBy.Name, SortDirection.Desc), ids, s1.TaxStatementId, s3.TaxStatementId, s2.TaxStatementId);
        // Status: New(s2), Approved(s3), Flagged(s1) — differs from FiscalYear.
        AssertOrder(await List(TaxStatementSortBy.Status, SortDirection.Asc), ids, s2.TaxStatementId, s3.TaxStatementId, s1.TaxStatementId);
    }
}
