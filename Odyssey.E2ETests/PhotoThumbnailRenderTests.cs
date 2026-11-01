using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// Issue #365 AC 17 in a real browser: the seeded photos' thumbnails are made by the worker and served as WebP with
/// no fallback request for the original; the converted surfaces keep their box at a phone width and at 200 % zoom in
/// both themes; and an image failing under a focused tile leaves focus where it was.
/// </summary>
/// <remarks>
/// Against the Aspire split-port stack (client 5199, API 5188) this is also the cross-origin case: a
/// <c>crossorigin</c> attribute or a <c>same-origin</c> CORP would make every thumbnail fail silently into the
/// original, which the no-fallback assertion catches.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class PhotoThumbnailRenderTests(StackFixture fixture) : IAsyncLifetime
{
    // One signed-in session for the class: the sign-in surface is rate-limited by network (see E2ESignIn).
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static IPlaywright? playwright;
    private static IBrowser? browser;
    private static IBrowserContext? browserContext;
    private static IPage? signedInPage;

    /// <summary>The worker renders the seed in the background; this is how long the first visit waits for it.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(180);

    private const string ThumbnailTile = "img.pl-tile-img[src*='/thumbnail?size=Medium']";

    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await SessionGate.WaitAsync();
        try
        {
            if (signedInPage is not null)
            {
                return;
            }

            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            browserContext = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = fixture.BaseUrl, IgnoreHTTPSErrors = true });
            var page = await browserContext.NewPageAsync();
            await page.GotoAsync("/login", new PageGotoOptions { Timeout = 120_000 });
            var admin = DemoUsers.All.First(user => user.Role == "Admin");
            await E2ESignIn.SignInAsync(page, admin.Email, admin.Password);
            signedInPage = page;
        }
        finally
        {
            SessionGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Reloads the library until a tile shows its thumbnail: readiness is picked up on the next list load.</summary>
    private static async Task WaitForThumbnailsAsync(IPage page)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            await page.GotoAsync("/photos", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            if (await page.Locator(ThumbnailTile).CountAsync() > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"No library tile reached ThumbnailsReady within {ReadyTimeout.TotalSeconds:0} s. Is the worker running?");
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    [SkippableFact]
    public async Task Ready_tiles_load_webp_thumbnails_and_never_fall_back_to_the_original()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = signedInPage!;
        await WaitForThumbnailsAsync(page);

        var thumbnails = new List<IResponse>();
        var originals = new List<string>();
        void OnResponse(object? _, IResponse response)
        {
            if (response.Url.Contains("/thumbnail?size=", StringComparison.Ordinal))
            {
                thumbnails.Add(response);
            }
            else if (response.Url.Contains("/api/files/", StringComparison.Ordinal) && response.Url.EndsWith("/content", StringComparison.Ordinal))
            {
                originals.Add(response.Url);
            }
        }

        page.Response += OnResponse;
        try
        {
            await page.GotoAsync("/photos", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var first = page.Locator(ThumbnailTile).First;
            await Expect(first).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("img => img.complete && img.naturalWidth > 0", await first.ElementHandleAsync(),
                new PageWaitForFunctionOptions { Timeout = 20_000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
        finally
        {
            page.Response -= OnResponse;
        }

        Assert.NotEmpty(thumbnails);
        Assert.All(thumbnails, response =>
        {
            Assert.True(response.Status is 200 or 304, $"{response.Url} answered {response.Status}.");
            if (response.Status == 200)
            {
                Assert.Equal("image/webp", response.Headers["content-type"]);
            }
        });

        // Every tile showing a thumbnail kept it: none fell back to its original.
        var shownThumbnails = await page.Locator(ThumbnailTile).CountAsync();
        var failed = await page.Locator("img.pl-tile-img.img-failed").CountAsync();
        Assert.Equal(0, failed);
        Assert.True(shownThumbnails > 0);
        var tileOriginals = await page.Locator("img.pl-tile-img[src*='/api/files/']").CountAsync();
        Assert.True(originals.Count <= tileOriginals,
            $"{originals.Count} originals requested, but only {tileOriginals} tiles show an original: a thumbnail fell back.");
    }

    public static TheoryData<int, bool> Viewports() => new()
    {
        { 320, false }, { 320, true }, { 640, false }, { 640, true },
    };

    /// <summary>320 CSS px is the reflow width; 640 px stands in for 200 % zoom of a 1280 px window.</summary>
    [SkippableTheory]
    [MemberData(nameof(Viewports))]
    public async Task The_library_tiles_and_album_covers_keep_their_box_at_phone_width_and_zoom(int width, bool dark)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = signedInPage!;
        await WaitForThumbnailsAsync(page);
        await page.SetViewportSizeAsync(width, 900);
        try
        {
            foreach (var (route, box) in new[] { ("/photos", ".pl-tile"), ("/albums", ".pl-albcover-cell") })
            {
                await page.GotoAsync(route, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                if (dark)
                {
                    // Theme is styling only; set it locally rather than saving the shared user's preference.
                    await page.EvaluateAsync("() => document.documentElement.dataset.theme = 'dark'");
                }

                var boxes = page.Locator(box);
                if (await boxes.CountAsync() == 0)
                {
                    continue;
                }

                var first = await boxes.First.BoundingBoxAsync();
                Assert.NotNull(first);
                Assert.True(first!.Width > 0 && first.Height > 0, $"{route} {box} collapsed at {width}px.");
                var scroll = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
                Assert.True(scroll <= width, $"{route} overflows a {width}px viewport ({scroll}px).");
            }
        }
        finally
        {
            await page.SetViewportSizeAsync(1280, 900);
            await page.EvaluateAsync("() => delete document.documentElement.dataset.theme");
        }
    }

    [SkippableFact]
    public async Task An_image_failing_under_a_focused_tile_leaves_focus_on_its_button()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var page = signedInPage!;
        await WaitForThumbnailsAsync(page);
        await page.GotoAsync("/photos", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var tile = page.Locator(".pl-tile", new PageLocatorOptions { Has = page.Locator(ThumbnailTile) }).First;
        var hit = tile.Locator("button.pl-tile-hit");
        await hit.FocusAsync();
        var label = await hit.GetAttributeAsync("aria-label");

        // Force the image to error, as a 404 or a dropped connection would; Blazor's @onerror moves the stage on.
        await tile.Locator("img").EvaluateAsync("img => img.dispatchEvent(new Event('error'))");
        await page.WaitForTimeoutAsync(300);

        Assert.True(await hit.EvaluateAsync<bool>("button => document.activeElement === button"), "Focus left the tile's button.");
        Assert.Equal(label, await hit.GetAttributeAsync("aria-label"));
    }
}
