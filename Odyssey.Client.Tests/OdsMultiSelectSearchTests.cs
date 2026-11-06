using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsMultiSelect's popover (Odyssey Design System · components/MultiSelect): a search field above
/// eight options, a scroll region, a loading row distinct from the "No matches" row, a polite status
/// line that announces both, and the panel keyboard wired on open.
/// </summary>
/// <remarks>
/// The keyboard itself (Tab cycling, ↑/↓, Enter, focus back after Clear) is <c>odsCheckListKeys</c> in
/// <c>wwwroot/index.html</c>; bUnit runs no JavaScript, so these tests pin the wiring and the markup
/// contract the script reads (<c>.odc-ms-search input</c>, <c>.odc-ms-opt</c> checkboxes,
/// <c>.odc-ms-foot</c> buttons, the "{trigger}-panel" id).
/// </remarks>
public class OdsMultiSelectSearchTests
{
    private static IReadOnlyList<OdsOption> OptionsOf(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new OdsOption($"v{i}", $"Option {i}"))];

    private sealed record Opened(
        IRenderedComponent<MudPopoverProvider> Pop, IRenderedComponent<OdsMultiSelect> Cut, string TriggerId);

    /// <summary>
    /// Renders the control beside a MudPopoverProvider (which the open panel portals into) and opens it.
    /// The panel's content is found on <see cref="Opened.Pop"/>.
    /// </summary>
    private static async Task<Opened> OpenAsync(
        BunitContext ctx, Action<ComponentParameterCollectionBuilder<OdsMultiSelect>> parameters)
    {
        var pop = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render(parameters);
        var trigger = cut.Find("button.odc-ms-trigger");
        await trigger.ClickAsync(new());
        return new(pop, cut, trigger.Id!);
    }

    private static IElement? Search(IRenderedComponent<MudPopoverProvider> pop) =>
        pop.FindAll(".odc-ms-search input").SingleOrDefault();

    private static IReadOnlyList<string> RowLabels(IRenderedComponent<MudPopoverProvider> pop) =>
        [.. pop.FindAll(".odc-ms-list .odc-ms-opt .odc-check-label").Select(e => e.TextContent.Trim())];

    private static string Status(IRenderedComponent<MudPopoverProvider> pop) =>
        pop.Find(".odc-ms-status").TextContent.Trim();

    [Theory]
    [InlineData(OdsMultiSelect.SearchLimit, null, false)]
    [InlineData(OdsMultiSelect.SearchLimit + 1, null, true)]
    [InlineData(3, true, true)]
    [InlineData(OdsMultiSelect.SearchLimit + 1, false, false)]
    public async Task The_search_field_turns_on_above_eight_options_unless_overridden(int count, bool? searchable, bool expected)
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(count))
            .Add(c => c.Searchable, searchable));

        Assert.Equal(expected, Search(opened.Pop) is not null);
    }

    [Fact]
    public async Task The_search_field_is_named_after_the_filter_and_shows_the_shared_placeholder()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(10)));

        var input = Search(opened.Pop)!;
        Assert.Equal("Search tag", input.GetAttribute("aria-label"));
        Assert.Equal("Search…", input.GetAttribute("placeholder"));
        Assert.Equal(opened.TriggerId + "-search", input.Id);
    }

    [Fact]
    public async Task The_search_label_placeholder_and_empty_text_can_be_overridden()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Account")
            .Add(c => c.Options, OptionsOf(10))
            .Add(c => c.SearchLabel, "Find an account")
            .Add(c => c.SearchPlaceholder, "Name or number…")
            .Add(c => c.EmptyText, "No account by that name"));

        var input = Search(opened.Pop)!;
        Assert.Equal("Find an account", input.GetAttribute("aria-label"));
        Assert.Equal("Name or number…", input.GetAttribute("placeholder"));

        await input.InputAsync(new() { Value = "zzz" });
        Assert.Equal("No account by that name", opened.Pop.Find(".odc-ms-list .odc-tagms-empty").TextContent);
        Assert.Equal("No account by that name", Status(opened.Pop));
    }

    [Fact]
    public async Task Typing_filters_the_rows_on_their_label_and_a_miss_says_no_matches()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(12)));

        await Search(opened.Pop)!.InputAsync(new() { Value = "option 1" });
        Assert.Equal(["Option 1", "Option 10", "Option 11", "Option 12"], RowLabels(opened.Pop));

        await Search(opened.Pop)!.InputAsync(new() { Value = "zzz" });
        Assert.Empty(opened.Pop.FindAll(".odc-ms-list .odc-ms-opt"));
        Assert.Equal("No matches", opened.Pop.Find(".odc-ms-list .odc-tagms-empty").TextContent);
    }

    [Fact]
    public async Task An_option_with_no_label_never_matches_a_search_and_never_throws()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        IReadOnlyList<OdsOption> options = [.. OptionsOf(9), new OdsOption("blank", null!)];
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, options));

        await Search(opened.Pop)!.InputAsync(new() { Value = "option 9" });
        Assert.Equal(["Option 9"], RowLabels(opened.Pop));
    }

    [Fact]
    public async Task The_status_line_announces_the_count_a_miss_and_nothing_before_a_search()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(12)));

        var status = opened.Pop.Find(".odc-ms-status");
        Assert.Equal("status", status.GetAttribute("role"));
        Assert.Equal("polite", status.GetAttribute("aria-live"));
        Assert.Equal(string.Empty, Status(opened.Pop));

        await Search(opened.Pop)!.InputAsync(new() { Value = "option 1" });
        Assert.Equal("4 results", Status(opened.Pop));

        await Search(opened.Pop)!.InputAsync(new() { Value = "option 12" });
        Assert.Equal("1 result", Status(opened.Pop));

        await Search(opened.Pop)!.InputAsync(new() { Value = "zzz" });
        Assert.Equal("No matches", Status(opened.Pop));
    }

    [Fact]
    public async Task Loading_reads_as_loading_and_never_as_no_matches()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Account")
            .Add(c => c.Options, OptionsOf(0))
            .Add(c => c.Loading, true));

        var row = opened.Pop.Find(".odc-ms-list .odc-ms-loading");
        Assert.Null(row.GetAttribute("role"));
        Assert.Contains("Loading…", row.TextContent);
        Assert.Equal("Loading…", Status(opened.Pop));
        Assert.Empty(opened.Pop.FindAll(".odc-tagms-empty"));
    }

    [Fact]
    public async Task Loading_outranks_a_search_that_matches_nothing()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Account")
            .Add(c => c.Options, OptionsOf(10)));

        await Search(opened.Pop)!.InputAsync(new() { Value = "zzz" });
        opened.Cut.Render(p => p.Add(c => c.Loading, true));

        Assert.NotNull(opened.Pop.Find(".odc-ms-list .odc-ms-loading"));
        Assert.Empty(opened.Pop.FindAll(".odc-tagms-empty"));
        Assert.Empty(opened.Pop.FindAll(".odc-ms-list .odc-ms-opt"));
        Assert.Equal("Loading…", Status(opened.Pop));
    }

    [Fact]
    public async Task Closing_the_popover_clears_the_search_so_it_reopens_on_the_whole_list()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(12)));

        await Search(opened.Pop)!.InputAsync(new() { Value = "option 12" });
        Assert.Single(RowLabels(opened.Pop));

        await opened.Pop.FindAll(".odc-ms-foot .odc-btn").Single(b => b.TextContent == "Done").ClickAsync(new());
        Assert.Empty(opened.Pop.FindAll(".odc-ms-pop"));

        await opened.Cut.Find("button.odc-ms-trigger").ClickAsync(new());
        Assert.Equal(string.Empty, Search(opened.Pop)!.GetAttribute("value") ?? string.Empty);
        Assert.Equal(12, RowLabels(opened.Pop).Count);
    }

    [Fact]
    public async Task Toggling_a_row_while_filtered_keeps_the_selection_the_search_hides_and_Clear_empties_it()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        IReadOnlyCollection<string> values = ["v2"];
        var pop = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Tag")
            .Add(c => c.Options, OptionsOf(12))
            .Add(c => c.Values, values)
            .Add(c => c.ValuesChanged, EventCallback.Factory.Create<IReadOnlyCollection<string>>(this, v => values = v)));
        await cut.Find("button.odc-ms-trigger").ClickAsync(new());

        await Search(pop)!.InputAsync(new() { Value = "option 12" });
        await pop.Find(".odc-ms-list .odc-ms-opt input[type=checkbox]").ChangeAsync(new() { Value = true });
        Assert.Equal(["v12", "v2"], values.Order());

        cut.Render(p => p.Add(c => c.Values, values));
        var clear = pop.FindAll(".odc-ms-foot .odc-btn").Single(b => b.TextContent == "Clear");
        Assert.False(clear.HasAttribute("disabled"));
        await clear.ClickAsync(new());
        Assert.Empty(values);

        cut.Render(p => p.Add(c => c.Values, values));
        Assert.True(pop.FindAll(".odc-ms-foot .odc-btn").Single(b => b.TextContent == "Clear").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Rows_sit_in_the_scroll_region_and_the_panel_keyboard_is_wired_on_open()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var opened = await OpenAsync(ctx, p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, OptionsOf(3)));

        Assert.Equal(3, opened.Pop.FindAll(".odc-ms-pop > .odc-ms-list > .odc-ms-opt").Count);
        var panelId = opened.TriggerId + "-panel";
        Assert.NotNull(opened.Pop.Find($"#{panelId} .odc-ms-pop"));
        Assert.Contains(ctx.JSInterop.Invocations,
            i => i.Identifier == "odsCheckListKeys" && Equals(i.Arguments[0], panelId));
    }

    [Fact]
    public void The_keyboard_script_waits_for_the_portaled_panel_and_restores_focus_after_Clear()
    {
        // bUnit runs no JavaScript, so pin the two behaviours the review asked for in the script's source:
        // a bounded wait for the portaled panel (the odsFocusInPopover pattern), and focus put back on the
        // search field or first row when Clear disables itself.
        var index = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "index.html"));
        var start = index.IndexOf("window.odsCheckListKeys", StringComparison.Ordinal);
        var end = index.IndexOf("window.odsFocusIfLost", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var script = index[start..end];

        Assert.Contains("requestAnimationFrame(attach)", script, StringComparison.Ordinal);
        Assert.Contains("frames++ < 30", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('click'", script, StringComparison.Ordinal);
        Assert.Contains(".odc-ms-search input", script, StringComparison.Ordinal);
    }
}
