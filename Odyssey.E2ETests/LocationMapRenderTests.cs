using System.Text.Json;
using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// Issue #371 in a real browser: the location map on the expanded <c>/locations</c> record.
/// </summary>
/// <remarks>
/// <para>
/// The tile host is never reached (AC 25): every request to it is intercepted and answered with a minimal
/// local style (a background layer and one vector source) and empty tiles, so this tier never depends on a
/// third-party service. The routes match the shipped default host; a stack configured for another one is
/// out of this tier's scope.
/// </para>
/// <para>
/// Chromium runs headless with SwiftShader, so WebGL is software-rendered and the map really draws. Service
/// workers are blocked so the routes apply on a production build too (see the context options).
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class LocationMapRenderTests(StackFixture fixture) : IAsyncLifetime
{
    private const string TileHost = "https://tiles.openfreemap.org/**";
    private const string LocationName = "Cabin";

    // One signed-in session for the class: the sign-in surface is rate-limited by network (see E2ESignIn).
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static IPlaywright? playwright;
    private static IBrowser? browser;
    private static IBrowserContext? browserContext;

    private static readonly object TestStyle = new
    {
        version = 8,
        sources = new Dictionary<string, object>
        {
            ["t"] = new { type = "vector", tiles = new[] { "https://tiles.openfreemap.org/e2e/{z}/{x}/{y}.pbf" }, maxzoom = 14 },
        },
        layers = new object[]
        {
            new { id = "bg", type = "background", paint = new Dictionary<string, object> { ["background-color"] = "#d8e2dc" } },
            new { id = "water", type = "fill", source = "t", sourceLayer = "water", paint = new Dictionary<string, object> { ["fill-color"] = "#9ec5e8" } },
        },
    };

    private static readonly string[] ControlNames =
        ["Pan north", "Pan west", "Re-centre on pin", "Pan east", "Pan south", "Zoom in", "Zoom out"];

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await SessionGate.WaitAsync();
        try
        {
            if (browserContext is not null)
            {
                return;
            }

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
                // A production build's service worker re-issues every fetch itself, and Playwright cannot
                // route a request a service worker makes — so with it active the tile routes below would be
                // bypassed and the map would reach the real tile host. Blocking it keeps AC 25's promise.
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });
            var page = await browserContext.NewPageAsync();
            await page.GotoAsync("/login", new PageGotoOptions { Timeout = 120_000 });
            var admin = DemoUsers.All.First(user => user.Role == "Admin");
            await E2ESignIn.SignInAsync(page, admin.Email, admin.Password);
            await page.CloseAsync();
        }
        finally
        {
            SessionGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── AC 15, 1, 7, 23 ─────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Maplibre_loads_on_the_first_map_only_and_the_map_draws_with_its_caption()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await NewPageAsync();
        var requested = new List<string>();
        page.Request += (_, r) => requested.Add(r.Url);

        await page.GotoAsync("/locations");
        var trigger = Trigger(page);
        await trigger.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        await CollapseAsync(page);

        Assert.DoesNotContain(requested, IsMapAsset);

        await ExpandAsync(page);
        await WaitReadyAsync(page);

        Assert.Contains(requested, u => u.Contains("/lib/maplibre-gl/", StringComparison.Ordinal));
        Assert.DoesNotContain(requested, u => u.Contains("unpkg", StringComparison.Ordinal) || u.Contains("jsdelivr", StringComparison.Ordinal));

        var surface = page.Locator(".loc-map canvas");
        await Expect(surface).ToHaveAttributeAsync("role", "group");
        await Expect(surface).ToHaveAttributeAsync("aria-roledescription", "map");
        await Expect(surface).ToHaveAttributeAsync("aria-label", LocationName);
        await Expect(page.Locator(".loc-map-pin")).ToHaveAttributeAsync("aria-label", $"{LocationName} pin");

        var link = page.Locator(".loc-map-caption a");
        await Expect(link).ToBeVisibleAsync();
        Assert.Contains("noopener", await link.GetAttributeAsync("rel"));
        Assert.Contains("noreferrer", await link.GetAttributeAsync("rel"));
        Assert.Equal("_blank", await link.GetAttributeAsync("target"));
        await Expect(page.Locator(".loc-map-caption")).ToContainTextAsync("OpenStreetMap");
        await page.CloseAsync();
    }

    // ── AC 3 ──────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Keyboard_reaches_every_part_in_order_and_the_popup_returns_focus_to_the_pin()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();

        await page.Locator(".loc-map canvas").FocusAsync();
        var order = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            order.Add(await FocusedNameAsync(page));
            await page.Keyboard.PressAsync("Tab");
        }

        Assert.Equal([LocationName, $"{LocationName} pin", .. ControlNames], order.Take(9));
        Assert.Contains("OpenStreetMap", order[9], StringComparison.Ordinal);

        var pin = page.Locator(".loc-map-pin");
        await pin.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Expect(pin).ToHaveAttributeAsync("aria-expanded", "true");
        Assert.Equal("Close", await FocusedNameAsync(page));
        await Expect(page.Locator(".loc-map-popup[role=dialog] h3")).ToHaveTextAsync(LocationName);

        await page.Keyboard.PressAsync("Escape");
        await Expect(pin).ToHaveAttributeAsync("aria-expanded", "false");
        Assert.Equal($"{LocationName} pin", await FocusedNameAsync(page));
        await page.CloseAsync();
    }

    // ── AC 1, 22 ─────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_controls_move_the_map_and_zoom_in_stops_at_16()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();
        var pin = page.Locator(".loc-map-pin");

        var before = await pin.BoundingBoxAsync();
        await Control(page, "Pan north").ClickAsync();
        var afterNorth = await pin.BoundingBoxAsync();
        Assert.True(afterNorth!.Y > before!.Y + 20, "Pan north should move the pin down the frame.");

        await Control(page, "Re-centre on pin").ClickAsync();
        await Expect(pin).ToHaveCountAsync(1);
        var centred = await pin.BoundingBoxAsync();
        Assert.InRange(centred!.Y, before.Y - 2, before.Y + 2);

        var zoomIn = Control(page, "Zoom in");
        await zoomIn.ClickAsync();
        await zoomIn.ClickAsync();
        await Expect(zoomIn).ToHaveAttributeAsync("aria-disabled", "true");
        // aria-disabled, not disabled: it keeps focus, and activating it does nothing. (Playwright will not
        // click an aria-disabled control, which is why this presses Enter on it instead.)
        await zoomIn.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        Assert.Equal("Zoom in", await FocusedNameAsync(page));
        await Expect(zoomIn).ToHaveAttributeAsync("aria-disabled", "true");
        await Expect(Control(page, "Zoom out")).Not.ToHaveAttributeAsync("aria-disabled", "true");

        await AssertTargetsAsync(page);
        await page.CloseAsync();
    }

    [SkippableFact]
    public async Task At_320_px_every_target_is_at_least_24_px_inside_the_frame_and_the_caption_shows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync(width: 320);

        await AssertTargetsAsync(page);
        await Expect(page.Locator(".loc-map-caption a")).ToBeVisibleAsync();
        await page.CloseAsync();
    }

    // ── AC 2 ──────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_plain_wheel_over_the_map_shows_the_apps_gesture_hint()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();
        var frame = page.Locator(".loc-map-frame");
        await frame.ScrollIntoViewIfNeededAsync();
        var box = await frame.BoundingBoxAsync();

        // A quarter in from the right, clear of the pin at the centre and the controls along the top.
        await page.Mouse.MoveAsync(box!.X + box.Width * 0.75f, box.Y + box.Height * 0.75f);
        await page.Mouse.WheelAsync(0, 200);

        // MapLibre flags the hint for 100 ms and the stylesheet holds it for a second before fading, so
        // the check is the opacity a person would see, not the transient class.
        await page.WaitForFunctionAsync(
            "() => { const h = document.querySelector('.loc-map .maplibregl-cooperative-gesture-screen'); return h && Number(getComputedStyle(h).opacity) > 0.9; }",
            null, new() { Timeout = 5_000 });
        await Expect(page.Locator(".loc-map .maplibregl-cooperative-gesture-screen")).ToContainTextAsync("scroll to zoom the map");
        await page.CloseAsync();
    }

    // ── AC 6 (S1) ─────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_hostile_style_attribution_never_renders()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var dialogs = 0;
        var page = await NewPageAsync(styleAttribution: "<img src=x onerror=\"window.__pwned=1\">");
        page.Dialog += (_, d) => { dialogs++; _ = d.DismissAsync(); };

        await GoToLocationsAsync(page);
        await ExpandAsync(page);
        await WaitReadyAsync(page);

        Assert.Equal(0, await page.Locator(".loc-map img").CountAsync());
        Assert.Null(await page.EvaluateAsync<int?>("() => window.__pwned"));
        Assert.Equal(0, dialogs);
        await Expect(page.Locator(".loc-map-caption")).ToContainTextAsync("OpenStreetMap");
        await page.CloseAsync();
    }

    // ── AC 12, 23 ────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_blocked_tile_host_fails_in_place_and_try_again_recovers_and_announces()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var blocked = true;
        var page = await NewPageAsync(isBlocked: () => blocked);

        await GoToLocationsAsync(page);
        await ExpandAsync(page);

        var state = page.Locator(".loc-map-state");
        await Expect(state).ToContainTextAsync($"The map for {LocationName} couldn't load.", new() { Timeout = 30_000 });
        await Expect(page.Locator(".loc-map-caption")).ToHaveCountAsync(0);

        blocked = false;
        await state.GetByRole(AriaRole.Button, new() { Name = "Try again" }).ClickAsync();
        await WaitReadyAsync(page);

        await Expect(page.Locator("[role=status][aria-live=polite]").Filter(new() { HasText = $"Map of {LocationName} loaded." }))
            .ToHaveCountAsync(1, new() { Timeout = 15_000 });
        Assert.Equal(LocationName, await FocusedNameAsync(page));
        await page.CloseAsync();
    }

    // ── AC 10 ─────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Collapsing_the_card_removes_the_map()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();

        await CollapseAsync(page);

        await Expect(page.Locator(".maplibregl-map")).ToHaveCountAsync(0);
        await page.CloseAsync();
    }

    // ── AC 4 (S2): a hostile location name is text everywhere the map shows it ─────────────────────

    [SkippableFact]
    public async Task A_markup_location_name_is_shown_literally_and_runs_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const string hostile = "<img src=x onerror=\"window.__pwned=1\">Cabin";
        var dialogs = 0;
        var page = await NewPageAsync(renameCabinTo: hostile);
        page.Dialog += (_, d) => { dialogs++; _ = d.DismissAsync(); };

        await GoToLocationsAsync(page);
        await ExpandAsync(page);
        await WaitReadyAsync(page);

        await Expect(page.Locator(".loc-map canvas")).ToHaveAttributeAsync("aria-label", hostile);
        await Expect(page.Locator(".loc-map-pin")).ToHaveAttributeAsync("aria-label", $"{hostile} pin");
        await page.Locator(".loc-map-pin").ClickAsync();
        Assert.Equal(hostile, await page.Locator(".loc-map-popup h3").TextContentAsync());

        Assert.Equal(0, await page.Locator(".loc-map img").CountAsync());
        Assert.Null(await page.EvaluateAsync<int?>("() => window.__pwned"));
        Assert.Equal(0, dialogs);
        await page.CloseAsync();
    }

    // ── AC 1: every input moves or zooms, from zoom 14, never past 16 ───────────────────────────

    [SkippableFact]
    public async Task Every_pan_and_zoom_input_moves_the_map()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();
        var host = page.Locator(".loc-map-host");
        var pin = page.Locator(".loc-map-pin");
        await page.Locator(".loc-map-frame").ScrollIntoViewIfNeededAsync();
        await Expect(host).ToHaveAttributeAsync("data-zoom", "14.00");

        async Task<(float X, float Y)> PinAsync()
        {
            var b = (await pin.BoundingBoxAsync())!;
            return (b.X, b.Y);
        }

        async Task AssertMovesAsync(Func<Task> act, string what, int dx, int dy)
        {
            await Control(page, "Re-centre on pin").ClickAsync();
            var before = await PinAsync();
            await act();
            await page.WaitForTimeoutAsync(400);
            var after = await PinAsync();
            Assert.True(
                Math.Sign(Math.Round(after.X - before.X)) == dx && Math.Sign(Math.Round(after.Y - before.Y)) == dy,
                $"{what}: the pin moved from {before} to {after}.");
        }

        await AssertMovesAsync(() => Control(page, "Pan north").ClickAsync(), "Pan north", 0, 1);
        await AssertMovesAsync(() => Control(page, "Pan south").ClickAsync(), "Pan south", 0, -1);
        await AssertMovesAsync(() => Control(page, "Pan west").ClickAsync(), "Pan west", 1, 0);
        await AssertMovesAsync(() => Control(page, "Pan east").ClickAsync(), "Pan east", -1, 0);

        var canvas = page.Locator(".loc-map canvas");
        await AssertMovesAsync(async () => { await canvas.FocusAsync(); await page.Keyboard.PressAsync("ArrowUp"); }, "ArrowUp", 0, 1);
        await AssertMovesAsync(async () => { await canvas.FocusAsync(); await page.Keyboard.PressAsync("ArrowLeft"); }, "ArrowLeft", 1, 0);

        var frame = (await page.Locator(".loc-map-frame").BoundingBoxAsync())!;
        await AssertMovesAsync(async () =>
        {
            await page.Mouse.MoveAsync(frame.X + frame.Width * 0.7f, frame.Y + frame.Height * 0.7f);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(frame.X + frame.Width * 0.7f - 80, frame.Y + frame.Height * 0.7f - 40, new() { Steps = 8 });
            await page.Mouse.UpAsync();
        }, "Drag", -1, -1);

        await Control(page, "Re-centre on pin").ClickAsync();
        await canvas.FocusAsync();
        await page.Keyboard.PressAsync("Equal");
        await Expect(host).ToHaveAttributeAsync("data-zoom", "15.00");
        await page.Keyboard.PressAsync("Minus");
        await Expect(host).ToHaveAttributeAsync("data-zoom", "14.00");

        await Control(page, "Zoom out").ClickAsync();
        await Expect(host).ToHaveAttributeAsync("data-zoom", "13.00");

        await page.Mouse.MoveAsync(frame.X + frame.Width * 0.7f, frame.Y + frame.Height * 0.7f);
        await page.Keyboard.DownAsync("Control");
        for (var i = 0; i < 12; i++)
            await page.Mouse.WheelAsync(0, -300);
        await page.Keyboard.UpAsync("Control");
        await page.WaitForTimeoutAsync(800);
        var zoomed = double.Parse((await host.GetAttributeAsync("data-zoom"))!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(zoomed > 13.0, $"Ctrl + wheel should zoom in, zoom is {zoomed}.");
        Assert.True(zoomed <= 16.0, $"Zoom passed the cap: {zoomed}.");
        await page.CloseAsync();
    }

    // ── AC 12, 13 and the context window: the thresholds in location-map.js, in a real browser ────

    [SkippableFact]
    public async Task Tile_errors_before_ready_fail_the_map_in_place()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await NewPageAsync(tilesFail: () => true);

        await GoToLocationsAsync(page);
        await ExpandAsync(page);

        await Expect(page.Locator(".loc-map-state")).ToContainTextAsync($"The map for {LocationName} couldn't load.", new() { Timeout = 30_000 });
        await Expect(page.Locator(".maplibregl-map")).ToHaveCountAsync(0);
        await page.CloseAsync();
    }

    [SkippableFact]
    public async Task Tile_errors_after_ready_degrade_once_and_clear_after_30_seconds()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var failing = false;
        var page = await NewPageAsync(tilesFail: () => failing);
        await GoToLocationsAsync(page);
        await ExpandAsync(page);
        await WaitReadyAsync(page);

        failing = true;
        var alert = page.Locator(".loc-map-alert");
        for (var i = 0; i < 6 && await alert.CountAsync() == 0; i++)
        {
            await Control(page, i % 2 == 0 ? "Zoom out" : "Pan east").ClickAsync();
            await page.WaitForTimeoutAsync(500);
        }

        await Expect(alert).ToContainTextAsync("Some map tiles didn't load.", new() { Timeout = 10_000 });
        Assert.Null(await alert.GetAttributeAsync("role"));
        await Expect(page.Locator(".loc-map canvas")).ToHaveCountAsync(1);
        await Expect(page.Locator("[role=status][aria-live=polite]").Filter(new() { HasText = $"Map of {LocationName}: some tiles didn't load." }))
            .ToHaveCountAsync(1);

        failing = false;
        await Expect(alert).ToHaveCountAsync(0, new() { Timeout = 40_000 });
        await Expect(page.Locator(".loc-map-caption")).ToBeVisibleAsync();
        await page.CloseAsync();
    }

    [SkippableFact]
    public async Task No_load_within_15_seconds_is_a_timeout_failure()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await NewPageAsync(styleDelay: TimeSpan.FromSeconds(20));

        await GoToLocationsAsync(page);
        await ExpandAsync(page);

        await Expect(page.Locator(".loc-map-state")).ToContainTextAsync($"The map for {LocationName} couldn't load.", new() { Timeout = 19_000 });
        await page.CloseAsync();
    }

    [SkippableFact]
    public async Task A_lost_context_not_restored_within_3_seconds_shows_webgl_unavailable()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();

        await LoseContextAsync(page);

        await Expect(page.Locator(".loc-map-state")).ToContainTextAsync("This browser can't draw maps", new() { Timeout = 10_000 });
        await Expect(page.Locator(".maplibregl-map")).ToHaveCountAsync(0);
        await page.CloseAsync();
    }

    [SkippableFact]
    public async Task A_context_restored_in_time_recreates_the_map_and_keeps_focus_on_it()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await OpenMapAsync();
        await page.Locator(".loc-map canvas").FocusAsync();

        await LoseContextAsync(page);
        await page.WaitForTimeoutAsync(500);
        await page.EvaluateAsync("() => window.__locMapLoseContext.restoreContext()");

        await WaitReadyAsync(page);
        await Expect(page.Locator(".loc-map canvas")).ToHaveCountAsync(1);
        await Expect(page.Locator(".loc-map-state")).ToHaveCountAsync(0);
        await page.WaitForFunctionAsync("() => document.activeElement?.tagName === 'CANVAS'", null, new() { Timeout = 15_000 });
        await page.CloseAsync();
    }

    [SkippableFact]
    public async Task Try_again_that_fails_again_moves_focus_to_the_state_box()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = await NewPageAsync(isBlocked: () => true);
        await GoToLocationsAsync(page);
        await ExpandAsync(page);
        var state = page.Locator(".loc-map-state");
        await Expect(state).ToContainTextAsync($"The map for {LocationName} couldn't load.", new() { Timeout = 30_000 });

        await state.GetByRole(AriaRole.Button, new() { Name = "Try again" }).ClickAsync();

        await Expect(state).ToContainTextAsync($"The map for {LocationName} couldn't load.", new() { Timeout = 30_000 });
        await page.WaitForFunctionAsync("() => document.activeElement?.classList.contains('loc-map-state')", null, new() { Timeout = 10_000 });
        await page.CloseAsync();
    }

    private static Task LoseContextAsync(IPage page) => page.EvaluateAsync(@"() => {
        const canvas = document.querySelector('.loc-map canvas');
        const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
        window.__locMapLoseContext = gl.getExtension('WEBGL_lose_context');
        window.__locMapLoseContext.loseContext();
    }");

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task<IPage> OpenMapAsync(int width = 1280)
    {
        var page = await NewPageAsync();
        await page.SetViewportSizeAsync(width, 900);
        await GoToLocationsAsync(page);
        await ExpandAsync(page);
        await WaitReadyAsync(page);
        return page;
    }

    private static async Task<IPage> NewPageAsync(
        Func<bool>? isBlocked = null,
        string? styleAttribution = null,
        Func<bool>? tilesFail = null,
        TimeSpan? styleDelay = null,
        string? renameCabinTo = null)
    {
        var page = await browserContext!.NewPageAsync();
        var style = styleAttribution is null
            ? JsonSerializer.Serialize(TestStyle)
            : JsonSerializer.Serialize(TestStyle).Replace(
                "\"type\":\"vector\"", $"\"type\":\"vector\",\"attribution\":{JsonSerializer.Serialize(styleAttribution)}",
                StringComparison.Ordinal);
        style = style.Replace("\"sourceLayer\"", "\"source-layer\"", StringComparison.Ordinal);

        await page.RouteAsync(TileHost, async route =>
        {
            if (isBlocked?.Invoke() == true)
            {
                await route.AbortAsync();
                return;
            }

            var cors = new Dictionary<string, string> { ["access-control-allow-origin"] = "*" };
            if (route.Request.Url.Contains("/styles/", StringComparison.Ordinal))
            {
                if (styleDelay is { } delay)
                    await Task.Delay(delay);
                await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = style, Headers = cors });
                return;
            }

            if (tilesFail?.Invoke() == true)
            {
                await route.FulfillAsync(new() { Status = 500, Body = "tile error", Headers = cors });
                return;
            }

            // An empty vector tile: valid, with no features.
            await route.FulfillAsync(new() { Status = 200, ContentType = "application/x-protobuf", BodyBytes = [], Headers = cors });
        });

        if (renameCabinTo is not null)
        {
            // The list the page renders, with the seeded Cabin renamed in flight — no stored row is touched.
            await page.RouteAsync(new System.Text.RegularExpressions.Regex(@"/api/locations(\?|$)"), async route =>
            {
                var response = await route.FetchAsync();
                var body = (await response.TextAsync()).Replace(
                    $"\"name\":\"{LocationName}\"", $"\"name\":{JsonSerializer.Serialize(renameCabinTo)}", StringComparison.Ordinal);
                await route.FulfillAsync(new() { Response = response, Body = body });
            });
        }

        return page;
    }

    private static async Task GoToLocationsAsync(IPage page)
    {
        await page.GotoAsync("/locations");
        await Trigger(page).WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
    }

    private static ILocator Trigger(IPage page) =>
        page.Locator(".odc-record-trigger", new PageLocatorOptions { HasText = LocationName }).First;

    private static async Task ExpandAsync(IPage page)
    {
        var trigger = Trigger(page);
        if (await trigger.GetAttributeAsync("aria-expanded") != "true")
            await trigger.ClickAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");
    }

    private static async Task CollapseAsync(IPage page)
    {
        var trigger = Trigger(page);
        if (await trigger.GetAttributeAsync("aria-expanded") == "true")
            await trigger.ClickAsync();
        await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "false");
    }

    private static async Task WaitReadyAsync(IPage page) =>
        await Expect(page.Locator(".loc-map-caption")).ToBeVisibleAsync(new() { Timeout = 30_000 });

    private static ILocator Control(IPage page, string name) =>
        page.Locator(".loc-map").GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static Task<string> FocusedNameAsync(IPage page) =>
        page.EvaluateAsync<string>("() => { const a = document.activeElement; return (a.getAttribute('aria-label') || a.textContent || '').trim(); }");

    private static bool IsMapAsset(string url) =>
        url.Contains("/lib/maplibre-gl/", StringComparison.Ordinal)
        || url.Contains("/js/location-map", StringComparison.Ordinal)
        || url.Contains("/css/location-map", StringComparison.Ordinal);

    /// <summary>The pin, the seven controls and Close are each at least 24 × 24 CSS px, inside the frame (AC 22).</summary>
    private static async Task AssertTargetsAsync(IPage page)
    {
        await page.Locator(".loc-map-pin").ClickAsync();
        await Expect(page.Locator(".loc-map-popup-close")).ToBeVisibleAsync();

        // One snapshot, after the click has scrolled the page, so every box shares one coordinate frame.
        var rects = await page.EvaluateAsync<Dictionary<string, double[]>>(@"names => {
            const map = document.querySelector('.loc-map');
            const box = el => { const r = el.getBoundingClientRect(); return [r.x, r.y, r.width, r.height]; };
            const out = { frame: box(map.querySelector('.loc-map-frame')), pin: box(map.querySelector('.loc-map-pin')),
                          Close: box(map.querySelector('.loc-map-popup-close')) };
            for (const n of names) out[n] = box([...map.querySelectorAll('button')].find(b => b.getAttribute('aria-label') === n));
            return out;
        }", ControlNames);

        var frame = rects["frame"];
        foreach (var (name, b) in rects.Where(r => r.Key != "frame"))
        {
            Assert.True(b[2] >= 24 && b[3] >= 24, $"{name} is {b[2]}×{b[3]}, under 24×24.");
            Assert.True(
                b[0] >= frame[0] - 0.5 && b[1] >= frame[1] - 0.5
                && b[0] + b[2] <= frame[0] + frame[2] + 0.5 && b[1] + b[3] <= frame[1] + frame[3] + 0.5,
                $"{name} [{string.Join(", ", b)}] lies outside the frame [{string.Join(", ", frame)}].");
        }

        var pad = rects["Pan east"];
        var zoom = rects["Zoom in"];
        Assert.True(pad[0] + pad[2] < zoom[0], "The pan pad and the zoom column overlap.");
        await page.Keyboard.PressAsync("Escape");
    }
}
