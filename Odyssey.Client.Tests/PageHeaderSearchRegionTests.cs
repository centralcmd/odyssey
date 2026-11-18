using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PageHeader"/> marks its Search region with <c>ph-search-region</c>, the hook the
/// recessed fill and the quiet control border in <c>PageHeader.razor.css</c> hang on. Rendered,
/// because the class is a property of the markup: without it the stylesheet rules match nothing and
/// every test of the rules themselves still passes.
/// </summary>
public class PageHeaderSearchRegionTests
{
    private static IRenderedComponent<PageHeader> Render()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx.Render<PageHeader>(p => p
            .Add(h => h.Title, "Transactions")
            .Add(h => h.OverviewContent, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"overview-body\">overview</p>")))
            .Add(h => h.OverviewOpenByDefault, true)
            .Add(h => h.SearchContent, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"search-body\">search</p>")))
            .Add(h => h.SearchOpenByDefault, true));
    }

    [Fact]
    public void The_search_region_carries_the_search_region_class()
    {
        var region = Render().Find("#search-body").ParentElement!;

        Assert.Contains("ph-region", region.ClassList);
        Assert.Contains("ph-search-region", region.ClassList);
    }

    [Fact]
    public void The_overview_region_does_not()
    {
        var region = Render().Find("#overview-body").ParentElement!;

        Assert.Contains("ph-region", region.ClassList);
        Assert.DoesNotContain("ph-search-region", region.ClassList);
    }
}
