using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// No page scrolls sideways on a phone. At a 420px viewport, 11 of these 13 routes measured wider than
/// the screen (up to 1174px): a chart's visually-hidden data table that a <c>&lt;table&gt;</c>'s ignored
/// width let escape its clip, a page-header action cluster that never wrapped, a split sort select, and
/// record tables with no scroll frame of their own.
/// </summary>
/// <remarks>
/// Layout is the one thing bUnit cannot see, so this is the only tier that can hold the line. Wide content
/// may still scroll inside its own frame (a record table does) — what is asserted is the page itself.
/// One test, one sign-in: the identity surface is rate-limited by network across this collection.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class PhoneWidthOverflowTests(StackFixture fixture) : IAsyncLifetime
{
    private const int PhoneWidth = 420;

    private static readonly string[] Routes =
    [
        "/", "/accounts", "/transactions", "/transaction-tags", "/users", "/tax-statements", "/account",
        "/budgets", "/contracts", "/properties", "/contacts", "/journal", "/settings",
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
            ViewportSize = new ViewportSize { Width = PhoneWidth, Height = 900 },
        });
        var page = await context.NewPageAsync();

        // Admin holds every claim these routes are gated on, so each one renders its real content.
        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, user.Email, user.Password);

        var offenders = new List<string>();
        foreach (var route in Routes)
        {
            await page.GotoAsync(route, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await SettledAsync(page);

            var width = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
            if (width > PhoneWidth)
            {
                offenders.Add($"{route}: {width}px");
            }
        }

        Assert.True(offenders.Count == 0, $"Wider than a {PhoneWidth}px viewport: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Waits until the route has swapped its loading placeholders for content: a skeleton is narrower than
    /// the data it stands for, so measuring one would pass a page that overflows a moment later.
    /// </summary>
    private static async Task SettledAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            """
            () => document.querySelector('h1')
                && !document.querySelector('[aria-busy="true"], .mud-skeleton')
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        // The charts measure their plot after the data render and re-render once with the width.
        await page.WaitForTimeoutAsync(750);
    }
}
