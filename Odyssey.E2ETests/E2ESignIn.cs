using Microsoft.Playwright;

namespace Odyssey.E2ETests;

/// <summary>
/// The browser suite's one sign-in, shared by every test class in the collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sign-in surface is rate-limited by NETWORK, not by account.</b> Every test class in this
/// collection therefore spends from one budget over a short window, and the class that happens to run
/// last is the one refused — with the refusal shown on the page rather than raised, so the symptom is a
/// sign-in that never completes and reads as the app being slow. Each class that hand-rolled the
/// sequence waited on a navigation and then simply timed out.
/// </para>
/// <para>
/// This waits the window out and retries, exactly as the notice instructs. It is also what keeps the
/// suite deterministic as classes are added: the budget is shared, so a new browser context is what
/// tips an already-tight suite over.
/// </para>
/// <para>
/// The wait POLLS <see cref="IPage.Url"/> rather than waiting on a navigation event, because this is a
/// Blazor WASM app: a successful sign-in is a client-side route change, so there may be no navigation
/// to wait for at all.
/// </para>
/// </remarks>
internal static class E2ESignIn
{
    /// <summary>The notice the surface shows when the shared limiter refuses an attempt.</summary>
    private const string RateLimitedNotice = "Too many sign-in attempts";

    /// <summary>How long one attempt is given before the page is inspected for a refusal.</summary>
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(30);

    /// <summary>The notice says to wait a minute; taking it at its word beats retrying into it.</summary>
    private static readonly TimeSpan LimiterWindow = TimeSpan.FromSeconds(65);

    /// <summary>
    /// Fills and submits the sign-in form on an already-loaded <c>/login</c> page, returning once the
    /// app has left it.
    /// </summary>
    public static async Task SignInAsync(IPage page, string email, string password, int attempts = 3)
    {
        await page.GetByLabel("Username or Email").FillAsync(email);
        await page.GetByLabel("Password").FillAsync(password);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();

            var deadline = DateTime.UtcNow + AttemptBudget;
            while (DateTime.UtcNow < deadline)
            {
                if (HasLeftLogin(page))
                {
                    return;
                }

                if ((await page.Locator("body").InnerTextAsync()).Contains(RateLimitedNotice, StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(250);
            }

            if (HasLeftLogin(page))
            {
                return;
            }

            if (attempt < attempts)
            {
                await Task.Delay(LimiterWindow);
            }
        }

        throw new TimeoutException(
            $"Signing in as {email} never left the login page (url={page.Url}).");
    }

    private static bool HasLeftLogin(IPage page) => !page.Url.Contains("/login", StringComparison.Ordinal);
}
