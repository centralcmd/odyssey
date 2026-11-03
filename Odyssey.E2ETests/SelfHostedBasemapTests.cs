using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// Issue #384 AC 16–20 in a real browser, against a stack serving the self-hosted basemap: the Compose stack
/// with <c>docker-compose.selfhosted-basemap.yml</c>, which mounts the committed fixture
/// (<c>TestShared/Basemap/</c>) and selects the self-hosted styles.
/// </summary>
/// <remarks>
/// Opt-in. Against any other stack these tests skip — unless <c>ODYSSEY_E2E_SELFHOSTED_BASEMAP=1</c> declares
/// the override is in use, in which case a stack that does not serve it fails them. Nothing is routed here:
/// the point is that every request really goes to the deployment and nowhere else.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class SelfHostedBasemapTests(StackFixture fixture) : IAsyncLifetime
{
    private const string Declared = "ODYSSEY_E2E_SELFHOSTED_BASEMAP";

    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static IPlaywright? playwright;
    private static IBrowser? browser;
    private static IBrowserContext? browserContext;
    private static string? notSelfHosted;

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
            return;

        await SessionGate.WaitAsync();
        try
        {
            if (browserContext is not null)
                return;

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"],
            });
            browserContext = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = fixture.BaseUrl,
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });
            var page = await browserContext.NewPageAsync();
            await page.GotoAsync("/login", new PageGotoOptions { Timeout = 120_000 });
            var admin = DemoUsers.All.First(user => user.Role == "Admin");
            await E2ESignIn.SignInAsync(page, admin.Email, admin.Password);

            var light = await page.EvaluateAsync<string?>(
                "async () => { const r = await fetch('/api/api/map-styles', { credentials: 'include' }); return r.ok ? (await r.json()).lightStyleUrl : null; }");
            if (light != "/maps/self-hosted-light.json")
                notSelfHosted = $"The stack does not serve the self-hosted basemap (light style: {light ?? "unavailable"}); bring it up with docker-compose.selfhosted-basemap.yml.";
            await page.CloseAsync();
        }
        finally
        {
            SessionGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void SkipUnlessSelfHosted()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        if (notSelfHosted is null)
            return;
        if (Environment.GetEnvironmentVariable(Declared) == "1")
            Assert.Fail(notSelfHosted);
        Skip.If(true, notSelfHosted);
    }

    // ── AC 16, 17, 18 ─────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_map_draws_from_the_deployment_alone_in_both_themes()
    {
        SkipUnlessSelfHosted();
        var page = await browserContext!.NewPageAsync();
        var requests = new List<IRequest>();
        var statuses = new List<(string Url, int Status)>();
        // Events keep arriving while the assertions run (tiles in flight), so every read takes a snapshot.
        var gate = new object();
        page.Request += (_, r) => { lock (gate) requests.Add(r); };
        page.Response += (_, r) => { lock (gate) statuses.Add((r.Url, r.Status)); };
        int StyleRequests() { lock (gate) return requests.Count(r => r.Url.Contains("/maps/self-hosted-", StringComparison.Ordinal)); }

        await OpenAsync(page, "Cabin");
        // Each zoom step settles before the next (a step rounds from the current zoom), so the map has
        // requested the deeper tiles by the time the loop ends.
        var host = page.Locator(".loc-map-host");
        for (var z = 15; z <= 18; z++)
        {
            await page.Locator(".loc-map").GetByRole(AriaRole.Button, new() { Name = "Zoom in", Exact = true }).ClickAsync();
            await Expect(host).ToHaveAttributeAsync("data-zoom", $"{z}.00");
        }

        // The theme toggle swaps to the other self-hosted style: wait for that request rather than a fixed time.
        var otherStyle = page.WaitForRequestAsync(r => r.Url.EndsWith("/maps/self-hosted-light.json", StringComparison.Ordinal)
            || r.Url.EndsWith("/maps/self-hosted-dark.json", StringComparison.Ordinal), new() { Timeout = 15_000 });
        var before = StyleRequests();
        await ToggleThemeAsync(page);
        await otherStyle;
        await Expect(page.Locator(".loc-map-caption")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.True(StyleRequests() > before);
        IRequest[] seen;
        (string Url, int Status)[] answered;
        lock (gate)
        {
            seen = [.. requests];
            answered = [.. statuses];
        }

        var origin = new Uri(fixture.BaseUrl).GetLeftPart(UriPartial.Authority);
        Assert.All(seen, r => Assert.True(
            r.Url.StartsWith(origin, StringComparison.Ordinal) || r.Url.StartsWith("data:", StringComparison.Ordinal) || r.Url.StartsWith("blob:", StringComparison.Ordinal),
            $"Request left the deployment: {r.Url}"));

        Assert.Contains(seen, r => r.Url.EndsWith("/maps/self-hosted-light.json", StringComparison.Ordinal));
        Assert.Contains(seen, r => r.Url.EndsWith("/maps/self-hosted-dark.json", StringComparison.Ordinal));
        Assert.Contains(answered, s => s.Url.EndsWith("/basemap/basemap.pmtiles", StringComparison.Ordinal) && s.Status == 206);
        Assert.DoesNotContain(answered, s => s.Status == 429);
        Assert.DoesNotContain(answered, s => s.Url.Contains("/basemap/", StringComparison.Ordinal) && s.Status >= 400);

        // The archive's range requests are authorized by nginx's auth_request, so every 206 above already
        // proves the session cookie went with it (one without would be a 401). Playwright reports the header
        // only for some requests (it comes from the network layer's extra info), so presence is asserted on
        // at least one, and absence on every style, glyph and sprite request.
        var basemapCookies = 0;
        foreach (var request in seen)
        {
            var path = new Uri(request.Url).AbsolutePath;
            var cookie = (await request.AllHeadersAsync()).ContainsKey("cookie");
            if (path == "/basemap/basemap.pmtiles" && cookie)
                basemapCookies++;
            else if (path.StartsWith("/maps/", StringComparison.Ordinal))
                Assert.False(cookie, $"{path} went out with a cookie.");
        }

        Assert.True(basemapCookies > 0, "No basemap range request was seen carrying the session cookie.");

        await page.CloseAsync();
    }

    // ── AC 17a ────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Without_a_session_the_file_is_refused()
    {
        SkipUnlessSelfHosted();
        var anonymous = await browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = fixture.BaseUrl, IgnoreHTTPSErrors = true });
        try
        {
            var response = await anonymous.APIRequest.GetAsync("/basemap/basemap.pmtiles", new() { Headers = new Dictionary<string, string> { ["Range"] = "bytes=0-126" } });

            Assert.Equal(401, response.Status);
        }
        finally
        {
            await anonymous.CloseAsync();
        }
    }

    // ── AC 20 ─────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_location_outside_the_extract_still_reaches_ready()
    {
        SkipUnlessSelfHosted();
        var page = await browserContext!.NewPageAsync();

        // OpenAsync returns only once the caption shows, which is the map's ready state (WaitReadyAsync's signal
        // in LocationMapRenderTests), so the absent failure state below is asserted after the load, not before.
        await OpenAsync(page, "Home");

        await Expect(page.Locator(".loc-map-pin")).ToBeVisibleAsync();
        await Expect(page.Locator(".loc-map-state")).ToHaveCountAsync(0);
        await page.CloseAsync();
    }

    private static async Task OpenAsync(IPage page, string location)
    {
        await page.GotoAsync("/locations");
        var trigger = page.Locator(".odc-record-trigger", new PageLocatorOptions { HasText = location }).First;
        await trigger.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        if (await trigger.GetAttributeAsync("aria-expanded") != "true")
            await trigger.ClickAsync();
        await Expect(page.Locator(".loc-map-caption")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    private static async Task ToggleThemeAsync(IPage page)
    {
        var toggle = page.Locator("button[aria-label='Switch to light mode'], button[aria-label='Switch to dark mode']").First;
        var name = await toggle.GetAttributeAsync("aria-label");
        await toggle.ClickAsync();
        await page.WaitForFunctionAsync(
            "label => !document.querySelector(`button[aria-label=\"${label}\"]`)", name, new PageWaitForFunctionOptions { Timeout = 15_000 });
    }
}
