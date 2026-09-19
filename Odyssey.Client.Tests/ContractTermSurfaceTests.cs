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

        var markup = cut.Markup;
        Assert.Contains("14,500", markup, StringComparison.Ordinal);
        // The superseded entry keeps its row and loses only its in-force badge.
        Assert.Equal(1, CountOccurrences(markup, "Superseded"));
        Assert.Equal(2, CountOccurrences(markup, "In force"));
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

    // ── The archive guard, drawn ─────────────────────────────────────────────

    [Fact]
    public void An_archived_contract_states_the_refusal_and_keeps_its_history_readable()
    {
        var cut = RenderSection(Lease(archived: Past(5)), [Fee("Monthly rent", 14500m, Past(100))], canWrite: true);

        var notice = cut.Find(".con-trm-notice.archived");
        Assert.Contains("restoring", notice.TextContent, StringComparison.OrdinalIgnoreCase);

        // The rows stay; only the per-row write affordances go.
        Assert.Contains("Monthly rent", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("td.trm-cell-act"));
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
            .Add(s => s.Archived, contract.Archived is not null)
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
