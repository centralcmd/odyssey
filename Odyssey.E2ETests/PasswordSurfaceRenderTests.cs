using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// The shared password-change form actually renders in a browser.
/// </summary>
/// <remarks>
/// <para>
/// Every other test over <c>OdsPasswordChangeForm</c> reads its source as text
/// (<c>Odyssey.Client.Tests.PasswordSurfaceSourceTests</c>), and the client has no renderer in its unit
/// tier — so a component that threw on every render still passed the whole suite. That is not
/// hypothetical: the form shipped binding <c>UserAttributes</c> on a field that also carried a loose
/// <c>autocomplete</c> attribute, which Blazor refuses at render time, and it reached a user as a blank
/// page with an unhandled exception in the console.
/// </para>
/// <para>
/// This is the cheapest place to catch that class of defect, because it needs a real Blazor runtime.
/// Asserting on console errors as well as on the visible field is deliberate: a render failure inside a
/// child component can leave the surrounding page looking fine.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class PasswordSurfaceRenderTests(StackFixture fixture) : IAsyncLifetime
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
    public async Task The_account_security_page_renders_the_password_form_without_component_errors()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        var errors = new List<string>();

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);

        await page.GotoAsync("/login");
        await page.GetByLabel("Username or Email").FillAsync(user.Email);
        await page.GetByLabel("Password").FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/login"), new PageWaitForURLOptions { Timeout = 30_000 });

        // Only failures from the page under test, not any the login flow happened to log.
        errors.Clear();
        await page.GotoAsync("/account");

        // Scoped to the form, not the page: /account also has an email-change section with its own
        // "Current password" field, so an unscoped GetByLabel matches that one instead.
        var form = page.Locator("form.odc-pw-form");
        await Expect(form).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // All three fields, not just the first. A render failure part-way through the form truncates
        // everything after it — which is exactly how the original defect presented, and a check on the
        // first field alone would have called that healthy.
        await Expect(form.Locator("input[type=password]")).ToHaveCountAsync(3);

        // WCAG 1.3.5, asserted where it actually has to hold: on the rendered inputs. The source lints
        // in Odyssey.Client.Tests can only see the markup, and markup that never reaches the DOM reads
        // identically to markup that does.
        Assert.Equal(
            ["current-password", "new-password", "new-password"],
            await form.Locator("input[type=password]").EvaluateAllAsync<string[]>(
                "nodes => nodes.map(n => n.getAttribute('autocomplete'))"));

        var componentErrors = errors
            .Where(error => error.Contains("UserAttributes", StringComparison.Ordinal)
                            || error.Contains("Unhandled exception rendering component", StringComparison.Ordinal))
            .ToList();
        Assert.True(componentErrors.Count == 0, string.Join("\n", componentErrors));
    }
}
