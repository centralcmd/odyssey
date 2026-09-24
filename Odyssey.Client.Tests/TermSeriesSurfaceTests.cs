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
/// The client half of the series key and the cadence sub-form, in <c>AddTermDialog</c>: the
/// (label, effectiveFrom) duplicate guard on the SAME normalized key the server writes, and the count
/// field's presence, bound and echo. The rendered term surfaces are covered by
/// <see cref="ContractTermSurfaceTests"/> — a term has had no other owner since issue #190.
///
/// <para>
/// These tests RENDER the dialog. <c>SubmitAsync</c>'s validation is private, and the rules under test
/// are about what the markup ends up saying, so asserting on rendered text is both the reachable route
/// and the honest one.
/// </para>
/// </summary>
public class TermSeriesSurfaceTests
{
    private static readonly Guid ContractId = Guid.NewGuid();

    private static ExistingContract Card() => new()
    {
        ContractId = ContractId,
        Name = "Travel card agreement",
        Type = ContractType.Loan,
        StartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ExistingTerm Fee(string label, decimal value, DateTime effectiveFrom) => new()
    {
        TermId = Guid.NewGuid(),
        ContractId = ContractId,
        Label = label,
        ValueUnit = TermValueUnit.Amount,
        Value = value,
        CurrencyCode = "USD",
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static DateTime Past(int daysAgo) => DateTime.UtcNow.Date.AddDays(-daysAgo);

    // ── The dialog's validation ──────────────────────────────────────────────

    [Fact]
    public void A_term_with_no_name_is_refused_before_a_request_is_made()
    {
        var (cut, client) = RenderDialog(Card(), []);

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
        var (cut, client) = RenderDialog(Card(), [existing]);

        Percentage(cut);
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
        var (cut, client) = RenderDialog(Card(), [existing]);

        Percentage(cut);
        Type(cut, "Name", "ATM · domestic");
        Type(cut, "Value", "5");
        Submit(cut);

        client.Verify(
            c => c.AddTermAsync(ContractId, It.Is<NewTerm>(t => t.Label == "ATM · domestic"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void The_posted_label_is_normalized_by_the_shared_rule()
    {
        var (cut, client) = RenderDialog(Card(), []);

        Percentage(cut);
        Type(cut, "Name", "  ATM   Abroad  ");
        Type(cut, "Value", "25");
        Submit(cut);

        client.Verify(
            c => c.AddTermAsync(ContractId, It.Is<NewTerm>(t => t.Label == "ATM Abroad"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

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

        Percentage(cut);
        Type(cut, "Name", "Paper statement");
        Type(cut, "Value", "2");
        Submit(cut);

        client.Verify(c => c.AddTermAsync(
            It.IsAny<Guid>(),
            It.Is<NewTerm>(t => t.Interval == Interval.Monthly && t.IntervalCount == TermIntervalCount.Min),
            It.IsAny<CancellationToken>()));
    }

    /// <summary>
    /// A percentage's helper names its cadence after the stored fraction — the same words the echo and
    /// the tiles use — and names none when the term has no cadence.
    /// </summary>
    [Theory]
    [InlineData(Interval.Annually, "0.0500 · annually")]
    [InlineData(null, "0.0500")]
    public void The_percentage_helper_carries_the_cadence_when_there_is_one(Interval? interval, string expected)
    {
        var term = Fee("Management fee", 0.05m, Past(30));
        term.ValueUnit = TermValueUnit.Percentage;
        term.CurrencyCode = null;
        term.Interval = interval;
        term.IntervalCount = interval is null ? null : 1;
        var (cut, _) = RenderDialog(Card(), [term], editing: term);

        // The innermost element holding the helper, so the line ends where the helper does rather than
        // wherever the markup that follows it happens to break.
        var helper = cut.FindAll("*").LastOrDefault(e => e.TextContent.Contains("Stored as a fraction: ", StringComparison.Ordinal));
        Assert.True(helper is not null, "No fraction helper rendered.");
        var help = helper!.TextContent;
        var at = help.IndexOf("Stored as a fraction: ", StringComparison.Ordinal);
        var line = help[(at + "Stored as a fraction: ".Length)..].Split('\n')[0].Trim();

        Assert.Equal(expected, line);
    }

    /// <summary>The echo's text, or null when it is not rendered.</summary>
    private static string? CadenceEcho(IRenderedComponent<DialogHost> cut) =>
        cut.FindAll(".trm-cadence-echo").SingleOrDefault()?.TextContent.Trim();

    private static (IRenderedComponent<DialogHost> Cut, Mock<IContractsApiClient> Client) RenderDialog(
        ExistingContract contract, IReadOnlyList<ExistingTerm> existing, ExistingTerm? editing = null)
    {
        var ctx = NewContext();
        var client = new Mock<IContractsApiClient>();
        client
            .Setup(c => c.AddTermAsync(It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.Created));
        client
            .Setup(c => c.UpdateTermAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NewTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResult.Success(HttpStatusCode.NoContent));
        ctx.Services.AddSingleton(client.Object);

        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Contract, contract)
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
        return ctx;
    }

    /// <summary>The dialog beside MudBlazor's providers, which portal the modal it renders into.</summary>
    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public ExistingContract Contract { get; set; } = default!;

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
            builder.AddComponentParameter(3, nameof(AddTermDialog.Contract), Contract);
            builder.AddComponentParameter(4, nameof(AddTermDialog.Existing), Existing);
            builder.AddComponentParameter(5, nameof(AddTermDialog.Open), true);
            builder.AddComponentParameter(6, nameof(AddTermDialog.Term), Term);
            builder.CloseComponent();
        }
    }

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
    /// <remarks>
    /// The Name field is a free-text combobox that commits a typed name on blur, so the blur is part
    /// of typing into it — as it is for a user tabbing to the next field.
    /// </remarks>
    private static void Type(IRenderedComponent<DialogHost> cut, string label, string value)
    {
        cut.InvokeAsync(() =>
        {
            var input = FindInput(cut, label)
                ?? throw new InvalidOperationException($"No input labelled '{label}'. Markup: {cut.Markup}");
            return input.InputAsync(new ChangeEventArgs { Value = value });
        }).GetAwaiter().GetResult();

        if (label == "Name")
        {
            cut.InvokeAsync(() => FindInput(cut, label)!.FocusOutAsync(new FocusEventArgs())).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Switches the value to a percentage. A contract has no currency of its own, so an AMOUNT would be
    /// refused for its currency before the rule a test is about could decide anything.
    /// </summary>
    private static void Percentage(IRenderedComponent<DialogHost> cut) =>
        cut.InvokeAsync(() => cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Percentage", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs())).GetAwaiter().GetResult();

    private static void Submit(IRenderedComponent<DialogHost> cut) =>
        cut.InvokeAsync(() => cut.FindAll("button")
            .Single(b => b.TextContent.Contains("Create term", StringComparison.Ordinal))
            .ClickAsync(new MouseEventArgs())).GetAwaiter().GetResult();
}
