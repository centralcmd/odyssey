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
/// toggle persists the preference, so the test flips it twice and leaves the seeded user as it found it.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class DarkThemeAttributeTests(StackFixture fixture) : IAsyncLifetime
{
    private const string DarkIncome = "#4ade80"; // --mint-500, app.css [data-theme='dark']

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
        await E2ESignIn.SignInAsync(page, user.Email, user.Password);

        var toDark = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Switch to dark mode" });
        var toLight = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Switch to light mode" });
        await Expect(toDark.Or(toLight)).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // Let the provider's async preference load settle, so the label and the attribute agree.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var startedDark = await toLight.IsVisibleAsync();
        await ExpectThemeAsync(page, startedDark);

        await (startedDark ? toLight : toDark).ClickAsync();
        await ExpectThemeAsync(page, !startedDark);

        await (startedDark ? toDark : toLight).ClickAsync();
        await ExpectThemeAsync(page, startedDark);
    }

    private static async Task ExpectThemeAsync(IPage page, bool dark)
    {
        if (dark)
        {
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            Assert.Equal("dark", await page.EvaluateAsync<string?>("() => document.documentElement.dataset.theme"));

            // The point of the attribute: a non-MudBlazor dark override now actually resolves.
            var income = await page.EvaluateAsync<string>(
                "() => getComputedStyle(document.documentElement).getPropertyValue('--finance-income')");
            Assert.Equal(DarkIncome, income.Trim().ToLowerInvariant());
        }
        else
        {
            await Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", "dark");
            Assert.Null(await page.EvaluateAsync<string?>("() => document.documentElement.dataset.theme ?? null"));
        }
    }
}
