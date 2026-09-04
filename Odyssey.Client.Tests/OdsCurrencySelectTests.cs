using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsCurrencySelect</c>'s rendered contract — the currency-only picker behind the account, budget,
/// tax-statement, exchange-rate and preference surfaces.
/// </summary>
/// <remarks>
/// The case worth pinning is the one that shipped broken: the currency list is FETCHED, so a dialog
/// that opens this picker on its first render can have the menu up before the options land.
/// MudAutocomplete caches the popup's last search result, so the picker sat on "No currency matches"
/// until the user typed — a control that says it knows nothing while holding a full list.
/// </remarks>
public class OdsCurrencySelectTests : IAsyncLifetime
{
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

    private IRenderedComponent<CurrencyHost> Render(bool optionsLoaded = true, bool showName = true)
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();

        return _ctx.Render<CurrencyHost>(p => p
            .Add(h => h.Loaded, optionsLoaded)
            .Add(h => h.ShowName, showName));
    }

    [Fact]
    public void The_trigger_reads_as_the_code_then_the_name()
    {
        var cut = Render();

        Assert.Equal("NOK · Norwegian krone", cut.Find("input.mud-input-slot").GetAttribute("value"));
    }

    [Fact]
    public void ShowName_false_leaves_the_bare_code_for_a_cell_too_narrow_to_carry_a_name()
    {
        var cut = Render(showName: false);

        Assert.Equal("NOK", cut.Find("input.mud-input-slot").GetAttribute("value"));
    }

    [Fact]
    public void An_option_row_carries_the_code_and_the_name()
    {
        // Opening with a currency ALREADY selected is the ordinary case, and the one that came up
        // empty: the search runs with the box's existing caption, which matches no code or name.
        var cut = Render();
        OpenMenu(cut);

        var rows = cut.FindAll(".odc-cursel-item").Select(r => r.TextContent).ToList();

        Assert.Contains(rows, r => r.Contains("NOK", StringComparison.Ordinal)
                                   && r.Contains("Norwegian krone", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_that_arrive_AFTER_the_menu_opened_replace_the_no_matches_state()
    {
        // The regression: MudAutocomplete caches the popup's last search result, so a list that landed
        // after the menu opened never reached the popup and the picker read "No currency matches"
        // until the user typed. The component re-runs the search when the options arrive.
        var cut = Render(optionsLoaded: false);
        OpenMenu(cut);

        Assert.Empty(cut.FindAll(".odc-cursel-item"));

        cut.Render(p => p.Add(h => h.Loaded, true));

        Assert.NotEmpty(cut.FindAll(".odc-cursel-item"));
    }

    // The popup opens through MudAutocomplete's own API: its click-to-open path runs through JS that
    // bUnit stubs out, so a rendered click never reaches it.
    private static void OpenMenu(IRenderedComponent<CurrencyHost> cut)
    {
        var autocomplete = cut.FindComponent<MudAutocomplete<OdsOption>>();
        cut.InvokeAsync(() => autocomplete.Instance.OpenMenuAsync()).GetAwaiter().GetResult();
    }

    /// <summary>The picker beside MudBlazor's providers, which portal its popover.</summary>
    public sealed class CurrencyHost : ComponentBase
    {
        /// <summary>Whether the fetched currency list has landed yet.</summary>
        [Parameter] public bool Loaded { get; set; } = true;

        [Parameter] public bool ShowName { get; set; } = true;

        private string? _value = "NOK";

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsCurrencySelect>(1);
            builder.AddComponentParameter(2, nameof(OdsCurrencySelect.Id), "cursel");
            builder.AddComponentParameter(3, nameof(OdsCurrencySelect.Label), "Currency");
            builder.AddComponentParameter(4, nameof(OdsCurrencySelect.ShowName), ShowName);
            builder.AddComponentParameter(5, nameof(OdsCurrencySelect.Value), _value);
            builder.AddComponentParameter(6, nameof(OdsCurrencySelect.ValueChanged),
                EventCallback.Factory.Create<string>(this, v => _value = v));
            builder.AddComponentParameter(7, nameof(OdsCurrencySelect.Options),
                Loaded ? Currencies : (IReadOnlyList<OdsOption>)[]);
            builder.CloseComponent();
        }
    }
}
