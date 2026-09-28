using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Odyssey.Client.Pages.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// NetWorthRangeDialog (Odyssey Design System · Dashboard · NetWorthRangeDialog): the draft is seeded
/// on open, a too-short custom span is refused on Apply, and Apply hands back the stored form.
/// </summary>
public class NetWorthRangeDialogTests
{
    private static readonly DateTime Today = new(2026, 9, 28);

    public sealed class DialogHost : ComponentBase
    {
        [Parameter] public NetWorthRange Value { get; set; } = NetWorthRange.Default;
        [Parameter] public List<NetWorthRange> Applied { get; set; } = [];
        [Parameter] public List<bool> OpenChanges { get; set; } = [];

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<NetWorthRangeDialog>(2);
            builder.AddComponentParameter(3, nameof(NetWorthRangeDialog.Open), true);
            builder.AddComponentParameter(4, nameof(NetWorthRangeDialog.Value), Value);
            builder.AddComponentParameter(5, nameof(NetWorthRangeDialog.MaxDate), Today);
            builder.AddComponentParameter(6, nameof(NetWorthRangeDialog.OnApply),
                EventCallback.Factory.Create<NetWorthRange>(this, r => Applied.Add(r)));
            builder.AddComponentParameter(7, nameof(NetWorthRangeDialog.OpenChanged),
                EventCallback.Factory.Create<bool>(this, o => OpenChanges.Add(o)));
            builder.CloseComponent();
        }
    }

    private static (BunitContext Ctx, IRenderedComponent<DialogHost> Cut, List<NetWorthRange> Applied, List<bool> OpenChanges) Render(NetWorthRange value)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var applied = new List<NetWorthRange>();
        var openChanges = new List<bool>();
        var cut = ctx.Render<DialogHost>(p => p
            .Add(h => h.Value, value)
            .Add(h => h.Applied, applied)
            .Add(h => h.OpenChanges, openChanges));
        return (ctx, cut, applied, openChanges);
    }

    private static void ClickButton(IRenderedComponent<DialogHost> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim().EndsWith(text, StringComparison.OrdinalIgnoreCase)).Click();

    [Fact]
    public async Task A_preset_shows_no_date_fields_and_applies_as_itself()
    {
        var (ctx, cut, applied, openChanges) = Render(new NetWorthRange(NetWorthRangePreset.TwelveMonths));
        await using var _ = ctx;

        Assert.Empty(cut.FindAll(".nw-range-dates"));
        ClickButton(cut, "Apply");

        Assert.Equal([new NetWorthRange(NetWorthRangePreset.TwelveMonths)], applied);
        Assert.Equal([false], openChanges);
    }

    [Fact]
    public async Task A_custom_range_opens_with_its_dates()
    {
        var (ctx, cut, _, _) = Render(new NetWorthRange(NetWorthRangePreset.Custom, new DateOnly(2024, 1, 1), null));
        await using var _ctx = ctx;

        var dates = cut.Find(".nw-range-dates");
        Assert.Contains("From", dates.TextContent);
        Assert.Contains("To", dates.TextContent);
        Assert.Contains("Empty runs to today", dates.TextContent);
    }

    [Fact]
    public async Task A_custom_span_shorter_than_a_month_is_refused_on_apply_and_nothing_is_saved()
    {
        var (ctx, cut, applied, openChanges) = Render(
            new NetWorthRange(NetWorthRangePreset.Custom, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 20)));
        await using var _ = ctx;

        Assert.DoesNotContain(NetWorthRange.SpanTooShort, cut.Markup);
        ClickButton(cut, "Apply");

        Assert.Contains(NetWorthRange.SpanTooShort, cut.Markup);
        Assert.Empty(applied);
        Assert.Empty(openChanges);
    }

    [Fact]
    public async Task Cancel_closes_without_applying()
    {
        var (ctx, cut, applied, openChanges) = Render(NetWorthRange.Default);
        await using var _ = ctx;

        ClickButton(cut, "Cancel");

        Assert.Empty(applied);
        Assert.Equal([false], openChanges);
    }
}
