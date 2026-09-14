using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsCardSelect (Odyssey Design System · components/CardSelect) — the icon-over-label card picker for
/// the "what kind is this?" question at the top of a create dialog. It is hand-rolled rather than a
/// MudToggleGroup precisely for its radiogroup semantics, so those are what these pin.
/// </summary>
public class CardSelectTests
{
    private static readonly IReadOnlyList<OdsCardSelectOption> Kinds =
    [
        new() { Value = "a", Label = "Account", Icon = "account_balance_wallet" },
        new() { Value = "b", Label = "Contact", Icon = "groups", Sub = "A person or organization" },
        new() { Value = "c", Label = "Locked", Icon = "lock", Disabled = true },
    ];

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public void Renders_a_named_radiogroup_whose_selected_card_is_the_only_tab_stop()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsCardSelect>(p => p
            .Add(c => c.Options, Kinds)
            .Add(c => c.Value, "b")
            .Add(c => c.AriaLabel, "Party kind"));

        var group = cut.Find(".odc-cardsel");
        Assert.Equal("radiogroup", group.GetAttribute("role"));
        Assert.Equal("Party kind", group.GetAttribute("aria-label"));

        var cards = cut.FindAll(".odc-cardsel-opt");
        Assert.All(cards, card => Assert.Equal("radio", card.GetAttribute("role")));
        Assert.Equal(["false", "true", "false"], cards.Select(c => c.GetAttribute("aria-checked")));
        Assert.Equal(["-1", "0", "-1"], cards.Select(c => c.GetAttribute("tabindex")));
        Assert.Equal("A person or organization", cards[1].QuerySelector(".odc-cardsel-sub")!.TextContent);
    }

    [Fact]
    public void With_nothing_selected_the_first_enabled_card_takes_the_tab_stop()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsCardSelect>(p => p.Add(c => c.Options, Kinds));

        Assert.Equal(["0", "-1", "-1"], cut.FindAll(".odc-cardsel-opt").Select(c => c.GetAttribute("tabindex")));
    }

    [Fact]
    public void Clicking_and_arrow_keys_select_but_never_land_on_a_disabled_card()
    {
        using var ctx = NewContext();
        string? picked = null;

        var cut = ctx.Render<OdsCardSelect>(p => p
            .Add(c => c.Options, Kinds)
            .Add(c => c.Value, "a")
            .Add(c => c.ValueChanged, v => picked = v));

        cut.FindAll(".odc-cardsel-opt")[1].Click();
        Assert.Equal("b", picked);

        cut.Render(p => p.Add(c => c.Value, "b"));
        cut.FindAll(".odc-cardsel-opt")[1].KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal("b", picked); // the next card is disabled — nothing moves

        cut.FindAll(".odc-cardsel-opt")[1].KeyDown(new KeyboardEventArgs { Key = "Home" });
        Assert.Equal("a", picked);
    }

    [Fact]
    public void The_group_accent_is_overridden_per_option_and_the_layout_follows_columns_and_width()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsCardSelect>(p => p
            .Add(c => c.Options, [
                new OdsCardSelectOption { Value = "rate", Label = "Rate" },
                new OdsCardSelectOption { Value = "fee", Label = "Fee", Color = "red", Soft = "pink" },
            ])
            .Add(c => c.Accent, "var(--con-accent)")
            .Add(c => c.AccentSoft, "var(--con-accent-soft)")
            .Add(c => c.Columns, 2)
            .Add(c => c.MaxItemWidth, 180)
            .Add(c => c.Center, true));

        var group = cut.Find(".odc-cardsel");
        Assert.Contains("center", group.ClassList);
        Assert.Equal(
            "grid-template-columns:repeat(2, minmax(0, 180px));--odc-cardsel-accent:var(--con-accent);" +
            "--odc-cardsel-line:var(--con-accent);--odc-cardsel-soft:var(--con-accent-soft);",
            group.GetAttribute("style"));

        var cards = cut.FindAll(".odc-cardsel-opt");
        Assert.Null(cards[0].GetAttribute("style"));
        Assert.Equal("--odc-cardsel-accent:red;--odc-cardsel-line:red;--odc-cardsel-soft:pink;", cards[1].GetAttribute("style"));
    }
}
