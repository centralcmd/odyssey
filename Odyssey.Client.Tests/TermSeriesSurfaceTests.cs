using System.Globalization;
using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
/// Issue #57 §9 names THREE sites that must group by series (now the label alone) — the term service,
/// the account read projection, and the client's own local recompute. The first two are covered by
/// service and API tests; this file is the third. It matters because the recompute is not a shared
/// helper: it is its own implementation inside <c>AccountTermsSection</c>, so a server that groups
/// per series and a client that grouped any other way would disagree the moment a second labelled fee
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
public class TermSeriesSurfaceTests
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

    private static ExistingTerm Fee(string label, decimal value, DateTime effectiveFrom) => new()
    {
        TermId = Guid.NewGuid(),
        AccountId = AccountId,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
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
    public void A_tile_is_named_by_its_label_with_no_kind_caption()
    {
        // Every term is one kind of thing, so a caption naming a kind would say nothing.
        var cut = RenderSection(Card(), [Fee("Interest rate", 0.2249m, Past(30))]);

        var tile = cut.Find(".trm-tile");

        Assert.Equal("Interest rate", tile.QuerySelector(".trm-tile-name")!.TextContent.Trim());
        Assert.Null(tile.QuerySelector(".trm-kind-caption"));
    }

    [Fact]
    public void The_section_draws_no_rate_chart()
    {
        // The headline rate and its step chart are withdrawn until they are reintroduced on a new
        // basis; a term named "Interest rate" renders like any other term.
        var cut = RenderSection(Card(), [Fee("Interest rate", 0.2249m, Past(30))]);

        Assert.Empty(cut.FindAll(".trm-hero"));
        Assert.Empty(cut.FindAll("svg.trm-chart"));
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
    public void A_term_with_no_name_is_refused_before_a_request_is_made()
    {
        var (cut, client) = RenderDialog(Card(AccountType.Cash), []);

        Submit(cut);

        Assert.Contains(
            "Name this term so it keeps its own history.",
            cut.Markup,
            StringComparison.Ordinal);
        client.Verify(
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()),
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
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()),
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
            c => c.AddTermAsync(AccountId, It.Is<NewTerm>(t => t.Label == "ATM · domestic"), It.IsAny<CancellationToken>()),
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
            c => c.AddTermAsync(AccountId, It.Is<NewTerm>(t => t.Label == "ATM Abroad"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── The record card's tile caption ───────────────────────────────────────

    [Fact]
    public void The_record_cards_tile_foot_states_the_date_and_the_cadence()
    {
        var term = Fee("Annual card fee", 95m, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        term.Interval = Interval.Annually;
        term.IntervalCount = 1;

        var foot = AccountsCard.TermFoot(term);

        Assert.StartsWith("since ", foot, StringComparison.Ordinal);
        // The cadence in words, from the one shared helper — not a chip label.
        Assert.EndsWith(" · annually", foot, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_time_fee_states_no_period_in_its_foot()
    {
        // "One-time" is the absence of a period, not a period; saying it would be noise.
        var term = Fee("Card replacement", 15m, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        term.Interval = Interval.OneTime;

        Assert.DoesNotContain("One-time", AccountsCard.TermFoot(term), StringComparison.Ordinal);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static IRenderedComponent<AccountTermsSection> RenderSection(
        ExistingAccount account, IReadOnlyList<ExistingTerm> terms)
    {
        var ctx = NewContext();
        var client = new Mock<IAccountsApiClient>();
        client
            .Setup(c => c.ListTermsAsync(account.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult<List<ExistingTerm>>.Success([.. terms], HttpStatusCode.OK));
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


    // ── The cadence sub-form in the New / Edit dialog (issue #120) ────────────
    //
    // The count field's PRESENCE, the bound it validates against, and the wiring that ties the
    // "Applies every 3 months." echo to the control it describes are all logic with no other test
    // site: the fields are private state inside AddTermDialog, and what they promise is markup.
    // The aria-describedby assertions below are the regression test for the WCAG 1.3.1 fix — they
    // fail against the version where the echo was a loose sibling div.

    /// <summary>
    /// A new term opens on Monthly, which is periodic, so the count field is there from the start and
    /// the echo names the identity cadence rather than waiting for a typed value.
    /// </summary>
    [Fact]
    public void A_new_fee_opens_with_the_count_field_and_an_echo_of_the_identity_cadence()
    {
        var (cut, _) = RenderDialog(Card(), []);

        Assert.NotNull(FindInput(cut, "Every"));
        Assert.Equal("Applies monthly.", CadenceEcho(cut));
    }

    /// <summary>
    /// The count is ABSENT for a non-periodic unit, not merely disabled: "how many of them between
    /// charges" has no meaning for an occasion, and the request writes null. The picker carries the
    /// wording instead, in its own Help slot so it reaches assistive technology.
    /// </summary>
    [Theory]
    [InlineData(Interval.PerOccurrence, "Applies per occurrence")]
    [InlineData(Interval.PerUnit, "Applies per unit")]
    public void A_non_periodic_unit_has_no_count_field_and_says_so_on_the_picker(Interval interval, string hint)
    {
        var term = Fee("ATM · abroad", 25m, Past(30));
        term.Interval = interval;

        var (cut, _) = RenderDialog(Card(), [term], editing: term);

        Assert.Null(FindInput(cut, "Every"));
        Assert.Null(CadenceEcho(cut));
        Assert.Contains(hint, cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A one-time charge is the absence of a rhythm, so it gets no cadence wording at all.</summary>
    [Fact]
    public void A_one_time_fee_states_no_cadence_in_the_dialog()
    {
        var term = Fee("Card replacement", 15m, Past(30));
        term.Interval = Interval.OneTime;

        var (cut, _) = RenderDialog(Card(), [term], editing: term);

        Assert.Null(FindInput(cut, "Every"));
        Assert.Null(CadenceEcho(cut));
        Assert.DoesNotContain("Applies one-time", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A stored multi-unit cadence round-trips into the dialog and is echoed in words.</summary>
    [Fact]
    public void Editing_a_multi_unit_cadence_echoes_it_in_words()
    {
        var term = Fee("Account maintenance", 45m, Past(30));
        term.Interval = Interval.Monthly;
        term.IntervalCount = 3;

        var (cut, _) = RenderDialog(Card(), [term], editing: term);

        Assert.Equal("3", FindInput(cut, "Every")!.GetAttribute("value"));
        Assert.Equal("Applies every 3 months.", CadenceEcho(cut));
    }

    /// <summary>
    /// The WCAG 1.3.1 regression guard. The echo is what the typed count MEANS, so the field has to
    /// name it through aria-describedby — a sighted user sees it appear inline, and before the fix
    /// it was a sibling div tied to nothing. The id must also resolve to a node that is actually
    /// rendered: a reference to an absent element is a dangling one.
    /// </summary>
    [Fact]
    public void The_count_field_describes_itself_with_the_cadence_echo()
    {
        var (cut, _) = RenderDialog(Card(), []);

        var described = FindInput(cut, "Every")!.GetAttribute("aria-describedby")?.Split(' ') ?? [];
        var echo = cut.Find(".trm-cadence-echo");

        Assert.Contains(echo.Id, described);
        Assert.Equal("Applies monthly.", echo.TextContent.Trim());
    }

    /// <summary>
    /// Its other half: when the echo is withheld the field must not still point at it. The count's
    /// own help and unit descriptions survive, so this is about the dangling id, not about the
    /// attribute disappearing.
    /// </summary>
    [Fact]
    public void The_count_field_names_no_echo_while_the_echo_is_withheld()
    {
        var (cut, _) = RenderDialog(Card(), []);

        // Out of range, so the echo gives way to the error. The count is validated on SUBMIT, not on
        // input — typing alone only clears the previous error — so the submit is what withholds it.
        Type(cut, "Name", "Account maintenance");
        Type(cut, "Value", "45");
        Type(cut, "Every", "0");
        Submit(cut);

        Assert.Empty(cut.FindAll(".trm-cadence-echo"));

        var described = FindInput(cut, "Every")!.GetAttribute("aria-describedby")?.Split(' ') ?? [];
        Assert.All(described, id => Assert.NotEmpty(cut.FindAll($"#{id}")));
    }

    /// <summary>
    /// The count is validated against the SHARED bound, so the message cannot quote a number the
    /// server would not enforce. Both ends, and a fractional value, are refused.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("1.5")]
    public void A_count_outside_the_shared_bound_is_refused_with_the_bound_in_the_message(string typed)
    {
        var (cut, client) = RenderDialog(Card(), []);

        Type(cut, "Name", "Account maintenance");
        Type(cut, "Value", "45");
        Type(cut, "Every", typed);
        Submit(cut);

        Assert.Contains(
            $"between {TermIntervalCount.Min} and {TermIntervalCount.Max}",
            cut.Markup,
            StringComparison.Ordinal);

        client.Verify(
            c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A periodic unit left blank posts the identity cadence, not null — the same value the service
    /// would have stored, so a read-modify-write of the row it creates does not then trip the
    /// "count only for a periodic interval" rule.
    /// </summary>
    [Fact]
    public void A_blank_count_posts_the_identity_cadence()
    {
        var (cut, client) = RenderDialog(Card(), []);

        Type(cut, "Name", "Paper statement");
        Type(cut, "Value", "2");
        Submit(cut);

        client.Verify(c => c.AddTermAsync(
            It.IsAny<Guid>(),
            It.Is<NewTerm>(t => t.Interval == Interval.Monthly && t.IntervalCount == TermIntervalCount.Min),
            It.IsAny<CancellationToken>()));
    }

    /// <summary>The echo's text, or null when it is not rendered.</summary>
    private static string? CadenceEcho(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll(".trm-cadence-echo").SingleOrDefault()?.TextContent.Trim();

    private static (IRenderedComponent<DialogHost> Cut, Mock<IAccountsApiClient> Client) RenderDialog(
        ExistingAccount account, IReadOnlyList<ExistingTerm> existing, ExistingTerm? editing = null)
    {
        var ctx = NewContext();
        var client = new Mock<IAccountsApiClient>();
        client
            .Setup(c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        client
            .Setup(c => c.UpdateTermAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Account, account)
            .Add(h => h.Existing, existing)
            .Add(h => h.Term, editing));

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
        // AddTermDialog serves BOTH owners of the Term table since issue #135, so it injects the
        // contracts client too. These tests drive the account owner; the default mock is never
        // reached, and a test that did reach it would fail loudly rather than silently pass.
        ctx.Services.AddSingleton(Mock.Of<IContractsApiClient>());
        return ctx;
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingAccount Account { get; set; } = default!;

        [Parameter] public IReadOnlyList<ExistingTerm> Existing { get; set; } = [];

        /// <summary>The term to EDIT, or null for the create dialog. Editing is the only way to put
        /// the cadence fields into a chosen state without driving MudSelect's popover.</summary>
        [Parameter] public ExistingTerm? Term { get; set; }

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
            builder.AddComponentParameter(6, nameof(AddTermDialog.Term), Term);
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
    /// The input a caption names. THREE shapes are in play: the Name field is an OdsField, which
    /// renders a real &lt;label&gt; inside a MudBlazor input control; the value field is an
    /// OdsAmountField / OdsMoneyField, whose caption is the "Value" heading above it, so the control
    /// carries its own aria-label instead; and the cadence count is an OdsNumberField, which is a
    /// plain <c>odc-input</c> inside an OdsFieldShell — no MudBlazor control wrapper at all, so the
    /// label is tied to it by <c>for</c>/<c>id</c> rather than by containment.
    /// </summary>
    private static AngleSharp.Dom.IElement? FindInput(IRenderedComponent<DialogHost> cut, string label) =>
        cut.FindAll("input")
            .FirstOrDefault(i => i.GetAttribute("aria-label")?.Contains(label, StringComparison.Ordinal) == true)
        ?? cut.FindAll("div.mud-input-control")
            .FirstOrDefault(control => control.QuerySelector("label")?.TextContent.Contains(label, StringComparison.Ordinal) == true)
            ?.QuerySelector("input")
        ?? cut.FindAll("label")
            .Where(l => l.TextContent.Trim().StartsWith(label, StringComparison.Ordinal))
            .Select(l => l.GetAttribute("for"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => cut.FindAll($"input#{id}").SingleOrDefault())
            .FirstOrDefault(input => input is not null);

    // Every dispatch finds its element and fires the event inside ONE renderer InvokeAsync, and the
    // test thread waits for the handler to finish. bUnit's plain Input()/Click() are fire-and-forget:
    // a handler that yields (MudBlazor's inputs do) could finish after the next step, so on a loaded
    // runner Submit read a stale field and posted nothing. Finding inside the same InvokeAsync also
    // stops a re-render retiring the handler id between the find and the dispatch.
    private static void Type(IRenderedComponent<DialogHost> cut, string label, string value) =>
        cut.InvokeAsync(() =>
        {
            var input = FindInput(cut, label)
                ?? throw new InvalidOperationException($"No input labelled '{label}'. Markup: {cut.Markup}");
            return input.InputAsync(new ChangeEventArgs { Value = value });
        }).GetAwaiter().GetResult();

    private static void Submit(IRenderedComponent<DialogHost> cut) =>
        cut.InvokeAsync(() => cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Create term", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs())).GetAwaiter().GetResult();
}
