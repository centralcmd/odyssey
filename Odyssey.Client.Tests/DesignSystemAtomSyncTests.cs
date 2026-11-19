using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The shared-atom changes of the November 2026 design-system sync: PageHeader's default "Attention" label,
/// compact problem rows and its secondary action slots; MultiSelect's <c>fit</c> root class and default cap
/// text; SortSelect's one trigger for a one-field page.
/// </summary>
public class DesignSystemAtomSyncTests
{
    private static readonly IReadOnlyList<PageHeaderProblem> OneProblem =
        [new PageHeaderProblem { Lead = "Feed", Message = "Failing." }];

    [Fact]
    public void The_problem_toggle_defaults_to_Attention()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<PageHeader>(p => p.Add(h => h.Title, "News").Add(h => h.Problems, OneProblem));

        var toggle = cut.Find("button.ph-signal-btn");
        Assert.Equal("Attention, 1", System.Text.RegularExpressions.Regex.Replace(toggle.TextContent, @"\s+", " ").Trim());
    }

    [Fact]
    public void Problem_rows_are_compact_alerts()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<PageHeader>(p => p.Add(h => h.Title, "News").Add(h => h.Problems, OneProblem));

        Assert.Contains("compact", cut.Find(".ph-problems .alert, .alert").ClassList);
    }

    [Fact]
    public void Lead_and_secondary_actions_render_before_the_toggles_and_the_primary()
    {
        using var ctx = NestedInteractiveControlTests.NewContext();
        var cut = ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "News")
            .Add(h => h.Problems, OneProblem)
            .Add(h => h.LeadActions, (RenderFragment)(b => b.AddMarkupContent(0, "<button id=\"lead\">Lead</button>")))
            .Add(h => h.Actions, (RenderFragment)(b => b.AddMarkupContent(0, "<button id=\"extra\">Extra</button>")))
            .Add(h => h.PrimaryActionText, "New"));

        var ids = cut.FindAll(".ph-actions button").Select(b => string.IsNullOrEmpty(b.Id) ? System.Text.RegularExpressions.Regex.Replace(b.TextContent, @"\s+", " ").Trim() : b.Id).ToList();
        var lead = ids.IndexOf("lead");
        var extra = ids.IndexOf("extra");
        var signal = ids.FindIndex(t => t.StartsWith("Attention", StringComparison.Ordinal));
        var primary = ids.IndexOf("New");
        Assert.True(lead >= 0 && lead < signal, string.Join(" | ", ids));
        Assert.True(extra > signal && extra < primary, string.Join(" | ", ids));
    }

    [Fact]
    public async Task A_fitted_multiselect_carries_the_fit_class_on_its_root()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Status")
            .Add(c => c.Options, (IReadOnlyList<OdsOption>)[new("u", "Unread"), new("r", "Read")])
            .Add(c => c.Values, (IReadOnlyCollection<string>)[]));

        Assert.Contains("fit", cut.Find(".odc-ms").ClassList);
    }

    [Fact]
    public async Task Without_page_text_the_cap_line_says_up_to_the_cap()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        var pop = ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsMultiSelect>(p => p
            .Add(c => c.Label, "Feed")
            .Add(c => c.Options, (IReadOnlyList<OdsOption>)[new("a", "Alpha"), new("b", "Beta"), new("c", "Gamma")])
            .Add(c => c.Values, (IReadOnlyCollection<string>)["a", "b"])
            .Add(c => c.MaxSelected, 2));
        await cut.Find("button.odc-ms-trigger").ClickAsync(new());

        Assert.Equal("Up to 2", pop.Find(".odc-ms-max").TextContent.Trim());
    }

    [Fact]
    public async Task A_one_field_sort_select_still_renders_the_field_trigger()
    {
        await using var ctx = NestedInteractiveControlTests.NewContext();
        ctx.Render<MudPopoverProvider>();
        var cut = ctx.Render<OdsSortSelect<object>>(p => p.Add(c => c.Fields, (IReadOnlyList<OdsSortField<object>>)
            [new OdsSortField<object> { Key = "name", Label = "Name", Type = OdsSortType.Text }]));

        Assert.Single(cut.FindAll("button.odc-sortsel-trigger"));
        Assert.Contains("split", cut.Find(".odc-sortsel").ClassList);
        Assert.Empty(cut.FindAll(".odc-sortsel-static"));
    }
}
