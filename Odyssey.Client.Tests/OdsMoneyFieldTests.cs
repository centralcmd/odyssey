using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// What <c>OdsMoneyField</c> actually RENDERS — the parts that are invisible to a unit test of its
/// helpers and were only ever confirmed by eye.
/// </summary>
/// <remarks>
/// The keystroke and parse rules live in <see cref="OdsMoneyText"/> and are covered by
/// <see cref="OdsMoneyTextTests"/>. What is pinned here is the markup contract the control makes with
/// assistive technology and with the keyboard: whether the amount has a name, whether the currency
/// list is one tab stop or a hundred, and whether an empty result says so out loud. Two of these are
/// regressions — the option buttons shipped without <c>tabindex="-1"</c>, so Tab walked every currency
/// in the ISO registry, and a label-less field had no programmatic name at all.
/// </remarks>
public class OdsMoneyFieldTests : IAsyncLifetime
{
    // One renderer per test (xUnit news up the class per case), disposed with it — a shared, undisposed
    // BunitContext leaks its renderer into whatever runs next in the same collection.
    private readonly BunitContext _ctx = new();

    // DisposeAsync, not Dispose: MudBlazor registers services that are IAsyncDisposable only, and the
    // synchronous path throws rather than tearing the container down.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private static readonly IReadOnlyList<OdsOption> Currencies =
    [
        new OdsOption("NOK", "Norwegian krone"),
        new OdsOption("USD", "US Dollar"),
        new OdsOption("SEK", "Swedish krona"),
    ];

    private IRenderedComponent<MoneyHost> Render(
        string? label = "Premium", string? ariaLabel = null, bool editable = true)
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();

        return _ctx.Render<MoneyHost>(p => p
            .Add(h => h.Label, label)
            .Add(h => h.AriaLabel, ariaLabel)
            .Add(h => h.Editable, editable));
    }

    // ── Accessible name ───────────────────────────────────────────────────────

    [Fact]
    public void A_visible_label_names_the_amount_through_a_real_label_element()
    {
        var cut = Render();

        var input = cut.Find("input.odc-money-input");
        var label = cut.Find("label.odc-field-label");

        Assert.Equal(input.GetAttribute("id"), label.GetAttribute("for"));
        // No competing second name: an aria-label would override the <label> the user can see.
        Assert.Null(input.GetAttribute("aria-label"));
    }

    [Fact]
    public void A_label_less_amount_is_named_by_AriaLabel()
    {
        // The regression: AddTermDialog's hero has a heading beside a unit toggle rather than a label
        // element, so without this the field had NO programmatic name at all.
        var cut = Render(label: null, ariaLabel: "Value");

        Assert.Equal("Value", cut.Find("input.odc-money-input").GetAttribute("aria-label"));
    }

    // ── The currency segment ──────────────────────────────────────────────────

    [Fact]
    public void A_locked_currency_renders_as_static_text_not_a_control()
    {
        var cut = Render(label: "Estimated value", editable: false);

        Assert.Equal("NOK", cut.Find(".odc-money-cur .odc-money-code").TextContent.Trim());
        Assert.Empty(cut.FindAll("button.odc-money-cur"));
    }

    [Fact]
    public void An_editable_currency_is_a_listbox_trigger_that_names_its_selection()
    {
        var cut = Render();

        var trigger = cut.Find("button.odc-money-cur");

        Assert.Equal("listbox", trigger.GetAttribute("aria-haspopup"));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Equal("Currency: NOK", trigger.GetAttribute("aria-label"));
    }

    // ── The popover ───────────────────────────────────────────────────────────

    [Fact]
    public void The_option_list_is_ONE_tab_stop_not_one_per_currency()
    {
        // The regression: without tabindex="-1" the roving-focus model the arrow keys implement was
        // contradicted by Tab, which stepped through every currency in the ISO registry one at a time.
        var cut = OpenPopover();

        var options = cut.FindAll("button.odc-money-opt");

        Assert.NotEmpty(options);
        Assert.All(options, o => Assert.Equal("-1", o.GetAttribute("tabindex")));
    }

    [Fact]
    public void An_option_row_carries_its_code_and_name_and_its_selected_state()
    {
        var cut = OpenPopover();

        var selected = cut.FindAll("button.odc-money-opt")
            .Single(o => o.GetAttribute("aria-selected") == "true");

        Assert.Equal("option", selected.GetAttribute("role"));
        Assert.Contains("NOK", selected.TextContent, StringComparison.Ordinal);
        Assert.Contains("Norwegian krone", selected.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_query_matching_nothing_ANNOUNCES_the_empty_result()
    {
        // Silent by default: a sighted user sees the list empty, a screen-reader user hears nothing at
        // all unless the node is a live region.
        var cut = OpenPopover(query: "zzz");

        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find(".odc-money-empty");
            Assert.Equal("status", empty.GetAttribute("role"));
            Assert.Equal("polite", empty.GetAttribute("aria-live"));
        });
    }

    [Fact]
    public void The_search_filters_on_the_currency_NAME_as_well_as_its_code()
    {
        var cut = OpenPopover(query: "kro");

        cut.WaitForAssertion(() => Assert.Equal(
            ["NOK", "SEK"],
            cut.FindAll("button.odc-money-opt .odc-money-opt-code").Select(c => c.TextContent.Trim())));
    }

    private IRenderedComponent<MoneyHost> OpenPopover(string? query = null)
    {
        var cut = Render();
        cut.Find("button.odc-money-cur").Click();

        if (query is not null)
            cut.Find(".odc-money-search input").Input(query);

        return cut;
    }

    /// <summary>The field beside MudBlazor's providers, which portal its popover.</summary>
    public sealed class MoneyHost : ComponentBase
    {
        [Parameter] public string? Label { get; set; } = "Premium";

        [Parameter] public string? AriaLabel { get; set; }

        [Parameter] public bool Editable { get; set; } = true;

        private string _value = "1234.56";

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsMoneyField>(1);
            builder.AddComponentParameter(2, nameof(OdsMoneyField.Id), "money");
            builder.AddComponentParameter(3, nameof(OdsMoneyField.Label), Label);
            builder.AddComponentParameter(4, nameof(OdsMoneyField.AriaLabel), AriaLabel);
            builder.AddComponentParameter(5, nameof(OdsMoneyField.Value), _value);
            builder.AddComponentParameter(6, nameof(OdsMoneyField.ValueChanged),
                EventCallback.Factory.Create<string>(this, v => _value = v));
            builder.AddComponentParameter(7, nameof(OdsMoneyField.Currency), "NOK");
            builder.AddComponentParameter(8, nameof(OdsMoneyField.CurrencyOptions), Currencies);
            builder.AddComponentParameter(9, nameof(OdsMoneyField.CurrencyEditable), Editable);
            builder.AddComponentParameter(10, nameof(OdsMoneyField.CurrencySearchThreshold), 0);
            if (Editable)
            {
                builder.AddComponentParameter(11, nameof(OdsMoneyField.CurrencyChanged),
                    EventCallback.Factory.Create<string>(this, _ => { }));
            }
            builder.CloseComponent();
        }
    }
}
