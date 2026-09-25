using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The slices behind a budget's Allocation donuts. The actual donut is ordered and coloured from the
/// planned one, so a tag keeps its colour across the pair.
/// </summary>
public class BudgetAllocationSlicesTests
{
    private static readonly Guid BudgetId = Guid.NewGuid();

    private static ExistingBudgetItem Item(string tag, BudgetCategoryType category, decimal planned, Guid? tagId = null)
    {
        var id = tagId ?? Guid.NewGuid();
        return new ExistingBudgetItem
        {
            BudgetItemId = Guid.NewGuid(),
            BudgetId = BudgetId,
            CategoryType = category,
            PlannedAmount = planned,
            TransactionTagId = id,
            Tag = new ExistingTransactionTag { TransactionTagId = id, Name = tag, Archived = null },
        };
    }

    private static ExistingBudget Budget(params ExistingBudgetItem[] items) => new()
    {
        BudgetId = BudgetId,
        Name = "Household",
        StartDate = new DateTime(2026, 1, 1),
        EndDate = new DateTime(2026, 12, 31),
        Archived = null,
        BudgetItems = items.ToList(),
    };

    private static string Stop(int index) => OdsDonutPalette.Default[index % OdsDonutPalette.Default.Count];

    [Fact]
    public void Planned_slices_are_largest_first_and_drop_zero_plans()
    {
        var budget = Budget(
            Item("Groceries", BudgetCategoryType.Expense, 300),
            Item("Housing", BudgetCategoryType.Expense, 1200),
            Item("Gifts", BudgetCategoryType.Expense, 0),
            Item("Salary", BudgetCategoryType.Income, 5000));

        var planned = BudgetAllocationSlices.Planned(budget, BudgetCategoryType.Expense);

        Assert.Equal(["Housing", "Groceries"], planned.Select(s => s.Label));
    }

    [Fact]
    public void An_actual_slice_keeps_its_planned_colour_when_an_earlier_tag_has_no_actuals()
    {
        var housing = Item("Housing", BudgetCategoryType.Expense, 1200);
        var groceries = Item("Groceries", BudgetCategoryType.Expense, 300);
        var travel = Item("Travel", BudgetCategoryType.Expense, 100);
        var budget = Budget(groceries, travel, housing);
        var planned = BudgetAllocationSlices.Planned(budget, BudgetCategoryType.Expense);

        // Housing — planned first — has nothing matched yet, so the donut will drop it.
        var actualByTag = new Dictionary<Guid, decimal>
        {
            [groceries.TransactionTagId] = -250m,
            [travel.TransactionTagId] = -80m,
        };
        var actual = BudgetAllocationSlices.Actual(budget, BudgetCategoryType.Expense, actualByTag, planned);

        Assert.Equal(["Housing", "Groceries", "Travel"], actual.Select(s => s.Label));
        Assert.Equal(Stop(1), actual.Single(s => s.Label == "Groceries").Color);
        Assert.Equal(Stop(2), actual.Single(s => s.Label == "Travel").Color);
    }

    [Fact]
    public void A_tag_planned_at_zero_follows_the_planned_tags()
    {
        var housing = Item("Housing", BudgetCategoryType.Expense, 1200);
        var gifts = Item("Gifts", BudgetCategoryType.Expense, 0);
        var budget = Budget(gifts, housing);
        var planned = BudgetAllocationSlices.Planned(budget, BudgetCategoryType.Expense);

        var actual = BudgetAllocationSlices.Actual(budget, BudgetCategoryType.Expense,
            new Dictionary<Guid, decimal> { [housing.TransactionTagId] = -900m, [gifts.TransactionTagId] = -40m }, planned);

        Assert.Equal(["Housing", "Gifts"], actual.Select(s => s.Label));
        // Gifts is not in the planned donut, so it takes the stop after the planned ones and cannot
        // collide with Housing's.
        Assert.Equal(Stop(0), actual[0].Color);
        Assert.Equal(Stop(1), actual[1].Color);
    }

    [Fact]
    public void An_expense_reads_its_magnitude_and_income_its_signed_sum()
    {
        var rent = Item("Rent", BudgetCategoryType.Expense, 1000);
        var salary = Item("Salary", BudgetCategoryType.Income, 5000);
        var budget = Budget(rent, salary);
        var actualByTag = new Dictionary<Guid, decimal>
        {
            [rent.TransactionTagId] = -1000m,
            [salary.TransactionTagId] = 4800m,
        };

        var expense = BudgetAllocationSlices.Actual(budget, BudgetCategoryType.Expense, actualByTag,
            BudgetAllocationSlices.Planned(budget, BudgetCategoryType.Expense));
        var income = BudgetAllocationSlices.Actual(budget, BudgetCategoryType.Income, actualByTag,
            BudgetAllocationSlices.Planned(budget, BudgetCategoryType.Income));

        Assert.Equal(1000m, Assert.Single(expense).Value);
        Assert.Equal(4800m, Assert.Single(income).Value);
    }

    [Fact]
    public void Items_sharing_a_tag_name_sum_into_one_slice()
    {
        var a = Item("Food", BudgetCategoryType.Expense, 200);
        var b = Item("Food", BudgetCategoryType.Expense, 100);
        var budget = Budget(a, b);
        var planned = BudgetAllocationSlices.Planned(budget, BudgetCategoryType.Expense);

        var actual = BudgetAllocationSlices.Actual(budget, BudgetCategoryType.Expense,
            new Dictionary<Guid, decimal> { [a.TransactionTagId] = -50m, [b.TransactionTagId] = -30m }, planned);

        Assert.Equal(300m, Assert.Single(planned).Value);
        Assert.Equal(80m, Assert.Single(actual).Value);
    }

    [Theory]
    [InlineData(false, "var(--finance-income)")]
    [InlineData(true, "var(--mud-palette-text-secondary)")]
    public void The_record_accent_follows_status(bool archived, string expected)
    {
        Assert.Equal(expected, BudgetBalanceVisuals.Accent(archived));
        Assert.Equal($"color-mix(in srgb, {expected} 14%, transparent)", BudgetBalanceVisuals.AccentSoft(archived));
    }

    [Fact]
    public void The_actual_donut_announces_that_it_is_loading()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var cut = ctx.Render<BudgetDonutPair>(p => p
            .Add(c => c.Income, true)
            .Add(c => c.PlannedSlices, [])
            .Add(c => c.ActualSlices, [])
            .Add(c => c.HasReport, false)
            .Add(c => c.PlannedFormat, (v, _) => v.ToString())
            .Add(c => c.ActualFormat, (v, _) => v.ToString()));

        var status = cut.Find("[role=status]");
        Assert.Equal("polite", status.GetAttribute("aria-live"));
        Assert.Equal("true", status.GetAttribute("aria-busy"));
        Assert.Contains("Loading matched transactions", status.TextContent);
        // The terminal empty line is plain text: it is a state, not an event.
        Assert.DoesNotContain(cut.FindAll("[role=status]"), e => e.TextContent.Contains("No planned"));
    }
}
