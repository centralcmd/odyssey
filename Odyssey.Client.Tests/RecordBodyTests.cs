using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsRecordBody (Odyssey Design System · components/RecordBody) — the expanded-record body shared by
/// OdsRecordCard and the table detail rows. The body order is structural, so it is asserted from the
/// markup rather than trusted to the caller; and the card must render through the component, or a
/// card and a table row stop being the same surface the moment one of them is restyled.
/// </summary>
public class RecordBodyTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static RenderFragment Marker(string name) => b =>
    {
        b.OpenElement(0, "i");
        b.AddAttribute(1, "data-slot", name);
        b.CloseElement();
    };

    [Fact]
    public void Slots_render_in_the_fixed_order_whatever_order_they_are_passed_in()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRecordBody>(p => p
            .Add(b => b.Sections, Marker("sections"))
            .Add(b => b.Content, Marker("content"))
            .Add(b => b.Details, Marker("details"))
            .Add(b => b.Alert, Marker("alert")));

        var order = cut.FindAll("[data-slot]").Select(e => e.GetAttribute("data-slot"));
        Assert.Equal(["alert", "details", "content", "sections"], order);
    }

    [Fact]
    public void InTable_adds_the_table_modifier_and_a_standalone_accent_sets_the_record_vars()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRecordBody>(p => p
            .Add(b => b.InTable, true)
            .Add(b => b.Accent, "red")
            .Add(b => b.AccentSoft, "pink"));

        var root = cut.Find(".odc-record-body");
        Assert.Contains("in-table", root.ClassList);
        Assert.Equal("--rec:red;--rec-soft:pink;", root.GetAttribute("style"));
    }

    [Fact]
    public void A_card_body_is_not_the_table_variant_and_carries_no_style_of_its_own()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRecordBody>();

        var root = cut.Find(".odc-record-body");
        Assert.DoesNotContain("in-table", root.ClassList);
        Assert.Null(root.GetAttribute("style"));
    }

    [Fact]
    public void An_open_record_card_renders_its_body_through_OdsRecordBody()
    {
        using var ctx = NewContext();

        var cut = ctx.Render<OdsRecordCard>(p => p
            .Add(c => c.Name, "Record")
            .Add(c => c.DefaultOpen, true)
            .Add(c => c.Details, Marker("details")));

        var body = cut.FindComponent<OdsRecordBody>();
        var trigger = cut.Find(".odc-record-trigger");
        Assert.Equal(body.Find(".odc-record-body").Id, trigger.GetAttribute("aria-controls"));
        Assert.Single(body.FindAll("[data-slot=details]"));
    }
}
