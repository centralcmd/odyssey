using System.Text.Json;
using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The tax-statements overview charts draw their axis labels at a true 10px (issue #274).
/// </summary>
/// <remarks>
/// <para>
/// The labels are sized in SVG user units, so their pixel size is
/// <c>fontSize × renderedWidth / viewBoxWidth</c>. Under the old fixed 1000-unit viewBox the three-up
/// cards (about 350px each) drew them at about 3.5px. The fix is a <c>ResizeObserver</c> bridge
/// (<c>plot-width.js</c>) that sets the viewBox width to the measured plot width. bUnit cannot run that
/// module or lay anything out, so this is the tier that proves the real bridge, the real layout and the
/// <c>contain: inline-size</c> rule together — at the desktop width and again after a resize, which only
/// a live <c>ResizeObserver</c> can deliver.
/// </para>
/// <para>One test, one sign-in: the identity surface is rate-limited by network across this collection.</para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class ChartAxisLabelSizeTests(StackFixture fixture) : IAsyncLifetime
{
    private const double AxisPx = 10;
    private const double Tolerance = 0.25;

    private IPlaywright? playwright;
    private IBrowser? browser;

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();
    }

    private sealed record Chart(double RenderedWidth, double ViewBoxWidth, double FontSize, bool ClippedLeft);

    [SkippableFact]
    public async Task Tax_overview_axis_labels_render_at_ten_pixels_at_any_width()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
        var page = await context.NewPageAsync();

        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, user.Email, user.Password);
        await page.GotoAsync("/tax-statements");

        var desktop = await SettledChartsAsync(page);
        AssertTrueTenPixels(desktop);
        Assert.All(desktop, c => Assert.InRange(c.RenderedWidth, 250, 500));

        await page.SetViewportSizeAsync(900, 900);
        var narrow = await SettledChartsAsync(page, previousWidth: desktop[0].RenderedWidth);
        AssertTrueTenPixels(narrow);
    }

    private static void AssertTrueTenPixels(IReadOnlyList<Chart> charts)
    {
        Assert.Equal(3, charts.Count);
        foreach (var chart in charts)
        {
            Assert.InRange(chart.FontSize * chart.RenderedWidth / chart.ViewBoxWidth, AxisPx - Tolerance, AxisPx + Tolerance);
            Assert.False(chart.ClippedLeft, "a y-axis label starts left of the viewBox");
        }
    }

    /// <summary>
    /// Waits until the three overview charts exist and each viewBox has caught up with its rendered width
    /// (and, after a resize, until that width has actually changed), then reads them. Polling the state
    /// itself, since the measurement lands in a render that follows no network response.
    /// </summary>
    private static async Task<IReadOnlyList<Chart>> SettledChartsAsync(IPage page, double previousWidth = -1)
    {
        var handle = await page.WaitForFunctionAsync(
            """
            (previous) => {
                const svgs = [...document.querySelectorAll('svg.odc-line-svg')];
                if (svgs.length !== 3) return null;
                const charts = svgs.map((svg) => {
                    const label = svg.querySelector('.odc-lc-axis text');
                    return {
                        renderedWidth: svg.getBoundingClientRect().width,
                        viewBoxWidth: svg.viewBox.baseVal.width,
                        fontSize: label ? parseFloat(getComputedStyle(label).fontSize) : 0,
                        clippedLeft: [...svg.querySelectorAll('.odc-lc-axis text')].some((t) => t.getBBox().x < -0.5),
                    };
                });
                const settled = charts.every((c) => c.fontSize > 0 && Math.abs(c.renderedWidth - c.viewBoxWidth) < 1)
                    && Math.abs(charts[0].renderedWidth - previous) >= 1;
                return settled ? JSON.stringify(charts) : null;
            }
            """,
            previousWidth,
            new PageWaitForFunctionOptions { Timeout = 60_000 });

        var json = await handle.JsonValueAsync<string>();
        return JsonSerializer.Deserialize<List<Chart>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
