using Bunit;
using MudBlazor;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsMultiSelect.MaxSelected</c> (issue #473 §4.2; the Feed and Tag filters cap at the request's id
/// limit): at the cap the unticked rows are <c>aria-disabled</c> (never <c>disabled</c>) and cannot be
/// ticked, the ticked rows stay operable, and a <c>role="status"</c> line states the page's text.
/// </summary>
public class OdsMultiSelectMaxSelectedTests
{
    private static readonly IReadOnlyList<OdsOption> Options =
        [new("a", "Alpha"), new("b", "Beta"), new("c", "Gamma")];

    private static async Task<(IRenderedComponent<MudPopoverProvider> Pop, IRenderedComponent<OdsMultiSelect> Cut)> OpenAsync(
        BunitContext ctx, IReadOnlyCollection<string> values, int? max, List<IReadOnlyCollection<string>> changes)
    {
        var pop = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Feed")
            .Add(c => c.Options, Options)
            .Add(c => c.Values, values)
            .Add(c => c.MaxSelected, max)
            .Add(c => c.MaxSelectedText, "Up to 2 feeds")
            .Add(c => c.ValuesChanged, (IReadOnlyCollection<string> v) => changes.Add(v)));
        await cut.Find("button.odc-ms-trigger").ClickAsync(new());
        return (pop, cut);
    }

    private static AngleSharp.Dom.IElement Box(IRenderedComponent<MudPopoverProvider> pop, string label) =>
        pop.FindAll(".odc-ms-opt").Single(o => o.TextContent.Contains(label)).QuerySelector("input")!;

    [Fact]
    public async Task At_the_cap_unticked_rows_are_aria_disabled_and_the_status_states_the_cap()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, _) = await OpenAsync(ctx, ["a", "b"], 2, []);

        var gamma = Box(pop, "Gamma");
        Assert.Equal("true", gamma.GetAttribute("aria-disabled"));
        Assert.False(gamma.HasAttribute("disabled"));
        Assert.Contains("is-disabled", gamma.Closest(".odc-ms-opt")!.ClassName);
        Assert.False(Box(pop, "Alpha").HasAttribute("aria-disabled"));

        var status = pop.Find(".odc-ms-max");
        Assert.Equal("status", status.GetAttribute("role"));
        Assert.Equal("Up to 2 feeds", status.TextContent.Trim());
    }

    [Fact]
    public async Task A_capped_row_cannot_be_ticked_but_a_ticked_row_can_be_cleared()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var changes = new List<IReadOnlyCollection<string>>();
        var (pop, _) = await OpenAsync(ctx, ["a", "b"], 2, changes);

        await Box(pop, "Gamma").ChangeAsync(new() { Value = true });
        Assert.Empty(changes);

        await Box(pop, "Alpha").ChangeAsync(new() { Value = false });
        Assert.Equal(["b"], Assert.Single(changes));
    }

    [Fact]
    public async Task Below_the_cap_every_row_is_operable_and_the_status_is_empty()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, _) = await OpenAsync(ctx, ["a"], 2, []);

        Assert.All(pop.FindAll(".odc-ms-opt input"), i => Assert.False(i.HasAttribute("aria-disabled")));
        Assert.Equal(string.Empty, pop.Find(".odc-ms-max").TextContent.Trim());
    }

    [Fact]
    public async Task Without_MaxSelected_nothing_changes()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, _) = await OpenAsync(ctx, ["a", "b", "c"], null, []);

        Assert.Empty(pop.FindAll(".odc-ms-max"));
        Assert.All(pop.FindAll(".odc-ms-opt input"), i => Assert.False(i.HasAttribute("aria-disabled")));
    }
}
