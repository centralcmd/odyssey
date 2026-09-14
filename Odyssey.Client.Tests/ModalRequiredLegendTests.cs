using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsModal's "* Required" footer legend (Odyssey Design System · Modal <c>requiredLegend</c>). The DS
/// grows it by querying the rendered body for a required marker; here the legend is always rendered
/// and a CSS <c>:has()</c> rule shows it only while the body holds one — so a field that appears later
/// is picked up without the dialog re-rendering. The markup half is asserted by render, the visibility
/// half from the stylesheet, since bUnit applies no CSS.
/// </summary>
public class ModalRequiredLegendTests
{
    [Fact]
    public async Task The_footer_carries_the_legend_by_default()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<ModalHost>(p => p.Add(h => h.RequiredLegend, true));

        var legend = cut.Find(".ods-modal-foot .ods-modal-legend");
        Assert.Equal("* Required", legend.TextContent.Trim());
        Assert.Equal("true", legend.QuerySelector(".odc-field-req")!.GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task RequiredLegend_false_opts_the_dialog_out()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<ModalHost>(p => p.Add(h => h.RequiredLegend, false));

        Assert.NotEmpty(cut.FindAll(".ods-modal-foot"));
        Assert.Empty(cut.FindAll(".ods-modal-legend"));
    }

    [Fact]
    public void The_legend_is_hidden_unless_the_body_holds_either_kind_of_required_marker()
    {
        var css = File.ReadAllText(Path.Combine(ClientSource.Root, "wwwroot", "css", "odyssey-components.css"));

        Assert.Matches(@"\.ods-modal-foot > \.ods-modal-legend \{\s*display: none;", css);
        // Both markers: the DS .odc-field-req (hand-labelled fields, OdsFieldShell) and MudBlazor's
        // .mud-input-required (OdsField / OdsSelect / OdsDatePicker) — dropping either would hide the
        // legend on a dialog whose only required fields are of that kind.
        Assert.Contains(
            ".mud-dialog:has(.mud-dialog-content :is(.odc-field-req, .mud-input-required)) .mud-dialog-actions.ods-modal-foot > .ods-modal-legend { display: inline-flex; }",
            css);
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    /// <summary>An open modal beside the provider its MudDialog teleports into.</summary>
    public sealed class ModalHost : ComponentBase
    {
        [Parameter] public bool RequiredLegend { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<OdsModal>(2);
            builder.AddComponentParameter(3, nameof(OdsModal.Open), true);
            builder.AddComponentParameter(4, nameof(OdsModal.RequiredLegend), RequiredLegend);
            builder.AddComponentParameter(5, nameof(OdsModal.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent<OdsFieldShell>(0);
                b.AddComponentParameter(1, nameof(OdsFieldShell.Label), "Name");
                b.AddComponentParameter(2, nameof(OdsFieldShell.Required), true);
                b.CloseComponent();
            }));
            builder.AddComponentParameter(6, nameof(OdsModal.Footer), (RenderFragment)(b => b.AddContent(0, "Save")));
            builder.CloseComponent();
        }
    }
}
