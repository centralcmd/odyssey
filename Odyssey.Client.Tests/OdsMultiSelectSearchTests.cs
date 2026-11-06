using AngleSharp.Dom;
using Bunit;
using MudBlazor;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsMultiSelect's popover (Odyssey Design System · components/MultiSelect): a search field above
/// eight options, a scroll region, an announced loading row distinct from the "No matches" row, and
/// the panel keyboard wired on open.
/// </summary>
public class OdsMultiSelectSearchTests
{
    private static IReadOnlyList<OdsOption> OptionsOf(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new OdsOption($"v{i}", $"Option {i}"))];

    /// <summary>
    /// Renders the control beside a MudPopoverProvider (which the open panel portals into) and opens it.
    /// Returns the provider, where the panel's content is found, and the trigger's id.
    /// </summary>
    private static async Task<(IRenderedComponent<MudPopoverProvider> Pop, string TriggerId)> OpenAsync(
        BunitContext ctx, Action<ComponentParameterCollectionBuilder<OdsMultiSelect>> parameters)
    {
        var pop = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render(parameters);
        var trigger = cut.Find("button.odc-ms-trigger");
        await trigger.ClickAsync(new());
        return (pop, trigger.Id!);
    }

    private static IElement? Search(IRenderedComponent<MudPopoverProvider> pop) =>
        pop.FindAll(".odc-ms-search input").SingleOrDefault();

    [Theory]
    [InlineData(OdsMultiSelect.SearchLimit, null, false)]
    [InlineData(OdsMultiSelect.SearchLimit + 1, null, true)]
    [InlineData(3, true, true)]
    [InlineData(OdsMultiSelect.SearchLimit + 1, false, false)]
    public async Task The_search_field_turns_on_above_eight_options_unless_overridden(int count, bool? searchable, bool expected)
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, triggerId) = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(count))
            .Add(c => c.Searchable, searchable));

        Assert.Equal(expected, Search(pop) is not null);
    }

    [Fact]
    public async Task The_search_field_is_named_after_the_filter_and_shows_the_shared_placeholder()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, triggerId) = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(10)));

        var input = Search(pop)!;
        Assert.Equal("Search tag", input.GetAttribute("aria-label"));
        Assert.Equal("Search…", input.GetAttribute("placeholder"));
        Assert.Equal(triggerId + "-search", input.Id);
    }

    [Fact]
    public async Task Typing_filters_the_rows_on_their_label_and_a_miss_says_no_matches()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, triggerId) = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(12)));

        await Search(pop)!.InputAsync(new() { Value = "option 1" });
        Assert.Equal(["Option 1", "Option 10", "Option 11", "Option 12"],
            pop.FindAll(".odc-ms-list .odc-ms-opt .odc-check-label").Select(e => e.TextContent.Trim()));

        await Search(pop)!.InputAsync(new() { Value = "zzz" });
        Assert.Empty(pop.FindAll(".odc-ms-list .odc-ms-opt"));
        Assert.Equal("No matches", pop.Find(".odc-ms-list .odc-tagms-empty").TextContent);
    }

    [Fact]
    public async Task Loading_is_an_announced_row_and_never_reads_as_no_matches()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, triggerId) = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Account")
            .Add(c => c.Options, OptionsOf(0))
            .Add(c => c.Loading, true));

        var row = pop.Find(".odc-ms-list .odc-ms-loading");
        Assert.Equal("status", row.GetAttribute("role"));
        Assert.Contains("Loading…", row.TextContent);
        Assert.Empty(pop.FindAll(".odc-tagms-empty"));
    }

    [Fact]
    public async Task Rows_sit_in_the_scroll_region_and_the_panel_keyboard_is_wired_on_open()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var (pop, triggerId) = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, OptionsOf(3)));

        Assert.Equal(3, pop.FindAll(".odc-ms-pop > .odc-ms-list > .odc-ms-opt").Count);
        var panelId = triggerId + "-panel";
        Assert.Contains(ctx.JSInterop.Invocations,
            i => i.Identifier == "odsCheckListKeys" && Equals(i.Arguments[0], panelId));
    }
}
