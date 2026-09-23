using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsCombobox's FreeText mode (Odyssey Design System · Combobox <c>freeText</c>): the field SUGGESTS
/// rather than constrains, so a typed name commits on blur and a value matching no option still
/// displays.
/// </summary>
public class OdsComboboxFreeTextTests
{
    private static readonly IReadOnlyList<OdsOption> Options = [OdsOption.From("Monthly rent"), OdsOption.From("Parking space")];

    private sealed class Host : ComponentBase
    {
        [Parameter] public bool FreeText { get; set; }
        [Parameter] public string? Initial { get; set; }
        public string? Value { get; private set; }

        protected override void OnInitialized() => Value = Initial;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<OdsCombobox>(1);
            builder.AddComponentParameter(2, nameof(OdsCombobox.InputId), "name");
            builder.AddComponentParameter(3, nameof(OdsCombobox.Options), Options);
            builder.AddComponentParameter(4, nameof(OdsCombobox.FreeText), FreeText);
            builder.AddComponentParameter(5, nameof(OdsCombobox.Value), Value);
            builder.AddComponentParameter(6, nameof(OdsCombobox.ValueChanged),
                EventCallback.Factory.Create<string?>(this, v => Value = v));
            builder.CloseComponent();
        }
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    [Fact]
    public async Task A_typed_name_commits_on_blur()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>(p => p.Add(h => h.FreeText, true));

        cut.Find("#name").Input("Water");
        cut.Find("#name").Blur();

        Assert.Equal("Water", cut.Instance.Value);
    }

    [Fact]
    public async Task A_typed_name_matching_an_option_commits_that_option()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>(p => p.Add(h => h.FreeText, true));

        cut.Find("#name").Input("  monthly RENT ");
        cut.Find("#name").Blur();

        Assert.Equal("Monthly rent", cut.Instance.Value);
    }

    [Fact]
    public async Task A_value_matching_no_option_still_displays()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>(p => p.Add(h => h.FreeText, true).Add(h => h.Initial, "Water"));

        Assert.Equal("Water", cut.Find("#name").GetAttribute("value"));
    }

    [Fact]
    public async Task A_constrained_combobox_does_not_commit_a_typed_name()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<Host>(p => p.Add(h => h.FreeText, false));

        cut.Find("#name").Input("Water");
        cut.Find("#name").Blur();

        Assert.Null(cut.Instance.Value);
    }
}
