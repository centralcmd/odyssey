using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// A required field's obligation has to reach assistive technology, not just the eye (WCAG 3.3.2 /
/// 4.1.2). The label's <c>*</c> is <c>aria-hidden</c>, so each hand-rolled control carries it itself:
/// a text entry control as <c>aria-required</c>, a button trigger — which may not carry
/// <c>aria-required</c> — as a visually-hidden "Required" in its description. (MudBlazor-backed fields
/// get <c>aria-required</c> from MudBlazor, and <see cref="CardSelectTests"/> covers the radiogroup.)
/// </summary>
public class RequiredFieldAccessibilityTests
{
    public static TheoryData<string> TextEntryFields =>
        [nameof(OdsTextInputField), nameof(OdsNoteField), nameof(OdsNumberField), nameof(OdsAmountField), nameof(OdsStepperField), nameof(OdsMoneyField)];

    [Theory]
    [MemberData(nameof(TextEntryFields))]
    public async Task A_required_text_entry_field_marks_its_control_aria_required(string component)
    {
        await using var ctx = NewContext();

        var required = Render(ctx, component, required: true);
        Assert.Equal("true", required.Find("#probe").GetAttribute("aria-required"));

        var optional = Render(ctx, component, required: false);
        Assert.False(optional.Find("#probe").HasAttribute("aria-required"));
    }

    [Fact]
    public async Task A_required_type_select_describes_its_trigger_as_required()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<OdsTypeSelect>(p => p
            .Add(c => c.Label, "Account type")
            .Add(c => c.Id, "probe")
            .Add(c => c.Required, true)
            .Add(c => c.Help, "Pick one")
            .Add(c => c.Types, [new OdsTypeOption { Key = "a", Label = "A", Icon = "wallet", Color = "red", Soft = "pink" }]));

        AssertDescribedAsRequired(cut, cut.Find("button#probe"));
        Assert.Contains("probe-help", cut.Find("button#probe").GetAttribute("aria-describedby")!.Split(' '));
    }

    [Fact]
    public async Task A_required_tag_multi_select_describes_its_trigger_as_required()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<OdsTagMultiSelect>(p => p
            .Add(c => c.Label, "Tags")
            .Add(c => c.Required, true)
            .Add(c => c.Options, [new OdsOption("t1", "Groceries")]));

        AssertDescribedAsRequired(cut, cut.Find("button.odc-tagms-trigger"));
    }

    [Fact]
    public async Task An_optional_button_trigger_carries_no_required_description()
    {
        await using var ctx = NewContext();

        var cut = ctx.Render<OdsTagMultiSelect>(p => p
            .Add(c => c.Label, "Tags")
            .Add(c => c.Options, [new OdsOption("t1", "Groceries")]));

        Assert.DoesNotContain(cut.FindAll(".sr-only"), e => e.TextContent == "Required");
        Assert.False(cut.Find("button.odc-tagms-trigger").HasAttribute("aria-describedby"));
    }

    private static void AssertDescribedAsRequired<T>(IRenderedComponent<T> cut, AngleSharp.Dom.IElement trigger)
        where T : IComponent
    {
        var ids = trigger.GetAttribute("aria-describedby")?.Split(' ') ?? [];
        Assert.Contains(ids, id => cut.FindAll($"#{id}").SingleOrDefault() is { } node
                                   && node.TextContent == "Required" && node.ClassList.Contains("sr-only"));
    }

    private static IRenderedComponent<IComponent> Render(BunitContext ctx, string component, bool required)
    {
        var type = typeof(OdsFieldShell).Assembly.GetType($"{typeof(OdsFieldShell).Namespace}.{component}")!;
        return ctx.Render(builder =>
        {
            builder.OpenComponent(0, type);
            builder.AddComponentParameter(1, "Label", "Probe");
            builder.AddComponentParameter(2, "Id", "probe");
            builder.AddComponentParameter(3, "Required", required);
            builder.CloseComponent();
        });
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }
}
