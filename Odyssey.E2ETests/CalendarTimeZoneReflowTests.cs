using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The event dialog's time-zone field and two-zone line reflow (issue #520 §6.7, AC 16): in both themes, at 200 % text
/// and at a 320 px viewport, neither clips and nothing scrolls sideways. The browser runs in America/New_York, so a new
/// event defaults to that zone, and picking Europe/Oslo brings up the two-zone line. Layout is the one thing bUnit
/// cannot see.
/// </summary>
/// <remarks>
/// The confirm step's reflow is not driven here: reaching it needs a keyed series carrying an override, which the demo
/// seed does not produce. Its markup is the same flex-wrapping region as the alert above it, and the screenshots taken
/// for the PR cover it. One test, one sign-in; nothing is saved.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class CalendarTimeZoneReflowTests(StackFixture fixture) : IAsyncLifetime
{
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

    public sealed class Measurement
    {
        public string[] Overflows { get; set; } = [];
    }

    private static async Task<string[]> OverflowsAsync(IPage page) =>
        (await page.EvaluateAsync<Measurement>(
            """
            () => {
                const overflows = [];
                const dialog = document.querySelector('.mud-dialog');
                if (!dialog) return { overflows: ['no dialog'] };
                for (const sel of ['#cal-ev-tz', '#cal-ev-twozone', '#cal-ev-tz-help']) {
                    const el = document.querySelector(sel);
                    if (!el) { overflows.push(sel + ' missing'); continue; }
                    const r = el.getBoundingClientRect(), d = dialog.getBoundingClientRect();
                    if (r.right > d.right + 1) overflows.push(sel + ' leaves the dialog');
                    if (el.scrollWidth > el.clientWidth + 1 && sel !== '#cal-ev-tz') overflows.push(sel + ' clips');
                }
                if (dialog.scrollWidth > dialog.clientWidth + 1) overflows.push('the dialog scrolls sideways');
                if (document.documentElement.scrollWidth > window.innerWidth + 1) overflows.push('the page scrolls sideways');
                return { overflows };
            }
            """)).Overflows;

    private static Task SetTextScaleAsync(IPage page, string scale) =>
        page.EvaluateAsync("s => { document.documentElement.style.fontSize = s; }", scale);

    private static async Task<bool> IsDarkAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.dataset.theme === 'dark'");

    private static Task ToggleThemeAsync(IPage page, bool fromDark) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = fromDark ? "Switch to light mode" : "Switch to dark mode" }).ClickAsync();

    private static async Task OpenWithOsloAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New event" }).First.ClickAsync();
        var zone = page.Locator("#cal-ev-tz");
        await zone.WaitForAsync();
        await zone.FillAsync("oslo");
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Europe/Oslo" }).First.ClickAsync();
        await page.Locator("#cal-ev-twozone").WaitForAsync();
    }

    [SkippableFact] // AC 16
    public async Task The_zone_field_and_two_zone_line_reflow_in_both_themes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            TimezoneId = "America/New_York",
        });
        var page = await context.NewPageAsync();
        var owner = DemoUsers.All.First(candidate => candidate.Role == "Owner");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, owner.Email, owner.Password);

        await page.GotoAsync("/calendar");
        var startedDark = await IsDarkAsync(page);
        var flipped = false;
        try
        {
            foreach (var pass in new[] { "first theme", "second theme" })
            {
                await page.SetViewportSizeAsync(1366, 900);
                await OpenWithOsloAsync(page);

                await SetTextScaleAsync(page, "200%");
                var overflows = await OverflowsAsync(page);
                Assert.True(overflows.Length == 0, $"{pass}, 200% text: " + string.Join("; ", overflows));

                await SetTextScaleAsync(page, "100%");
                await page.SetViewportSizeAsync(320, 800);
                overflows = await OverflowsAsync(page);
                Assert.True(overflows.Length == 0, $"{pass}, 320 px: " + string.Join("; ", overflows));

                await page.SetViewportSizeAsync(1366, 900);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel" }).ClickAsync();
                await page.Locator("#cal-ev-tz").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
                if (!flipped)
                {
                    await ToggleThemeAsync(page, startedDark);
                    flipped = true;
                    await page.WaitForFunctionAsync(
                        $"() => (document.documentElement.dataset.theme === 'dark') === {(!startedDark).ToString().ToLowerInvariant()}");
                }
            }
        }
        finally
        {
            await SetTextScaleAsync(page, "100%");
            if (flipped)
            {
                await page.Keyboard.PressAsync("Escape");
                await ToggleThemeAsync(page, !startedDark);
            }
        }
    }
}
