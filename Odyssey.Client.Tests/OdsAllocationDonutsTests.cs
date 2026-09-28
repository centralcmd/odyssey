using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// OdsAllocationDonuts (design system · components/AllocationDonuts): the slice ordering both wells
/// share, the money tint each well carries, and the empty line that keeps a well's heading.
/// </summary>
public class OdsAllocationDonutsTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static OdsDonutSlice Slice(string label, decimal value) => new() { Label = label, Value = value };

    [Fact]
    public void Slices_sort_largest_magnitude_first_and_zero_values_drop()
    {
        var sorted = OdsAllocationDonuts.Sort([Slice("a", 10), Slice("b", 0), Slice("c", -40), Slice("d", 25)]);

        Assert.Equal(["c", "d", "a"], sorted.Select(s => s.Label));
        Assert.Equal([40m, 25m, 10m], sorted.Select(s => s.Value));
    }

    [Fact]
    public void Each_well_carries_its_money_tone()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsAllocationDonuts>(p => p
            .Add(c => c.AssetSlices, [Slice("Savings", 100)])
            .Add(c => c.LiabilitySlices, [Slice("Card", -50)]));

        var wells = cut.FindAll(".odc-alloc-card");
        Assert.Equal(2, wells.Count);
        Assert.Contains("income", wells[0].ClassList);
        Assert.Contains("expense", wells[1].ClassList);
    }

    [Fact]
    public void A_side_can_be_hidden()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsAllocationDonuts>(p => p
            .Add(c => c.AssetSlices, [Slice("Savings", 100)])
            .Add(c => c.ShowLiabilities, false));

        Assert.Single(cut.FindAll(".odc-alloc-card"));
    }

    [Fact]
    public void An_empty_side_with_empty_text_keeps_its_title_and_draws_no_ring()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsAllocationDonuts>(p => p
            .Add(c => c.AssetSlices, [Slice("Savings", 100)])
            .Add(c => c.LiabilitiesEmpty, "No negative balances yet"));

        var liability = cut.FindAll(".odc-alloc-card")[1];
        Assert.Equal("Liability allocation", liability.QuerySelector(".odc-chart-ttl")!.TextContent);
        Assert.Contains("No negative balances yet", liability.TextContent);
        Assert.Null(liability.QuerySelector("svg"));
    }

    [Fact]
    public void Each_ring_is_named_after_its_title_by_default()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsAllocationDonuts>(p => p
            .Add(c => c.AssetSlices, [Slice("Savings", 100)])
            .Add(c => c.LiabilitySlices, [Slice("Card", 50)]));

        var labels = cut.FindAll("svg[role=img]").Select(s => s.GetAttribute("aria-label") ?? "").ToArray();
        Assert.Equal(["Asset allocation — donut chart", "Liability allocation — donut chart"], labels);
    }

    [Fact]
    public void Per_side_format_overrides_the_shared_one()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<OdsAllocationDonuts>(p => p
            .Add(c => c.AssetSlices, [Slice("Savings", 100)])
            .Add(c => c.LiabilitySlices, [Slice("Card", 50)])
            .Add(c => c.Format, (v, _) => $"shared {v}")
            .Add(c => c.LiabilitiesFormat, (v, s) => s is null ? "owed total" : $"owed {v}"));

        var wells = cut.FindAll(".odc-alloc-card");
        Assert.Equal("shared 100", wells[0].QuerySelector(".odc-donut-total-amt")!.TextContent);
        Assert.Equal("owed total", wells[1].QuerySelector(".odc-donut-total-amt")!.TextContent);
    }
}
