using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The sortable list's keyboard lift in a real browser (PR #598 test review): <c>wwwroot/js/sortable-list.js</c> stops
/// Space from activating the handle as a button and the arrows from scrolling, which the bUnit tier only stubs. In the
/// New recipe dialog's Ingredients step, Space lifts the first ingredient, ArrowDown moves it, Space drops it, and the
/// order of the name fields follows.
/// </summary>
/// <remarks>
/// Nothing is saved: the dialog is left open and the browser context is thrown away, so the seeded data is untouched.
/// One test, one sign-in (<see cref="E2ESignIn"/>).
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class RecipeIngredientKeyboardReorderTests(StackFixture fixture) : IAsyncLifetime
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
    public async Task Space_arrow_down_space_moves_an_ingredient_down_one_place()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await context.NewPageAsync();

        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, user.Email, user.Password);

        await page.GotoAsync("/recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New recipe" }).First.ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByRole(AriaRole.Navigation, new LocatorGetByRoleOptions { Name = "Recipe steps" })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Ingredients" }).ClickAsync();

        var list = dialog.Locator(".rc-sort-ing");
        var names = list.Locator("fieldset[data-sort-row] .rc-ing-name input");
        await names.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // The blank recipe opens with one empty ingredient; name it and add a second.
        await names.Nth(0).FillAsync("salt");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add ingredient" }).ClickAsync();
        await Assertions.Expect(names).ToHaveCountAsync(2);
        await names.Nth(1).FillAsync("flour");
        await Assertions.Expect(names.Nth(0)).ToHaveValueAsync("salt");
        await Assertions.Expect(names.Nth(1)).ToHaveValueAsync("flour");

        var handle = list.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Move ingredient 1", Exact = true });
        await handle.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(handle).ToHaveAttributeAsync("aria-pressed", "true");

        await page.Keyboard.PressAsync("ArrowDown");
        // The lifted handle keeps focus as its row moves: it is now the second row's.
        var moved = list.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Move ingredient 2", Exact = true });
        await Assertions.Expect(moved).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(moved).ToBeFocusedAsync();

        await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(moved).ToHaveAttributeAsync("aria-pressed", "false");

        await Assertions.Expect(names.Nth(0)).ToHaveValueAsync("flour");
        await Assertions.Expect(names.Nth(1)).ToHaveValueAsync("salt");
        await Assertions.Expect(list.Locator("[role=status][aria-live=assertive]")).ToContainTextAsync("Dropped at position 2 of 2.");
        // Space dropped the row; it did not also close or submit the dialog.
        await Assertions.Expect(dialog).ToBeVisibleAsync();
    }
}
