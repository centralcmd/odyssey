using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// The rail's dark-mode toggle stamps <c>data-theme="dark"</c> on the root element, and clears it for
/// light mode (issue #228).
/// </summary>
/// <remarks>
/// <para>
/// MudBlazor 9.10 writes no <c>data-theme</c>, so every <c>[data-theme='dark']</c> override in the
/// client stylesheets — finance, status, tag, chart, nav and focus tokens — was unreachable while the
/// MudBlazor palette itself switched, and the app merely <em>looked</em> dark. The contrast guards parse
/// the CSS text and cannot see that; this reads the running document.
/// </para>
/// <para>
/// One test, one sign-in (the identity surface is rate-limited by network across this collection). The
/// toggle persists the preference, so the test flips it twice and a <c>finally</c> flips it back if a
/// step in between fails, leaving the seeded user as it found it.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class DarkThemeAttributeTests(StackFixture fixture) : IAsyncLifetime
{
    private const string DarkIncome = "#4ade80"; // --mint-500, app.css [data-theme='dark']

    private const string LightIncome = "#15803d"; // --mint-700, app.css :root

    private const string PreferencesPath = "/api/user-preferences/preferences-page";

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
    public async Task Toggling_dark_mode_owns_the_root_data_theme_attribute()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();

        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        await page.GotoAsync("/login");

        // The saved preference arrives after sign-in; until it does, the rail shows the dark-first
        // default, so reading the starting state any earlier could race a light-mode preference.
        var preferenceLoaded = page.WaitForResponseAsync(
            response => response.Url.Contains(PreferencesPath, StringComparison.Ordinal)
                        && response.Request.Method == "GET",
            new PageWaitForResponseOptions { Timeout = 60_000 });
        await E2ESignIn.SignInAsync(page, user.Email, user.Password);
        await preferenceLoaded;

        var startedDark = await SettledThemeAsync(page);
        await ExpectThemeAsync(page, startedDark);

        var current = startedDark;
        try
        {
            current = await ToggleAsync(page, current);
            await ExpectThemeAsync(page, current);

            current = await ToggleAsync(page, current);
            await ExpectThemeAsync(page, current);
        }
        finally
        {
            // A mid-test failure must not leave the seeded Admin on the other theme for every later run.
            if (current != startedDark)
            {
                await ToggleAsync(page, current);
            }
        }
    }

    /// <summary>
    /// Polls until the rail's toggle label and the root attribute agree, and returns whether that
    /// agreed state is dark. Polling the state itself rather than a network-idle heuristic, which a
    /// Blazor render that follows a response does not wait for.
    /// </summary>
    private static async Task<bool> SettledThemeAsync(IPage page)
    {
        var handle = await page.WaitForFunctionAsync(
            """
            () => {
                const toggle = document.querySelector(
                    'button[aria-label="Switch to light mode"], button[aria-label="Switch to dark mode"]');
                if (!toggle) return null;
                const dark = toggle.getAttribute('aria-label') === 'Switch to light mode';
                return (document.documentElement.dataset.theme === 'dark') === dark ? (dark ? 'dark' : 'light') : null;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        return await handle.JsonValueAsync<string>() == "dark";
    }

    /// <summary>Flips the theme from the rail and waits for the save, so the stored preference follows.</summary>
    private static async Task<bool> ToggleAsync(IPage page, bool fromDark)
    {
        var name = fromDark ? "Switch to light mode" : "Switch to dark mode";
        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name }).ClickAsync(),
            response => response.Url.Contains(PreferencesPath, StringComparison.Ordinal)
                        && response.Request.Method == "PUT");
        return !fromDark;
    }

    private static async Task ExpectThemeAsync(IPage page, bool dark)
    {
        if (dark)
        {
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            Assert.Equal("dark", await page.EvaluateAsync<string?>("() => document.documentElement.dataset.theme"));
        }
        else
        {
            await Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", "dark");
            Assert.Null(await page.EvaluateAsync<string?>("() => document.documentElement.dataset.theme ?? null"));
        }

        // The point of the attribute: the matching finance token actually resolves, in both
        // directions, so a dark override stuck on (or never reached) is caught.
        var income = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--finance-income')");
        Assert.Equal(dark ? DarkIncome : LightIncome, income.Trim().ToLowerInvariant());
    }
}
