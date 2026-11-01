using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsTooltip's structured bubble (design system · components/Tooltip, the <c>label</c> node):
/// LabelContent replaces the text in the bubble, and the plain Label is what a caller keeps for the
/// trigger's <c>aria-describedby</c>, since the bubble itself is not linked to its trigger.
/// </summary>
public class OdsTooltipTests
{
    private const string Plain = "Plain tooltip text";

    [Fact]
    public async Task LabelContent_is_drawn_in_the_bubble_instead_of_the_plain_label()
    {
        await using var ctx = NewContext();
        var popover = ctx.Render<MudPopoverProvider>();
        ctx.Render<OdsTooltip>(p => p
            .Add(t => t.Label, Plain)
            .AddUnmatched(nameof(MudTooltip.Visible), true)
            .Add(t => t.LabelContent, (RenderFragment)(b =>
            {
                b.OpenElement(0, "span");
                b.AddAttribute(1, "class", "probe-rich");
                b.AddContent(2, "Structured lead");
                b.CloseElement();
            }))
            .Add(t => t.ChildContent, (RenderFragment)(b =>
            {
                b.OpenElement(0, "button");
                b.AddAttribute(1, "aria-describedby", "probe-desc");
                b.AddContent(2, "i");
                b.CloseElement();
            })));

        popover.WaitForAssertion(() => Assert.NotNull(popover.Find(".mud-tooltip .probe-rich")));
        Assert.Equal("Structured lead", popover.Find(".probe-rich").TextContent);
        Assert.DoesNotContain(Plain, popover.Find(".mud-tooltip").TextContent);
    }

    [Fact]
    public async Task Without_LabelContent_the_bubble_carries_the_plain_label()
    {
        await using var ctx = NewContext();
        var popover = ctx.Render<MudPopoverProvider>();
        ctx.Render<OdsTooltip>(p => p
            .Add(t => t.Label, Plain)
            .AddUnmatched(nameof(MudTooltip.Visible), true)
            .Add(t => t.ChildContent, (RenderFragment)(b => b.AddMarkupContent(0, "<button>i</button>"))));

        popover.WaitForAssertion(() => Assert.Contains(Plain, popover.Find(".mud-tooltip").TextContent));
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }
}
