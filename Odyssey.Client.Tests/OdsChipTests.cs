using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsChip (Odyssey Design System · components/Chip) — the compact status / category pill.
/// </summary>
/// <remarks>
/// <para>
/// <b>Class and the Fg/Bg pair are load-bearing, not conveniences.</b> Before they existed the
/// component took neither, and a caller passing one got no compile error — Blazor rejects an unknown
/// parameter at RENDER time, so the failure was an <c>InvalidOperationException</c> thrown while
/// drawing the page rather than a build break. The journal entry card's attachment rows pass both, so
/// a regression there takes out every entry carrying a file.
/// </para>
/// <para>
/// The modifier classes are what the global stylesheet keys its variants off (<c>.odc-chip.sm</c>,
/// <c>.odc-chip.archived</c>), and the colour pair is how a chip whose hue comes from a registry — a
/// file kind, a contact type — carries it, since no tone enum can name those.
/// </para>
/// </remarks>
public class OdsChipTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public void Renders_without_a_class_or_colours()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsChip>(p => p.AddChildContent("Plain"));

        var chip = cut.Find("span.odc-chip");
        Assert.Equal("odc-chip", chip.GetAttribute("class"));
        Assert.Contains("Plain", chip.TextContent);
        // No colours supplied means no style attribute at all, not an empty one.
        Assert.False(chip.HasAttribute("style"));
    }

    // The regression guard: passing Class must render, not throw.
    [Fact]
    public void Class_is_appended_after_the_tone_rather_than_replacing_it()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsChip>(p => p
            .Add(c => c.Tone, OdsChipTone.Outline)
            .Add(c => c.Class, "archived sm")
            .AddChildContent("Archived"));

        var css = cut.Find("span.odc-chip").GetAttribute("class");
        Assert.Contains("odc-chip", css);
        Assert.Contains("outline", css);
        Assert.Contains("archived", css);
        Assert.Contains("sm", css);
    }

    [Fact]
    public void A_registry_colour_pair_is_carried_onto_the_chip()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsChip>(p => p
            .Add(c => c.Bg, "var(--finance-expense-soft)")
            .Add(c => c.Fg, "var(--finance-expense)")
            .AddChildContent("PDF"));

        var style = cut.Find("span.odc-chip").GetAttribute("style");
        Assert.Contains("background:var(--finance-expense-soft)", style);
        Assert.Contains("color:var(--finance-expense)", style);
    }

    // Half a pair is a legitimate call — a chip may want its own ink over the tone's ground.
    [Fact]
    public void Half_of_the_colour_pair_writes_only_that_half()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsChip>(p => p
            .Add(c => c.Fg, "var(--tag-text)")
            .AddChildContent("Tag"));

        var style = cut.Find("span.odc-chip").GetAttribute("style");
        Assert.Equal("color:var(--tag-text);", style);
    }

    // The glyph and the dot are reinforcement; the label is where the meaning lives.
    [Fact]
    public void The_icon_and_dot_are_decorative()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsChip>(p => p
            .Add(c => c.Icon, "label")
            .Add(c => c.Dot, true)
            .AddChildContent("Finance"));

        Assert.Equal("true", cut.Find("span.material-icons").GetAttribute("aria-hidden"));
        Assert.Equal("true", cut.Find("span.odc-chip-dot").GetAttribute("aria-hidden"));
        Assert.Contains("Finance", cut.Find("span.odc-chip").TextContent);
    }
}
