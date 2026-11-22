using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The API reference (Scalar) contacts nothing outside the deployment (issue #569 §7.2, AC 12-13). Every request
/// to an origin other than the client's and the API's own is aborted and recorded while the page loads, an
/// operation is opened and one "try it" GET is sent; the test fails on any.
/// </summary>
/// <remarks>
/// <para>
/// It runs twice, because Scalar's AI chat defaults ON, without a key, on localhost-like hosts: once on
/// <c>localhost</c>, and once on a host name Scalar does not treat as local, mapped to loopback by Chromium's
/// <c>--host-resolver-rules</c>. The second run signs in on that host itself, through the API's
/// <c>/login?useCookies=true</c>, since a cookie set for <c>localhost</c> is never sent to another host.
/// </para>
/// <para>
/// The docs live on the API: through nginx's same-origin <c>/api/</c> proxy under Compose (which also proves the
/// page resolves its document under the prefix), or on the API's own port under Aspire.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class ApiDocsEgressTests(StackFixture fixture) : IAsyncLifetime
{
    private const string AlternateHost = "docs.odyssey-e2e.net";
    private const string AspireApiBase = "http://localhost:5188";

    private IPlaywright? playwright;

    public async Task InitializeAsync()
    {
        if (fixture.Available)
        {
            playwright = await Playwright.CreateAsync();
        }
    }

    public Task DisposeAsync()
    {
        playwright?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task On_localhost_the_reference_contacts_only_the_deployment()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var apiBase = await ResolveApiBaseAsync(fixture.BaseUrl);

        await using var browser = await playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = fixture.BaseUrl });
        var traffic = await RecordForeignRequestsAsync(context, fixture.BaseUrl, apiBase);

        var admin = DemoUsers.All.First(user => user.Role == "Admin");
        var signIn = await context.NewPageAsync();
        await signIn.GotoAsync("/login");
        await E2ESignIn.SignInAsync(signIn, admin.Email, admin.Password);

        await ExerciseReferenceAsync(context, apiBase, traffic);
    }

    [SkippableFact]
    public async Task On_a_non_local_host_the_reference_contacts_only_the_deployment()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var localApiBase = await ResolveApiBaseAsync(fixture.BaseUrl);
        var apiBase = OnAlternateHost(localApiBase);
        var clientBase = OnAlternateHost(fixture.BaseUrl);

        await using var browser = await playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = [$"--host-resolver-rules=MAP {AlternateHost} 127.0.0.1"],
        });
        await using var context = await browser.NewContextAsync();
        var traffic = await RecordForeignRequestsAsync(context, clientBase, apiBase);

        await SignInThroughTheApiAsync(context, apiBase, DemoUsers.All.First(user => user.Role == "Admin"));

        await ExerciseReferenceAsync(context, apiBase, traffic);
    }

    private static async Task ExerciseReferenceAsync(IBrowserContext context, string apiBase, Traffic traffic)
    {
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
            {
                traffic.CspViolations.Add(message.Text);
            }
        };

        var response = await page.GotoAsync(apiBase + "/scalar/v1");
        Assert.Equal((int)HttpStatusCode.OK, response?.Status);

        // The page fetched its document from under its own base path.
        await page.GetByRole(AriaRole.Button, new() { Name = "Open Group - Currencies" }).ClickAsync();
        await page.Locator("a").Filter(new() { HasText = "List currencies." }).First.ClickAsync();
        var tryIt = await page.RunAndWaitForResponseAsync(
            async () =>
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Test Request (get /api/currencies)", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = false }).First.ClickAsync();
            },
            candidate => new Uri(candidate.Url).AbsolutePath == new Uri(apiBase + "/api/currencies").AbsolutePath);

        // Same origin, carrying the session: an authenticated GET succeeds.
        Assert.Equal((int)HttpStatusCode.OK, tryIt.Status);
        Assert.Equal(new Uri(apiBase).GetLeftPart(UriPartial.Authority), new Uri(tryIt.Url).GetLeftPart(UriPartial.Authority));

        Assert.True(traffic.Aborted.Count == 0, "Requests to foreign origins: " + string.Join(", ", traffic.Aborted));
        Assert.True(traffic.DocumentRequests.Count > 0, "The page never fetched its OpenAPI document.");
        Assert.All(traffic.DocumentRequests, url => Assert.StartsWith(apiBase + "/openapi/", url, StringComparison.Ordinal));
        Assert.Empty(traffic.CspViolations);

        // No AI chat entry point, and no visible way out to scalar.com.
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { NameRegex = new("ask ai|agent", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).CountAsync());
        var outbound = page.Locator("a[href*='scalar.com']");
        for (var index = 0; index < await outbound.CountAsync(); index++)
        {
            Assert.False(await outbound.Nth(index).IsVisibleAsync(), "A visible link to scalar.com is on the page.");
        }
    }

    private static async Task<Traffic> RecordForeignRequestsAsync(IBrowserContext context, string clientBase, string apiBase)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new Uri(clientBase).GetLeftPart(UriPartial.Authority),
            new Uri(apiBase).GetLeftPart(UriPartial.Authority),
        };
        var traffic = new Traffic();

        await context.RouteAsync("**/*", async route =>
        {
            var url = new Uri(route.Request.Url);
            if (!allowed.Contains(url.GetLeftPart(UriPartial.Authority)))
            {
                traffic.Aborted.Add(route.Request.Url);
                await route.AbortAsync();
                return;
            }

            if (url.AbsolutePath.EndsWith("/openapi/v1.json", StringComparison.Ordinal))
            {
                traffic.DocumentRequests.Add(url.GetLeftPart(UriPartial.Path));
            }

            await route.ContinueAsync();
        });

        return traffic;
    }

    private static async Task SignInThroughTheApiAsync(IBrowserContext context, string apiBase, DemoUser user)
    {
        // From inside a page on that host: only the browser resolves it through --host-resolver-rules (Playwright's
        // APIRequest resolves in Node), and a same-origin fetch stores the cookie for exactly this host.
        var page = await context.NewPageAsync();
        await page.GotoAsync(apiBase + "/healthz");

        // The sign-in surface is rate-limited by network (see E2ESignIn); wait a refusal out as the notice says.
        for (var attempt = 1; ; attempt++)
        {
            var result = await page.EvaluateAsync<JsonElement>(
                """
                async ([email, password]) => {
                    const token = (await (await fetch('api/antiforgery/token', { credentials: 'same-origin' })).json()).token;
                    const login = await fetch('login?useCookies=true', {
                        method: 'POST',
                        credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': token },
                        body: JSON.stringify({ email, password }),
                    });
                    return { status: login.status, retryAfter: login.headers.get('retry-after') };
                }
                """,
                new[] { user.Email, user.Password });
            var status = result.GetProperty("status").GetInt32();

            if (status == (int)HttpStatusCode.TooManyRequests && attempt < 4)
            {
                var retryAfter = int.TryParse(result.GetProperty("retryAfter").GetString(), out var seconds) ? seconds : 60;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfter + 1, 90)));
                continue;
            }

            Assert.Equal((int)HttpStatusCode.OK, status);
            await page.CloseAsync();
            return;
        }
    }

    /// <summary>
    /// The API root: nginx's same-origin <c>/api</c> prefix under Compose (whose <c>/api/healthz</c> answers JSON),
    /// otherwise the API's own port (Aspire, where the client is served by Kestrel without a proxy).
    /// </summary>
    private static async Task<string> ResolveApiBaseAsync(string clientBase)
    {
        using var client = new HttpClient();
        try
        {
            using var response = await client.GetAsync(clientBase + "/api/healthz");
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                return clientBase + "/api";
            }
        }
        catch (HttpRequestException)
        {
        }

        return Environment.GetEnvironmentVariable("E2E_API_BASE_URL")?.TrimEnd('/') ?? AspireApiBase;
    }

    private static string OnAlternateHost(string url)
    {
        var builder = new UriBuilder(url) { Host = AlternateHost };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private sealed class Traffic
    {
        public List<string> Aborted { get; } = [];

        public List<string> DocumentRequests { get; } = [];

        public List<string> CspViolations { get; } = [];
    }
}
