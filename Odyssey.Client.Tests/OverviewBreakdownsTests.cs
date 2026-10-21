using System.Text.Json;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Pins the Overview "By type" / "By status" rows on Accounts, Tax statements and Budgets. Each
/// bucket is fed a DISTINCT count, so a row reading the wrong count fails here rather than rendering a
/// plausible number; and each tile's rows must sum to the total it stands for.
/// </summary>
public class OverviewBreakdownsTests
{
    private static int Count(IReadOnlyList<OdsBreakdownRow> rows, string label) =>
        (int)rows.Single(r => r.Label == label).Count!;

    private static int Sum(IReadOnlyList<OdsBreakdownRow> rows) => rows.Sum(r => (int)r.Count!);

    // ── Accounts "By type" ────────────────────────────────────────────────────

    [Fact]
    public void AccountTypeRows_ShowAnUnclassifiedRow_WhenAnUnknownAccountIsLive()
    {
        var rows = OverviewBreakdowns.AccountTypeRows(new AccountSummary
        {
            CountsByStatus = new(),
            CountsByType =
            [
                new AccountTypeCount { Type = AccountType.CheckingAccount, Count = 2 },
                new AccountTypeCount { Type = AccountType.Mortgage, Count = 1 },
                new AccountTypeCount { Type = AccountType.Unknown, Count = 3 },
            ],
        });

        Assert.Equal(3, Count(rows, "Unclassified"));
        Assert.Equal("Unclassified", rows[^1].Label);
        Assert.Equal(6, Sum(rows));
    }

    [Fact]
    public void AccountTypeRows_OmitTheUnclassifiedRow_WhenThereIsNone()
    {
        var rows = OverviewBreakdowns.AccountTypeRows(new AccountSummary
        {
            CountsByStatus = new(),
            CountsByType = [new AccountTypeCount { Type = AccountType.SavingsAccount, Count = 4 }],
        });

        Assert.DoesNotContain(rows, r => r.Label == "Unclassified");
        Assert.Equal(4, Sum(rows));
    }

    [Fact]
    public void AccountTypeRows_AreEmpty_BeforeTheSummaryLoads() =>
        Assert.Empty(OverviewBreakdowns.AccountTypeRows(null));

    // ── Tax statements "By status" ────────────────────────────────────────────

    [Fact]
    public void TaxStatementStatusRows_ReadEachBucketFromItsOwnCount()
    {
        var rows = OverviewBreakdowns.TaxStatementStatusRows(new TaxStatementSummary
        {
            TotalStatements = 10,
            CountsByStatus = new TaxStatementStatusCounts { New = 1, Approved = 2, Flagged = 3, Archived = 4 },
        });

        Assert.Equal(["New", "Approved", "Flagged", "Archived"], rows.Select(r => r.Label));
        Assert.Equal(1, Count(rows, "New"));
        Assert.Equal(2, Count(rows, "Approved"));
        Assert.Equal(3, Count(rows, "Flagged"));
        Assert.Equal(4, Count(rows, "Archived"));
        Assert.Equal(10, Sum(rows));
    }

    [Fact]
    public void TaxStatementStatusRows_ShowEveryBucketAtZero_BeforeTheSummaryLoads()
    {
        var rows = OverviewBreakdowns.TaxStatementStatusRows(null);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Count));
    }

    // ── Budgets "By status" ───────────────────────────────────────────────────

    [Fact]
    public void BudgetStatusRows_ReadEachBucketFromItsOwnCount()
    {
        var rows = OverviewBreakdowns.BudgetStatusRows(new BudgetSummary
        {
            TotalBudgets = 7,
            ActiveCount = 5,
            ArchivedCount = 2,
        });

        Assert.Equal(["Active", "Archived"], rows.Select(r => r.Label));
        Assert.Equal(5, Count(rows, "Active"));
        Assert.Equal(2, Count(rows, "Archived"));
        Assert.Equal(7, Sum(rows));
    }

    [Fact]
    public void BudgetsPageState_SavedBeforeTheOverviewExisted_OpensTheOverview()
    {
        // State persisted by a build without the Overview has no OverviewOpen member.
        const string legacy = """{"searchOpen":false,"search":"rent","statusFilter":["Active"]}""";

        var state = JsonSerializer.Deserialize<BudgetsCard.BudgetsPageState>(
            legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.True(state.OverviewOpen);
        Assert.False(state.SearchOpen);
    }
}
