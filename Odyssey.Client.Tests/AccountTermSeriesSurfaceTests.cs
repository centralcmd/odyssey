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
/// The client half of the series key, rendered rather than derived.
///
/// <para>
/// Issue #57 §9 names THREE sites that must group by <c>(TermKind, LabelKey)</c> — the term service,
/// the account read projection, and the client's own local recompute. The first two are covered by
/// service and API tests; this file is the third. It matters because the recompute is not a shared
/// helper: it is its own implementation inside <c>AccountTermsSection</c>, so a server that groups
/// per series and a client that still groups per kind would disagree the moment a second labelled fee
/// existed — and the surface a user checks is this one.
/// </para>
///
/// <para>
/// These tests RENDER the components. <c>Recompute</c>, <c>SubmitAsync</c>'s validation and the record
/// card's tile caption are private, and the rules under test are about what the markup ends up saying,
/// so asserting on rendered text is both the reachable route and the honest one. The section loads its
/// terms on first expand (<c>ToggleOpen</c>), which is what lets a test drive it at all: its
/// <c>OnInitializedAsync</c> returns early outside the browser.
/// </para>
/// </summary>
public class AccountTermSeriesSurfaceTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    private static ExistingAccount Card(AccountType type = AccountType.CreditCard) => new()
    {
        AccountId = AccountId,
        Name = "Travel card",
        Description = "Rewards card",
        Opened = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        AccountType = type,
        CurrencyCode = "USD",
    };

    private static ExistingAccountTerm Fee(string label, decimal value, DateTime effectiveFrom) => new()
    {
        AccountTermId = Guid.NewGuid(),
        AccountId = AccountId,
        TermKind = TermKind.Fee,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static ExistingAccountTerm Rate(decimal value, DateTime effectiveFrom) => new()
    {
        AccountTermId = Guid.NewGuid(),
        AccountId = AccountId,
        TermKind = TermKind.InterestRate,
        ValueUnit = TermValueUnit.Percentage,
        Value = value,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static DateTime Past(int daysAgo) => DateTime.UtcNow.Date.AddDays(-daysAgo);

    // ── The third grouping site: the section's local recompute ───────────────

    [Fact]
    public void Two_labelled_fees_on_one_date_render_as_two_current_tiles()
    {
        var cut = RenderSection(Card(), [
            Fee("ATM · abroad", 25m, Past(30)),
            Fee("ATM · domestic", 5m, Past(30)),
        ]);

        var tiles = TileNames(cut);

        Assert.Equal(["ATM · abroad", "ATM · domestic"], tiles);
    }

    [Fact]
    public void Supersession_applies_within_one_label_and_leaves_its_sibling_alone()
    {
        var cut = RenderSection(Card(), [
            Fee("ATM · abroad", 25m, Past(400)),
            Fee("ATM · domestic", 5m, Past(400)),
            Fee("ATM · abroad", 30m, Past(30)),
        ]);

        // Three rows in history, two series in force.
        Assert.Equal(["ATM · abroad", "ATM · domestic"], TileNames(cut));

        var abroad = TileValueFor(cut, "ATM · abroad");
        var domestic = TileValueFor(cut, "ATM · domestic");

        Assert.Contains("30", abroad, StringComparison.Ordinal);
        Assert.Contains("5", domestic, StringComparison.Ordinal);
        Assert.DoesNotContain("25", abroad, StringComparison.Ordinal);
    }

    [Fact]
    public void A_future_dated_entry_is_not_yet_in_force()
    {
        var cut = RenderSection(Card(), [
            Fee("Annual card fee", 95m, Past(400)),
            Fee("Annual card fee", 120m, DateTime.UtcNow.Date.AddDays(30)),
        ]);

        Assert.Equal(["Annual card fee"], TileNames(cut));
        Assert.Contains("95", TileValueFor(cut, "Annual card fee"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlabelled_rate_and_a_labelled_fee_are_separate_series()
    {
        var cut = RenderSection(Card(), [
            Rate(0.2249m, Past(400)),
            Fee("Annual card fee", 95m, Past(400)),
        ]);

        // The rate is named by its kind wording — on a credit card, a liability, the #55 caption.
        Assert.Equal(["Interest charged", "Annual card fee"], TileNames(cut));
    }

    [Fact]
    public void A_labelled_tile_carries_the_kind_wording_as_a_caption_beneath_its_name()
    {
        // Criterion 14: both are TEXT, so no meaning rides on the glyph or its hue.
        var cut = RenderSection(Card(), [Fee("Paper statement", 2m, Past(30))]);

        var tile = cut.Find(".trm-tile");

        Assert.Equal("Paper statement", tile.QuerySelector(".trm-tile-name")!.TextContent.Trim());
        Assert.Equal("Fee", tile.QuerySelector(".trm-kind-caption")!.TextContent.Trim());
    }

    [Fact]
    public void An_unlabelled_rate_tile_has_no_kind_caption()
    {
        // Its name already IS the kind wording; a caption repeating it would be noise.
        var cut = RenderSection(Card(AccountType.SavingsAccount), [Rate(0.0325m, Past(30))]);

        var tile = cut.Find(".trm-tile");

        Assert.Equal("Interest rate", tile.QuerySelector(".trm-tile-kind")!.TextContent.Trim());
        Assert.Null(tile.QuerySelector(".trm-kind-caption"));
    }

    [Fact]
    public void History_rows_are_named_by_their_label_too()
    {
        var cut = RenderSection(Card(), [
            Fee("ATM · abroad", 25m, Past(400)),
            Fee("ATM · abroad", 30m, Past(30)),
        ]);

        var rowNames = cut.FindAll(".trm-row-kind-name").Select(n => n.TextContent.Trim()).ToList();

        Assert.Equal(["ATM · abroad", "ATM · abroad"], rowNames);
    }

    // ── The dialog's validation ──────────────────────────────────────────────

    [Fact]
    public void A_fee_with_no_name_is_refused_before_a_request_is_made()
    {
        // A cash account has only Fee to offer, so the form opens on it.
        var (cut, client) = RenderDialog(Card(AccountType.Cash), []);

        Submit(cut);

        Assert.Contains(
            "Name this fee so it keeps its own history.",
            cut.Markup,
            StringComparison.Ordinal);
        client.Verify(
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewAccountTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("atm · abroad")]
    [InlineData("  ATM   ·   abroad  ")]
    public void A_name_differing_only_by_case_or_spacing_is_caught_client_side(string colliding)
    {
        var existing = Fee("ATM · abroad", 25m, DateTime.UtcNow.Date);
        var (cut, client) = RenderDialog(Card(AccountType.Cash), [existing]);

        Type(cut, "Name", colliding);
        Type(cut, "Value", "30");
        Submit(cut);

        // The error names the label, and lands on the DATE field — which of several same-kind series
        // collided is the thing the user cannot otherwise tell.
        Assert.Contains("already has an entry on that date", cut.Markup, StringComparison.Ordinal);
        client.Verify(
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewAccountTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void A_differently_named_fee_on_the_same_date_is_allowed_through()
    {
        var existing = Fee("ATM · abroad", 25m, DateTime.UtcNow.Date);
        var (cut, client) = RenderDialog(Card(AccountType.Cash), [existing]);

        Type(cut, "Name", "ATM · domestic");
        Type(cut, "Value", "5");
        Submit(cut);

        client.Verify(
            c => c.AddTermAsync(AccountId, It.Is<NewAccountTerm>(t => t.Label == "ATM · domestic"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void The_posted_label_is_normalized_by_the_shared_rule()
    {
        var (cut, client) = RenderDialog(Card(AccountType.Cash), []);

        Type(cut, "Name", "  ATM   Abroad  ");
        Type(cut, "Value", "25");
        Submit(cut);

        client.Verify(
            c => c.AddTermAsync(AccountId, It.Is<NewAccountTerm>(t => t.Label == "ATM Abroad"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void The_kind_picker_is_not_rendered_when_one_kind_is_eligible()
    {
        // Criterion 19: a cash account has only Fee to offer, so the one-option grid is dropped — and
        // the form still opens on that kind and saves.
        var (cut, client) = RenderDialog(Card(AccountType.Cash), []);

        Assert.Empty(cut.FindAll(".trm-kind-grid"));

        Type(cut, "Name", "Safekeeping");
        Type(cut, "Value", "3");
        Submit(cut);

        client.Verify(
            c => c.AddTermAsync(AccountId, It.Is<NewAccountTerm>(t => t.TermKind == TermKind.Fee), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void The_kind_picker_is_rendered_when_a_rate_sits_beside_the_fee()
    {
        var (cut, _) = RenderDialog(Card(), []);

        Assert.NotEmpty(cut.FindAll(".trm-kind-grid"));
    }

    [Fact]
    public void The_name_field_is_rendered_for_a_fee_and_not_for_a_rate()
    {
        // A credit card offers both, and opens on the rate (registry order), which takes no label.
        var (cut, _) = RenderDialog(Card(), []);

        Assert.Null(FindInput(cut, "Name"));

        PickKind(cut, "Fee");
        Assert.NotNull(FindInput(cut, "Name"));

        PickKind(cut, "Interest rate");
        Assert.Null(FindInput(cut, "Name"));
    }

    [Fact]
    public void A_name_typed_on_a_fee_is_discarded_when_the_kind_switches_to_a_rate()
    {
        // A rate is refused a label, so a typed one must not be carried invisibly into a request the
        // server would reject.
        var (cut, _) = RenderDialog(Card(), []);

        PickKind(cut, "Fee");
        Type(cut, "Name", "ATM · abroad");
        PickKind(cut, "Interest rate");
        PickKind(cut, "Fee");

        Assert.Equal("", FindInput(cut, "Name")!.GetAttribute("value") ?? "");
    }

    // ── The record card's tile caption ───────────────────────────────────────

    [Fact]
    public void The_record_cards_tile_foot_leads_with_the_kind_wording_for_a_labelled_term()
    {
        var term = Fee("Annual card fee", 95m, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        term.BillingPeriod = BillingPeriod.Annually;

        var foot = AccountsCard.TermFoot(term, Card());

        Assert.StartsWith("Fee · since ", foot, StringComparison.Ordinal);
        Assert.EndsWith(" · Annually", foot, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_cards_tile_foot_omits_the_kind_wording_for_an_unlabelled_rate()
    {
        // The rate's NAME is already its kind wording, so repeating it in the foot would say it twice.
        var foot = AccountsCard.TermFoot(Rate(0.2249m, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc)), Card());

        Assert.StartsWith("since ", foot, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_time_fee_states_no_period_in_its_foot()
    {
        // "One-time" is the absence of a period, not a period; saying it would be noise.
        var term = Fee("Card replacement", 15m, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        term.BillingPeriod = BillingPeriod.OneTime;

        Assert.DoesNotContain("One-time", AccountsCard.TermFoot(term, Card()), StringComparison.Ordinal);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static IRenderedComponent<AccountTermsSection> RenderSection(
        ExistingAccount account, IReadOnlyList<ExistingAccountTerm> terms)
    {
        var ctx = NewContext();
        var client = new Mock<IAccountsApiClient>();
        client
            .Setup(c => c.ListTermsAsync(account.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingAccountTerm>>.Success([.. terms], HttpStatusCode.OK));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<AccountTermsSection>(p => p
            .Add(s => s.Account, account)
            .Add(s => s.CanWrite, false)
            .Add(s => s.FormatMoney, (decimal value, string? currency) =>
                value.ToString("0.##", CultureInfo.InvariantCulture) + " " + (currency ?? "USD")));

        // The section loads on first expand — outside the browser its OnInitializedAsync returns
        // early, so the click is what drives the load and the recompute under test.
        cut.Find(".odc-collapsible-trigger").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".trm-section")), TimeSpan.FromSeconds(10));

        return cut;
    }

    private static (IRenderedComponent<DialogHost> Cut, Mock<IAccountsApiClient> Client) RenderDialog(
        ExistingAccount account, IReadOnlyList<ExistingAccountTerm> existing)
    {
        var ctx = NewContext();
        var client = new Mock<IAccountsApiClient>();
        client
            .Setup(c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewAccountTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Account, account)
            .Add(h => h.Existing, existing));

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
        return ctx;
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingAccount Account { get; set; } = default!;

        [Parameter] public IReadOnlyList<ExistingAccountTerm> Existing { get; set; } = [];

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<AddTermDialog>(2);
            builder.AddComponentParameter(3, nameof(AddTermDialog.Account), Account);
            builder.AddComponentParameter(4, nameof(AddTermDialog.Existing), Existing);
            builder.AddComponentParameter(5, nameof(AddTermDialog.Open), true);
            builder.CloseComponent();
        }
    }

    /// <summary>The names on the Current terms tiles, in render order.</summary>
    private static List<string> TileNames(IRenderedComponent<AccountTermsSection> cut) =>
        cut.FindAll(".trm-tile .trm-tile-name, .trm-tile .trm-tile-kind")
            .Select(n => n.TextContent.Trim())
            .ToList();

    private static string TileValueFor(IRenderedComponent<AccountTermsSection> cut, string name) =>
        cut.FindAll(".trm-tile")
            .Single(t => t.QuerySelector(".trm-tile-name, .trm-tile-kind")!.TextContent.Trim() == name)
            .QuerySelector(".trm-tile-value")!
            .TextContent.Trim();

    /// <summary>
    /// The input a caption names. Two shapes are in play: the Name field is an OdsField, which renders
    /// a real &lt;label&gt;; the value field is an OdsAmountField / OdsMoneyField, whose caption is the
    /// "Value" heading above it, so the control carries its own aria-label instead.
    /// </summary>
    private static AngleSharp.Dom.IElement? FindInput(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("input")
            .FirstOrDefault(i => i.GetAttribute("aria-label")?.Contains(label, StringComparison.Ordinal) == true)
        ?? cut.FindAll("div.mud-input-control")
            .FirstOrDefault(control => control.QuerySelector("label")?.TextContent.Contains(label, StringComparison.Ordinal) == true)
            ?.QuerySelector("input");

    private static void Type(IRenderedComponent<DialogHost> cut, string label, string value)
    {
        var input = FindInput(cut, label)
            ?? throw new InvalidOperationException($"No input labelled '{label}'. Markup: {cut.Markup}");
        input.Input(value);
    }

    private static void PickKind(IRenderedComponent<DialogHost> cut, string kindLabel) =>
        cut.FindAll(".trm-kind-opt")
            .Single(b => b.TextContent.Contains(kindLabel, StringComparison.Ordinal))
            .Click();

    private static void Submit(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Create term", StringComparison.Ordinal))
            .Click();
}
