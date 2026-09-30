using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The Contracts page-header signal panel: <see cref="PageHeader"/>'s new grouping and custom-row
/// rendering, and the <see cref="ContractChargeRow"/> that rides in it.
///
/// <para>
/// These RENDER, because every rule here is a property of the markup rather than of a computation —
/// that the heading is a real heading, that a group heading is emitted once per run rather than once
/// per row, that the fragment the page supplies replaces the alert row instead of appearing beside
/// it, and that the caption a narrow viewport hides is still in the accessible name. A derivation
/// test could not tell any of those from their broken twins.
/// </para>
/// </summary>
public class ContractSignalPanelTests
{
    static ContractSignalPanelTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly Guid ContractId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static string Money(decimal value, string? code) =>
        $"{code} {value:0.00}";

    private static ContractUpcomingCharge Charge(
        string name = "Apartment Lease",
        string? label = "Monthly rent",
        int daysUntil = 3,
        ContractType type = ContractType.Rental) => new()
        {
            ContractId = ContractId,
            Name = name,
            Type = type,
            Label = label,
            Amount = 1850m,
            CurrencyCode = "USD",
            Interval = Interval.Monthly,
            IntervalCount = 1,
            ChargeDate = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc),
            DaysUntil = daysUntil,
        };

    private static IRenderedComponent<PageHeader> RenderPanel(params PageHeaderProblem[] problems)
    {
        var ctx = NewContext();
        return ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "Contracts")
            .Add(h => h.Problems, [.. problems])
            .Add(h => h.ProblemsLabel, "Upcoming")
            .Add(h => h.ProblemsOpen, true));
    }

    // ── PageHeader grouping ──────────────────────────────────────────────────

    /// <summary>
    /// The group heading must be a REAL heading, not a styled div. Without it the grouping is
    /// visual-only (WCAG 1.3.1) and a screen reader cannot tell "Recently expired" from "Next
    /// charges" — the whole point of splitting one panel into four groups.
    /// </summary>
    [Fact]
    public void A_group_heading_renders_as_a_real_heading_element()
    {
        var panel = RenderPanel(new PageHeaderProblem { Group = "Ending soon", Message = "Term ends soon." });

        var heading = panel.Find("h2.ph-signal-group");
        Assert.Equal("Ending soon", heading.TextContent.Trim());
    }

    /// <summary>
    /// One heading per RUN of rows sharing a group, not one per row: the page groups purely by the
    /// order it supplies rows in, so a repeated heading would mean the panel had silently stopped
    /// grouping.
    /// </summary>
    [Fact]
    public void A_group_heading_is_emitted_once_per_run_of_rows()
    {
        var panel = RenderPanel(
            new PageHeaderProblem { Group = "Ending soon", Message = "First." },
            new PageHeaderProblem { Group = "Ending soon", Message = "Second." },
            new PageHeaderProblem { Group = "Starting soon", Message = "Third." });

        var headings = panel.FindAll("h2.ph-signal-group").Select(h => h.TextContent.Trim()).ToList();

        Assert.Equal(["Ending soon", "Starting soon"], headings);
        Assert.Equal(3, panel.FindAll(".signal-panel .alert").Count);
    }

    /// <summary>A panel whose rows name no group renders exactly as it did before grouping existed.</summary>
    [Fact]
    public void A_panel_with_no_groups_renders_no_headings()
    {
        var panel = RenderPanel(new PageHeaderProblem { Message = "Something needs attention." });

        Assert.Empty(panel.FindAll(".ph-signal-group"));
        Assert.Single(panel.FindAll(".signal-panel .alert"));
    }

    /// <summary>
    /// A supplied <c>Row</c> REPLACES the alert row rather than appearing beside it — otherwise every
    /// charge would render twice, once as a sentence and once as a table row.
    /// </summary>
    [Fact]
    public void A_supplied_row_replaces_the_alert_row()
    {
        var panel = RenderPanel(new PageHeaderProblem
        {
            Group = "Upcoming transactions",
            Message = "Apartment Lease",
            Row = builder => builder.AddMarkupContent(0, "<div class=\"stand-in\">row</div>"),
        });

        Assert.Single(panel.FindAll(".stand-in"));
        Assert.Empty(panel.FindAll(".signal-panel .alert"));
    }

    /// <summary>
    /// The rows are static content inside a disclosure the reader just opened, not status messages.
    /// <c>role="status"</c> is an <c>aria-live</c> region, so leaving it on every row made opening a
    /// multi-group panel announce the lot.
    /// </summary>
    [Fact]
    public void An_alert_row_is_not_a_live_region()
    {
        var panel = RenderPanel(new PageHeaderProblem { Group = "Ending soon", Message = "Term ends soon." });

        var row = panel.Find(".signal-panel .alert");
        Assert.Null(row.GetAttribute("role"));
    }

    // ── The merged "Upcoming transactions" list ──────────────────────────────

    private static readonly Guid OtherContractId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static ContractSummary Summary(
        IEnumerable<ContractUpcomingCharge> charges, IEnumerable<ContractUpcomingCharge> receipts) => new()
    {
        CountsByStatus = new ContractStatusCounts(),
        RunRate = new ContractRunRate { BaseCurrency = "USD" },
        UpcomingCharges = [.. charges],
        UpcomingReceipts = [.. receipts],
    };

    /// <summary>
    /// Charges and receipts read as ONE list ordered by date, with each row's direction taken from
    /// the summary list it came from rather than from its position.
    /// </summary>
    [Fact]
    public void Upcoming_movements_interleave_charges_and_receipts_by_date()
    {
        var summary = Summary(
            charges: [Charge(name: "Rent", daysUntil: 9), Charge(name: "Power", daysUntil: 2)],
            receipts: [Charge(name: "Salary", daysUntil: 5)]);

        var movements = ContractsCard.UpcomingMovements(summary, new HashSet<Guid> { ContractId }).ToList();

        Assert.Equal(["Power", "Salary", "Rent"], movements.Select(m => m.Row.Name));
        Assert.Equal([false, true, false], movements.Select(m => m.Incoming));
    }

    /// <summary>
    /// On the same day a charge precedes a receipt, and rows within one list keep their server order —
    /// so the panel does not reshuffle between renders.
    /// </summary>
    [Fact]
    public void Same_day_movements_keep_charges_first_and_server_order_within_a_list()
    {
        var summary = Summary(
            charges: [Charge(name: "Fee A", daysUntil: 4), Charge(name: "Fee B", daysUntil: 4)],
            receipts: [Charge(name: "Interest", daysUntil: 4)]);

        var movements = ContractsCard.UpcomingMovements(summary, new HashSet<Guid> { ContractId }).ToList();

        Assert.Equal(["Fee A", "Fee B", "Interest"], movements.Select(m => m.Row.Name));
    }

    /// <summary>
    /// The summary is unfiltered, so a row naming a contract the active list filter excluded is
    /// dropped — its jump would otherwise scroll to nothing. Both directions are filtered.
    /// </summary>
    [Fact]
    public void Upcoming_movements_drop_contracts_the_list_is_not_showing()
    {
        var summary = Summary(
            charges: [Charge(name: "Shown"), Charge(name: "Hidden charge") with { ContractId = OtherContractId }],
            receipts: [Charge(name: "Hidden receipt") with { ContractId = OtherContractId }]);

        var movements = ContractsCard.UpcomingMovements(summary, new HashSet<Guid> { ContractId }).ToList();

        Assert.Equal(["Shown"], movements.Select(m => m.Row.Name));
    }

    /// <summary>No summary yet, or nothing due in the window: the group simply has no rows.</summary>
    [Fact]
    public void Upcoming_movements_are_empty_without_a_summary_or_without_rows()
    {
        var listed = new HashSet<Guid> { ContractId };

        Assert.Empty(ContractsCard.UpcomingMovements(null, listed));
        Assert.Empty(ContractsCard.UpcomingMovements(Summary([], []), listed));
    }

    // ── ContractChargeRow ────────────────────────────────────────────────────

    private static IRenderedComponent<ContractChargeRow> RenderCharge(ContractUpcomingCharge charge)
    {
        var ctx = NewContext();
        return ctx.Render<ContractChargeRow>(p => p
            .Add(r => r.Charge, charge)
            .Add(r => r.Money, Money));
    }

    /// <summary>
    /// A real <c>&lt;button&gt;</c>, not a div wearing <c>role="button"</c>: Enter, Space, focus and
    /// the role all come free, and Space does not scroll the page. Blazor resolves
    /// <c>@onkeydown:preventDefault</c> before the handler, so the div form cannot suppress that
    /// scroll on the first keystroke without a JS hop.
    /// </summary>
    [Fact]
    public void A_charge_row_is_a_real_button()
    {
        var row = RenderCharge(Charge()).Find(".con-charge-row");

        Assert.Equal("BUTTON", row.TagName);
        Assert.Equal("button", row.GetAttribute("type"));
    }

    /// <summary>
    /// The accessible name carries the series label, which the narrow (&lt;720px) layout drops
    /// visually. Assembling the name from the visible fragments alone would silently lose it at the
    /// width where the row is hardest to read.
    /// </summary>
    [Fact]
    public void A_charge_rows_accessible_name_keeps_the_label_the_narrow_layout_hides()
    {
        var row = RenderCharge(Charge(label: "Monthly rent")).Find(".con-charge-row");

        var label = row.GetAttribute("aria-label");
        Assert.Contains("Apartment Lease", label);
        Assert.Contains("Monthly rent", label);
        Assert.Contains("View contract", label);
    }

    /// <summary>A fee series with no label of its own must not leave a dangling comma in the name.</summary>
    [Fact]
    public void An_unlabelled_charge_omits_the_label_clause()
    {
        var row = RenderCharge(Charge(label: null)).Find(".con-charge-row");

        Assert.DoesNotContain(", ,", row.GetAttribute("aria-label"));
        Assert.Empty(row.QuerySelectorAll(".con-charge-term"));
    }

    /// <summary>
    /// The relative wording comes from the shared helper, so this caption and the "Starting soon"
    /// alert beside it in the same panel cannot phrase the same fact two ways.
    /// </summary>
    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "tomorrow")]
    [InlineData(12, "in 12 days")]
    public void A_charge_row_reads_its_day_relatively(int daysUntil, string expected)
    {
        var row = RenderCharge(Charge(daysUntil: daysUntil));

        Assert.Equal(expected, row.Find(".con-charge-rel").TextContent.Trim());
        Assert.Equal(expected, OdsRelativeDay.Ahead(daysUntil));
    }

    /// <summary>
    /// Charges and receipts share one date-ordered list, so the SIGNED amount carries the direction:
    /// "+" for money in, U+2212 for money out, over the magnitude. The accessible name says it in
    /// words, since a sign character is read inconsistently and colour alone may not carry it.
    /// </summary>
    [Theory]
    [InlineData(false, "\u2212USD 1850.00", "money out USD 1850.00 due")]
    [InlineData(true, "+USD 1850.00", "money in USD 1850.00 expected")]
    public void A_charge_rows_amount_is_signed_by_direction(bool incoming, string shown, string spoken) =>
        AssertSignedAmount(1850m, incoming, shown, spoken);

    /// <summary>
    /// The sign comes from the DIRECTION alone, over the magnitude: a stored negative amount must not
    /// double up into "−−" or flip a receipt to a minus. <c>Math.Abs</c> is what guarantees it, in both
    /// the visible amount and the accessible name.
    /// </summary>
    [Theory]
    [InlineData(false, "\u2212USD 1850.00", "money out USD 1850.00 due")]
    [InlineData(true, "+USD 1850.00", "money in USD 1850.00 expected")]
    public void A_negative_stored_amount_is_signed_by_direction_not_by_its_own_sign(
        bool incoming, string shown, string spoken) =>
        AssertSignedAmount(-1850m, incoming, shown, spoken);

    private static void AssertSignedAmount(decimal amount, bool incoming, string shown, string spoken)
    {
        var ctx = NewContext();
        var row = ctx.Render<ContractChargeRow>(p => p
            .Add(r => r.Charge, Charge() with { Amount = amount })
            .Add(r => r.Money, Money)
            .Add(r => r.Incoming, incoming));

        Assert.Equal(shown, row.Find(".con-charge-amt").TextContent.Trim());
        Assert.Contains(spoken, row.Find(".con-charge-row").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    /// <summary>The decorative type glyph and the "View" affordance are named by the row's own label.</summary>
    [Fact]
    public void The_decorative_fragments_are_hidden_from_assistive_technology()
    {
        var row = RenderCharge(Charge()).Find(".con-charge-row");

        Assert.Equal("true", row.QuerySelector(".con-charge-name .material-icons")!.GetAttribute("aria-hidden"));
        Assert.Equal("true", row.QuerySelector(".con-charge-go")!.GetAttribute("aria-hidden"));
    }
}
