using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Finance;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The contract half of the term surface (issue #135), rendered rather than derived.
///
/// <para>
/// Three things are contract-specific and each is DRAWN, so each is asserted on the markup: the
/// eligible kinds (Fee and InterestRate, never ExpectedReturn), the currency that is a question
/// rather than a default, and the archived state, where the history stays readable while every write
/// affordance goes away. The fourth property under test is the one a per-owner regression would hide:
/// the shared rules — the series grouping, the supersession badges, the cadence wording — must read
/// identically to the account surface, because both run through the same helpers.
/// </para>
/// </summary>
public class ContractTermSurfaceTests
{
    private static readonly Guid ContractId = Guid.NewGuid();

    private static ExistingContract Lease(DateTime? archived = null) => new()
    {
        ContractId = ContractId,
        Name = "Maple St lease",
        Type = ContractType.Rental,
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Archived = archived,
    };

    private static ExistingTerm Fee(string label, decimal value, DateTime effectiveFrom, Interval? interval = null) => new()
    {
        TermId = Guid.NewGuid(),
        ContractId = ContractId,
        AccountId = null,
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "NOK",
        Interval = interval,
        IntervalCount = interval is not null && TermKindVisuals.IsPeriodic(interval) ? 1 : null,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static ExistingTerm Rate(decimal value, DateTime effectiveFrom) => new()
    {
        TermId = Guid.NewGuid(),
        ContractId = ContractId,
        AccountId = null,
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static DateTime Past(int daysAgo) => DateTime.UtcNow.Date.AddDays(-daysAgo);

    // ── The series key on the contract surface ───────────────────────────────

    [Fact]
    public void Two_labelled_charges_render_as_two_tiles_and_supersession_stays_within_a_series()
    {
        var cut = RenderSection(Lease(),
        [
            Fee("Monthly rent", 14000m, Past(700)),
            Fee("Monthly rent", 14500m, Past(200)),
            Fee("Service charge", 450m, Past(200)),
        ]);

        var tiles = cut.FindAll(".odc-infotile");
        Assert.Equal(2, tiles.Count);

        Assert.Contains("14,500", cut.Markup, StringComparison.Ordinal);
        // The superseded entry keeps its row and loses only its in-force badge. Counted in the history
        // table alone: the chart's text equivalent states every entry's state too.
        var history = cut.Find(".con-tbl-frame").InnerHtml;
        Assert.Equal(1, CountOccurrences(history, "Superseded"));
        Assert.Equal(2, CountOccurrences(history, "In force"));
    }

    [Fact]
    public void A_future_entry_reads_as_scheduled_and_never_as_in_force()
    {
        var cut = RenderSection(Lease(),
        [
            Fee("Monthly rent", 14500m, Past(200)),
            Fee("Monthly rent", 15500m, DateTime.UtcNow.Date.AddDays(90)),
        ]);

        Assert.Contains("Scheduled", cut.Markup, StringComparison.Ordinal);
        // One tile: the scheduled entry is not in force yet.
        Assert.Single(cut.FindAll(".odc-infotile"));
    }

    [Fact]
    public void A_contract_whose_every_entry_is_scheduled_says_so_rather_than_reading_as_empty()
    {
        var cut = RenderSection(Lease(), [Fee("Monthly rent", 15500m, DateTime.UtcNow.Date.AddDays(90))]);

        Assert.Contains("Nothing in force today", cut.Markup, StringComparison.Ordinal);
        // The history is still there — this is a different state from "no terms recorded".
        Assert.Contains("Term history", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_contract_with_no_terms_shows_the_empty_line_and_no_history_section()
    {
        var cut = RenderSection(Lease(), []);

        Assert.Contains("No terms yet", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Term history", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unlabelled interest rate on a contract reads as its registry wording — never as the
    /// account surface's "Interest charged", which is a LIABILITY's cost-rate wording and has no
    /// meaning here. One helper, one nullable owner context.
    /// </summary>
    [Fact]
    public void An_unlabelled_rate_reads_as_the_plain_kind_and_not_as_a_cost_rate()
    {
        var cut = RenderSection(Lease(), [Rate(0.0325m, Past(100))]);

        Assert.Contains("Interest rate", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Interest charged", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The cadence comes from the one helper every surface reads.</summary>
    [Fact]
    public void A_periodic_charge_states_its_cadence_beside_the_date()
    {
        var cut = RenderSection(Lease(), [Fee("Monthly rent", 14500m, Past(100), Interval.Monthly)]);

        Assert.Contains("monthly", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ── The archive state changes nothing here ───────────────────────────────

    /// <summary>
    /// <b>Archiving does not lock a contract's terms.</b> The section keeps its rows, its per-row
    /// Edit and Delete, and states no refusal — archival hides a contract from the default list, and
    /// the final rent or the closing fee is exactly what gets recorded after an agreement has ended.
    ///
    /// <para>
    /// The section no longer takes an archive flag at all, so this renders an archived contract and
    /// asserts it is indistinguishable from a live one. The per-contract cap is the only thing that
    /// refuses a term write, and it is enforced server-side.
    /// </para>
    /// </summary>
    [Fact]
    public void An_archived_contract_still_records_and_edits_its_terms()
    {
        var cut = RenderSection(Lease(archived: Past(5)), [Fee("Monthly rent", 14500m, Past(100))], canWrite: true);

        Assert.Contains("Monthly rent", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("td.trm-cell-act"));
        Assert.DoesNotContain("restoring", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // No notice band of ANY kind. Selected by role rather than by class: the retired
        // `.con-trm-notice` and the shared `.odc-recordsection-notice` both carry role="note", so a
        // reintroduced refusal is caught whichever of the two shapes it comes back in — naming one
        // class would pass vacuously against the other.
        Assert.Empty(cut.FindAll("[role='note']"));
    }

    [Fact]
    public void An_active_contract_offers_the_per_row_actions_to_a_writer_and_not_to_a_reader()
    {
        var writer = RenderSection(Lease(), [Fee("Monthly rent", 14500m, Past(100))], canWrite: true);
        Assert.NotEmpty(writer.FindAll("td.trm-cell-act"));

        var reader = RenderSection(Lease(), [Fee("Monthly rent", 14500m, Past(100))], canWrite: false);
        Assert.Empty(reader.FindAll("td.trm-cell-act"));
    }

    /// <summary>
    /// Every icon in the history table is decorative — the kind and the status are both written out
    /// beside their glyph — so none of them may be announced. A Material Icons span without
    /// <c>aria-hidden</c> reads its ligature text aloud, which on the "In force" badge would be once
    /// per in-force row (WCAG 1.1.1, Level A).
    /// </summary>
    [Fact]
    public void Every_icon_in_the_history_table_is_hidden_from_assistive_technology()
    {
        var cut = RenderSection(Lease(),
        [
            Fee("Monthly rent", 14000m, Past(700)),
            Fee("Monthly rent", 14500m, Past(200)),
            Fee("Service charge", 450m, DateTime.UtcNow.Date.AddDays(90)),
        ]);

        // All three temporal states are on screen, so the badge icons of each are in scope.
        var icons = cut.FindAll(".trm-tbl .material-icons");
        Assert.NotEmpty(icons);
        Assert.All(icons, icon => Assert.Equal("true", icon.GetAttribute("aria-hidden")));
    }

    // ── The "New term" request, which arrives as data rather than through a ref ──

    /// <summary>
    /// The row action menu's "New term" reaches this section as a TOKEN parameter. The mechanism
    /// matters: an <c>@ref</c> is rebound on the next render, so immediately after expanding another
    /// record it still points at the previously expanded section — opening through it would put the
    /// dialog on the wrong contract and drop the click. A token is handed only to the matching row,
    /// so the section that receives it is that contract's by construction.
    /// </summary>
    [Fact]
    public void A_request_token_opens_the_create_dialog()
    {
        var cut = RenderSection(Lease(), [Fee("Monthly rent", 14500m, Past(100))], canWrite: true);
        Assert.Empty(cut.FindComponents<AddTermDialog>());

        cut.Render(p => p.Add(s => s.NewTermRequestToken, Guid.NewGuid()));

        var dialog = Assert.Single(cut.FindComponents<AddTermDialog>());
        Assert.True(dialog.Instance.Open);
        // A CREATE, not an edit — the row menu's action never carries a term.
        Assert.Null(dialog.Instance.Term);
    }

    /// <summary>
    /// One ask, one dialog. A re-render with an unchanged token must not re-open — otherwise any
    /// unrelated parameter change on the host would reopen a dialog the reader had dismissed.
    /// </summary>
    [Fact]
    public void An_unchanged_token_does_not_reopen_the_dialog()
    {
        var cut = RenderSection(Lease(), [Fee("Monthly rent", 14500m, Past(100))], canWrite: true);
        var token = Guid.NewGuid();

        cut.Render(p => p.Add(s => s.NewTermRequestToken, token));
        var first = Assert.Single(cut.FindComponents<AddTermDialog>()).Instance;

        cut.Render(p => p.Add(s => s.NewTermRequestToken, token));

        // Same instance: a second OpenNew would mint a new @key and replace it.
        Assert.Same(first, Assert.Single(cut.FindComponents<AddTermDialog>()).Instance);
    }

    /// <summary>A second ask is a new token, and opens a fresh dialog.</summary>
    [Fact]
    public void A_new_token_opens_the_dialog_again()
    {
        var cut = RenderSection(Lease(), [Fee("Monthly rent", 14500m, Past(100))], canWrite: true);

        cut.Render(p => p.Add(s => s.NewTermRequestToken, Guid.NewGuid()));
        var first = Assert.Single(cut.FindComponents<AddTermDialog>()).Instance;

        cut.Render(p => p.Add(s => s.NewTermRequestToken, Guid.NewGuid()));

        Assert.NotSame(first, Assert.Single(cut.FindComponents<AddTermDialog>()).Instance);
    }

    // ── The dialog's contract-specific rules ─────────────────────────────────

    /// <summary>
    /// Expected return prices invested principal, which a contract does not hold — so it is not
    /// offered at all, rather than offered and refused.
    /// </summary>
    [Fact]
    public void The_dialog_offers_fee_and_interest_rate_only()
    {
        var cut = RenderDialog(Lease());

        var offered = cut.FindAll(".odc-cardsel-opt").Select(o => o.TextContent).ToList();

        Assert.Contains(offered, o => o.Contains("Fee", StringComparison.Ordinal));
        Assert.Contains(offered, o => o.Contains("Interest rate", StringComparison.Ordinal));
        // Not merely refused on submit — not offered at all.
        Assert.DoesNotContain(offered, o => o.Contains("Expected return", StringComparison.Ordinal));

        // And the omission is explained rather than silent.
        Assert.Contains("Expected return prices invested principal", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The currency is a QUESTION on a contract: the picker opens unset with its own placeholder, and
    /// the helper says why rather than leaving the blank unexplained.
    /// </summary>
    [Fact]
    public void The_dialog_opens_with_an_unset_currency_and_explains_why()
    {
        var cut = RenderDialog(Lease());

        var markup = cut.Markup;
        Assert.Contains("Pick", markup, StringComparison.Ordinal);
        Assert.Contains("has no currency of its own", markup, StringComparison.Ordinal);
    }

    /// <summary>Submitting an amount with no currency is refused inline, before any request.</summary>
    [Fact]
    public void Submitting_an_amount_without_a_currency_is_refused_and_posts_nothing()
    {
        var (cut, client) = RenderDialogWithClient(Lease());

        var value = cut.FindAll("input")
            .First(i => i.GetAttribute("aria-label")?.Contains("Value", StringComparison.Ordinal) == true);
        value.Input("14500");

        cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Create term", StringComparison.Ordinal))
            .Click();

        Assert.Contains("has no currency of its own", cut.Markup, StringComparison.Ordinal);
        client.Verify(
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// <b>The dialog posts on an ARCHIVED contract.</b> This is the half the section-level tests
    /// cannot reach: they assert the section renders writable and the row menu offers <b>New term</b>,
    /// but neither would catch a dialog that opens, fills and then refuses on submit.
    ///
    /// <para>
    /// That is exactly what happened — <c>AddTermDialog</c> carried its own restated archive guard,
    /// written when the server had one, whose own comment called itself "the second line, not the
    /// first". Removing the server guard and the row-menu gate left it as the only refusal on the
    /// path: the menu offered the write and the dialog dead-ended it. A guard restated in a second
    /// place is a guard that outlives the first, so this pins the submit itself.
    /// </para>
    /// </summary>
    [Fact]
    public void The_dialog_posts_a_term_on_an_archived_contract()
    {
        var (cut, client) = RenderDialogWithClient(Lease(archived: Past(5)));

        // A PERCENTAGE term, so the contract's own "an amount must name a currency" rule — which is
        // a separate refusal with its own test above — cannot be what decides this one.
        cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Percentage", StringComparison.Ordinal))
            .Click();

        // A fee is NAMED, so its series keeps its own history; the rule has its own coverage and is
        // satisfied here rather than left to decide this test. OdsField labels its input through a
        // <label for>, so the field is reached the way a user reaches it rather than by position.
        var nameFor = cut.FindAll("label")
            .First(l => l.TextContent.Contains("Name", StringComparison.Ordinal))
            .GetAttribute("for");
        // The contract's name field SUGGESTS rather than constrains: a typed name commits on blur, so
        // a new charge is written without ever being picked from the list.
        var name = cut.Find($"#{nameFor}");
        name.Input("Late-payment interest");
        cut.Find($"#{nameFor}").Blur();

        var value = cut.FindAll("input")
            .First(i => i.GetAttribute("aria-label")?.Contains("Value", StringComparison.Ordinal) == true);
        value.Input("3.25");

        cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Create term", StringComparison.Ordinal))
            .Click();

        var errors = cut.FindAll(".odc-field-error, .mud-input-error, [aria-invalid='true']");
        Assert.DoesNotContain("archived", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restore", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(errors);
        client.Verify(
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── The terms chart (design system · TermHistoryChart) ──────────────────

    /// <summary>
    /// The history leads with the chart, opening on the series whose LATEST entry is the most recent —
    /// a scheduled entry counts, because it is the change the reader came to look at.
    /// </summary>
    [Fact]
    public void The_history_leads_with_a_chart_opening_on_the_most_recently_changed_series()
    {
        var cut = RenderSection(Lease(),
        [
            Fee("Monthly rent", 2150m, Past(300), Interval.Monthly),
            Fee("Parking space", 85m, Past(300), Interval.Monthly),
            Fee("Parking space", 95m, Past(20), Interval.Monthly),
        ]);

        Assert.Single(cut.FindAll(".odc-thc.trm-seriesplot"));
        var legend = cut.FindAll(".odc-sc-leg");
        Assert.Single(legend);
        Assert.Contains("Parking space", legend[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Chart_series_group_by_unit_and_currency_and_state_the_value_in_force()
    {
        var eur = Fee("Service", 40m, Past(100), Interval.Monthly);
        eur.CurrencyCode = "EUR";
        var terms = new List<ExistingTerm>
        {
            Fee("Monthly rent", 2150m, Past(300), Interval.Monthly),
            Fee("Monthly rent", 2350m, DateTime.UtcNow.Date.AddDays(60), Interval.Monthly),
            Rate(0.08m, Past(200)),
            eur,
        };

        var series = ContractTermsSection.BuildChartSeries(
            terms, DateTime.UtcNow.Date, (v, c) => $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {c}");

        // The rent's latest entry is scheduled, so it leads — but it STATES the entry in force.
        Assert.Equal("Monthly rent", series[0].Label);
        Assert.Equal("2150 NOK", series[0].Value);
        Assert.Equal(2, series[0].Points.Count);
        Assert.Equal("amt:NOK", series[0].Group);
        Assert.Equal("amt:EUR", series.Single(s => s.Label == "Service").Group);
        Assert.Equal("pct", series.Single(s => s.Label == "Interest rate").Group);
        // A contract rate states its direction, like a fee.
        Assert.Equal("Outgoing", series.Single(s => s.Label == "Interest rate").ToneLabel);
    }

    // ── The name field's suggestions ─────────────────────────────────────────

    [Fact]
    public void The_name_field_suggests_this_contracts_own_series_of_the_kind_being_written()
    {
        var editing = Fee("Monthly rent", 2150m, Past(300), Interval.Monthly);
        var terms = new List<ExistingTerm>
        {
            editing,
            Fee("Monthly rent", 2250m, Past(30), Interval.Monthly),
            Fee("Water", 40m, DateTime.UtcNow.Date.AddDays(30), Interval.Monthly),
            Rate(0.08m, Past(200)),
        };

        var suggestions = AddTermDialog.NameSuggestions(terms, TermKind.Fee, editing.TermId, DateTime.UtcNow.Date);

        Assert.Equal(["Monthly rent", "Water"], suggestions.Select(s => s.Label));
        Assert.StartsWith("2,250.00 NOK · monthly", suggestions[0].Note, StringComparison.Ordinal);
        Assert.False(suggestions[0].Scheduled);
        // A series still entirely ahead says when it starts.
        Assert.True(suggestions[1].Scheduled);
        Assert.Contains("from ", suggestions[1].Note, StringComparison.Ordinal);
        // A rate is unlabelled, so it offers no name.
        Assert.Empty(AddTermDialog.NameSuggestions(terms, TermKind.InterestRate, null, DateTime.UtcNow.Date));
    }

    /// <summary>
    /// A contract RATE carries a direction too — an arrears rate charges, a deposit rate pays — so the
    /// value control keeps its direction lead and no refusal stands in its place.
    /// </summary>
    [Fact]
    public void A_contract_rate_is_asked_its_direction_like_a_fee()
    {
        var cut = RenderDialog(Lease());

        cut.FindAll(".odc-cardsel-opt")
            .Single(o => o.TextContent.Contains("Interest rate", StringComparison.Ordinal))
            .Click();

        Assert.Empty(cut.FindAll(".trm-dir-refused"));
        Assert.Contains("money leaves the household", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name field's error must land on the id its input's aria-describedby names. Help and error
    /// are therefore mutually exclusive: with the help line withdrawn, the error takes
    /// <c>trm-label-help</c> rather than a separate <c>-error</c> id nothing points at.
    /// </summary>
    [Fact]
    public void A_missing_name_error_is_the_node_the_input_is_described_by()
    {
        var cut = RenderDialog(Lease());

        Assert.Contains("Pick a charge this updates", cut.Find("#trm-label-help").TextContent, StringComparison.Ordinal);

        cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Create term", StringComparison.Ordinal))
            .Click();

        var described = cut.Find("#trm-label-help");
        Assert.Contains("Name this charge", described.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#trm-label-help-error"));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static IRenderedComponent<ContractTermsSection> RenderSection(
        ExistingContract contract, IReadOnlyList<ExistingTerm> terms, bool canWrite = false)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.ListTermsAsync(contract.ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTerm>>.Success([.. terms], HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<ContractTermsSection>(p => p
            .Add(s => s.Contract, contract)
            .Add(s => s.CanWrite, canWrite)
            .Add(s => s.FormatMoney, (decimal value, string? currency) =>
                value.ToString("#,##0.##", CultureInfo.InvariantCulture) + " " + (currency ?? "NOK")));

        // The section loads on OnInitializedAsync, which early-returns outside the browser (the
        // component has no InteractiveCheck seam and neither does the account one), so the load is
        // driven through the section's own public reload — the same entry point the host uses.
        cut.InvokeAsync(() => cut.Instance.ReloadAsync()).GetAwaiter().GetResult();

        return cut;
    }

    private static IRenderedComponent<DialogHost> RenderDialog(ExistingContract contract) =>
        RenderDialogWithClient(contract).Cut;

    private static (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Client) RenderDialogWithClient(
        ExistingContract contract)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p.Add(h => h.Contract, contract));
        return (cut, client);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // MudBlazor's own registrations supply ISnackbar and IDialogService; substituting a mock for
        // either breaks the providers the modal renders through.
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(Mock.Of<IReferenceDataCache>());
        // The dialog serves both owners, so it injects both typed clients. The contract tests
        // register a real mock for the contracts one and leave the accounts one unreachable.
        ctx.Services.AddSingleton(Mock.Of<IAccountsApiClient>());
        return ctx;
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingContract Contract { get; set; } = default!;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddTermDialog>(2);
            builder.AddComponentParameter(3, nameof(AddTermDialog.Contract), Contract);
            builder.AddComponentParameter(4, nameof(AddTermDialog.Open), true);
            builder.AddComponentParameter(5, nameof(AddTermDialog.Existing), (IReadOnlyList<ExistingTerm>)[]);
            builder.CloseComponent();
        }
    }
}
