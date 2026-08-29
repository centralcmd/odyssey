using Microsoft.Playwright;
using Odyssey.TestData;
using Odyssey.TestData.Catalog;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// First end-to-end smoke flow against the full running stack (nginx → Blazor WASM → API →
/// MariaDB): sign in as a seeded demo user and confirm seeded data is rendered. This exercises
/// the whole chain — cookie auth, the SPA, the API, and the demo seed — in a real browser.
/// </summary>
[Collection(StackCollection.Name)]
public sealed class LoginSmokeTests(StackFixture fixture) : IAsyncLifetime
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

    [SkippableFact]
    public async Task Demo_user_can_sign_in_and_see_seeded_accounts()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = DemoUsers.All.First(user => user.Role == "Admin");

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync("/login");
        await page.GetByLabel("Username or Email").FillAsync(admin.Email);
        await page.GetByLabel("Password").FillAsync(admin.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();

        // Login navigates away from /login on success.
        await page.WaitForURLAsync(url => !url.Contains("/login"), new PageWaitForURLOptions { Timeout = 20_000 });

        // The accounts page is auth-gated; reaching it and seeing a seeded account proves the
        // whole chain (auth cookie + SPA + API + demo seed) end to end.
        await page.GotoAsync("/accounts");
        await Expect(page.GetByText(Accounts.EverydayChecking).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });
    }
}
