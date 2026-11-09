using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// No page scrolls sideways on a phone. At a 420px viewport, 11 of the first 13 routes measured wider than
/// the screen (up to 1174px): a chart's visually-hidden data table that a <c>&lt;table&gt;</c>'s ignored
/// width let escape its clip, a page-header action cluster that never wrapped, a split sort select, and
/// record tables with no scroll frame of their own. <c>/calendar</c> then overflowed at 420px (issue #291),
/// and at WCAG 1.4.10's 320px reference width 17 routes did (issue #389): fixed 220-300px floors on filter
/// controls, summary grids and cards, and one-word page titles beside the header's icon tile.
/// </summary>
/// <remarks>
/// Layout is the one thing bUnit cannot see, so this is the only tier that can hold the line. Wide content
/// may still scroll inside its own frame (a record table does) — what is asserted is the page itself.
/// One test, one sign-in: the identity surface is rate-limited by network across this collection.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class PhoneWidthOverflowTests(StackFixture fixture) : IAsyncLifetime
{
    /// <summary>WCAG 1.4.10's reference width, a common phone, and the width the first defects were found at.</summary>
    private static readonly int[] PhoneWidths = [320, 390, 420];

    /// <summary>Every signed-in page route.</summary>
    private static readonly string[] Routes =
    [
        "/", "/accounts", "/transactions", "/transaction-tags", "/users", "/tax-statements", "/account",
        "/budgets", "/contracts", "/properties", "/contacts", "/journal", "/settings",
        "/albums", "/analysis-log", "/background-jobs", "/calendar", "/currencies", "/exchange-rates", "/files",
        "/instrument-prices", "/instruments", "/journal-tags", "/legal-documents", "/locations", "/photo-tags",
        "/photos", "/preferences", "/roles", "/task-tags", "/tasks", "/weather-points",
    ];

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

    [SkippableFact]
    public async Task No_page_is_wider_than_a_phone_viewport()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = PhoneWidths[0], Height = 900 },
        });
        var page = await context.NewPageAsync();

        // Admin holds every claim these routes are gated on, so each one renders its real content.
        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, user.Email, user.Password);

        var offenders = new List<string>();
        foreach (var width in PhoneWidths)
        {
            await page.SetViewportSizeAsync(width, 900);
            foreach (var route in Routes)
            {
                await page.GotoAsync(route, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                await SettledAsync(page);
                await RecordAsync(page, width, route, offenders);
            }

            // Issue #389: a location's expanded record (its weather point, weather and history sections) too.
            if (width < 420)
            {
                await page.GotoAsync("/locations", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                await SettledAsync(page);
                var triggers = page.Locator(".odc-record-trigger");
                var count = await triggers.CountAsync();
                Assert.True(count > 0, "The seeded stack lists no locations to expand.");
                for (var i = 0; i < count; i++)
                {
                    await triggers.Nth(i).ClickAsync();
                    await SettledAsync(page);
                    await RecordAsync(page, width, $"/locations, record {i + 1} expanded", offenders);
                    await triggers.Nth(i).ClickAsync();
                }
            }
        }

        Assert.True(offenders.Count == 0, $"Wider than the phone viewport: {string.Join(", ", offenders)}");
    }

    private static async Task RecordAsync(IPage page, int width, string what, List<string> offenders)
    {
        var scrollWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        if (scrollWidth > width)
        {
            offenders.Add($"{what} at {width}px: {scrollWidth}px");
        }
    }

    /// <summary>
    /// Waits until the route has swapped its loading placeholders for content: a skeleton is narrower than
    /// the data it stands for, so measuring one would pass a page that overflows a moment later.
    /// </summary>
    private static async Task SettledAsync(IPage page)
    {
        // A chart measures its plot after the data render and re-renders once with the width, so it is
        // settled when its viewBox has caught up with its rendered width (the ChartAxisLabelSizeTests rule).
        await page.WaitForFunctionAsync(
            """
            () => document.querySelector('h1')
                && !document.querySelector('[aria-busy="true"], .mud-skeleton')
                && [...document.querySelectorAll('svg.odc-line-svg')].every((svg) =>
                    Math.abs(svg.getBoundingClientRect().width - svg.viewBox.baseVal.width) < 1)
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }
}
