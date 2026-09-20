using Bunit;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <c>OdsEmptyLine</c> and <c>OdsEmptyState</c>'s Line variant — the thin muted sentence that stands
/// in for an empty table frame or record section (Odyssey Design System · components/EmptyLine).
/// </summary>
/// <remarks>
/// The branching here is small but all of it is reachable from a call site: Text-vs-ChildContent
/// precedence, the two modifier classes, and — the one with a correctness cost — whether
/// <c>OdsEmptyState.Alert</c> survives the delegation to the line shape. It does not survive by
/// accident: the Line branch renders a different component, so a failure state switched to the line
/// variant would ship silently unannounced if the role were not forwarded (WCAG 4.1.3).
/// </remarks>
public class OdsEmptyLineTests
{
    private static IRenderedComponent<OdsEmptyLine> Render(
        Action<ComponentParameterCollectionBuilder<OdsEmptyLine>> configure)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.Render(configure);
    }

    [Fact]
    public void The_default_line_is_leading_medium_padded_and_carries_no_role()
    {
        var cut = Render(p => p.Add(l => l.Text, "No files attached to this account yet."));

        var root = cut.Find("div");
        Assert.Equal("odc-empty line", root.ClassName);
        Assert.Equal("No files attached to this account yet.", root.TextContent.Trim());

        // An ordinary absence is not a status message and must not interrupt a screen-reader user.
        Assert.False(root.HasAttribute("role"));
    }

    [Theory]
    [InlineData(OdsEmptyLineAlign.Center, OdsSize.Lg, "odc-empty line center pad-lg")]
    [InlineData(OdsEmptyLineAlign.Start, OdsSize.Sm, "odc-empty line pad-sm")]
    [InlineData(OdsEmptyLineAlign.Center, OdsSize.Md, "odc-empty line center")]
    public void Align_and_pad_map_to_the_modifier_classes(
        OdsEmptyLineAlign align, OdsSize pad, string expected)
    {
        var cut = Render(p => p
            .Add(l => l.Text, "No accounts match your filters.")
            .Add(l => l.Align, align)
            .Add(l => l.Pad, pad));

        Assert.Equal(expected, cut.Find("div").ClassName);
    }

    /// <summary>
    /// <c>Text</c> wins over <c>ChildContent</c>, so a call site that supplies both gets the simple
    /// string rather than both rendered one after the other.
    /// </summary>
    [Fact]
    public void Text_takes_precedence_over_child_content()
    {
        var cut = Render(p => p
            .Add(l => l.Text, "From Text")
            .Add(l => l.ChildContent, "From ChildContent"));

        Assert.Equal("From Text", cut.Find("div").TextContent.Trim());
        Assert.DoesNotContain("From ChildContent", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_content_renders_when_no_text_is_given()
    {
        var cut = Render(p => p.Add(l => l.ChildContent, "No events yet."));

        Assert.Equal("No events yet.", cut.Find("div").TextContent.Trim());
    }

    /// <summary>
    /// The line is a programmatic focus target on at least one surface (the contract parties section
    /// links to it), so the id and the negative tabindex both have to reach the DOM.
    /// </summary>
    [Fact]
    public void Id_and_tab_index_reach_the_element()
    {
        var cut = Render(p => p
            .Add(l => l.Text, "No parties yet.")
            .Add(l => l.Id, "con-parties-empty")
            .Add(l => l.TabIndex, -1));

        var root = cut.Find("div");
        Assert.Equal("con-parties-empty", root.Id);
        Assert.Equal("-1", root.GetAttribute("tabindex"));
    }

    [Fact]
    public void An_explicit_role_reaches_the_element()
    {
        var cut = Render(p => p
            .Add(l => l.Text, "Couldn't load these rows.")
            .Add(l => l.Role, "alert"));

        Assert.Equal("alert", cut.Find("div").GetAttribute("role"));
    }

    // ── OdsEmptyState's Line variant delegates here ──────────────────────────

    private static IRenderedComponent<OdsEmptyState> RenderState(
        Action<ComponentParameterCollectionBuilder<OdsEmptyState>> configure)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.Render(configure);
    }

    /// <summary>
    /// The Line variant renders the line shape and none of the panel's furniture — no icon tile, no
    /// action slot — whatever those parameters were set to.
    /// </summary>
    [Fact]
    public void The_line_variant_renders_the_line_and_drops_the_panel_furniture()
    {
        var cut = RenderState(p => p
            .Add(s => s.Variant, OdsEmptyStateVariant.Line)
            .Add(s => s.Icon, "inbox")
            .Add(s => s.Title, "No declared figures yet.")
            .Add(s => s.Action, "<button>Add</button>"));

        Assert.Equal("odc-empty line", cut.Find("div").ClassName);
        Assert.Empty(cut.FindAll(".odc-empty-ic"));
        Assert.Empty(cut.FindAll(".odc-empty-actions"));
        Assert.Empty(cut.FindAll(".odc-empty-ttl"));
        Assert.Equal("No declared figures yet.", cut.Find("div").TextContent.Trim());
    }

    /// <summary>The line falls back to the Description when there is no Title, matching the DS.</summary>
    [Fact]
    public void The_line_variant_falls_back_to_the_description()
    {
        var cut = RenderState(p => p
            .Add(s => s.Variant, OdsEmptyStateVariant.Line)
            .Add(s => s.Description, "Nothing in force today."));

        Assert.Equal("Nothing in force today.", cut.Find("div").TextContent.Trim());
    }

    /// <summary>
    /// <b>Alert survives the variant switch.</b> The Line branch renders a different component, so
    /// the role has to be forwarded explicitly — dropping it would ship an unannounced failure state
    /// the moment a surface chose the line shape (WCAG 4.1.3).
    /// </summary>
    [Fact]
    public void Alert_is_honoured_by_both_variants()
    {
        var line = RenderState(p => p
            .Add(s => s.Variant, OdsEmptyStateVariant.Line)
            .Add(s => s.Alert, true)
            .Add(s => s.Title, "Couldn't load these rows."));
        Assert.Equal("alert", line.Find("div").GetAttribute("role"));

        var panel = RenderState(p => p
            .Add(s => s.Alert, true)
            .Add(s => s.Title, "Couldn't load these rows."));
        Assert.Equal("alert", panel.Find(".odc-empty").GetAttribute("role"));
    }

    /// <summary>The panel is the default, so an omitted Variant must not quietly become a line.</summary>
    [Fact]
    public void The_panel_is_the_default_variant()
    {
        var cut = RenderState(p => p.Add(s => s.Title, "No rates or fees recorded yet"));

        Assert.NotEmpty(cut.FindAll(".odc-empty-ic"));
        Assert.DoesNotContain("odc-empty line", cut.Markup, StringComparison.Ordinal);
    }
}
