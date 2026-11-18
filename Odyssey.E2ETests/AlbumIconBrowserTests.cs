using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The two album-icon acceptance criteria of issue #406 that only a real browser can prove (issue #413).
/// <list type="bullet">
/// <item>AC 13, forced colours: in <c>forced-colors: active</c> every album glyph renders in the system
/// <c>CanvasText</c> and stays visible — on the album card's meta row and its no-cover area, the open album's
/// header, the overview's "By album" row, the Album filter option and the add-to-album row.</item>
/// <item>AC 15, reflow and text spacing: at 320 CSS px, in both themes, and again with the WCAG 1.4.12
/// spacing overrides, the album form scrolls nothing sideways, clips no caption or help line, and its icon
/// grid scrolls inside its own window.</item>
/// </list>
/// </summary>
/// <remarks>
/// The seeded albums all have a cover and no icon or colour, so the test creates one album with both and no
/// photos (so no cover), and deletes it in a <c>finally</c>. One test, one sign-in: the identity surface is
/// rate-limited by network across this collection (<see cref="E2ESignIn"/>).
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class AlbumIconBrowserTests(StackFixture fixture) : IAsyncLifetime
{
    private const string TextSpacingCss =
        "* { line-height: 1.5 !important; letter-spacing: 0.12em !important; word-spacing: 0.16em !important; }"
        + " p { margin-bottom: 2em !important; }";

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
    public async Task Album_glyphs_survive_forced_colours_and_the_form_reflows_at_320_px()
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

        var name = $"E2E glyph {Guid.NewGuid():N}"[..17];
        var startedDark = await IsDarkAsync(page);
        var created = false;
        try
        {
            await CreateAlbumAsync(page, name);
            created = true;

            await AssertGlyphsUseCanvasTextAsync(page, name);

            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ForcedColors = ForcedColors.None });
            await page.SetViewportSizeAsync(320, 800);
            foreach (var dark in new[] { startedDark, !startedDark })
            {
                await SetThemeAsync(page, dark);
                await AssertFormReflowsAsync(page, dark ? "dark" : "light", textSpacing: false);
                await AssertFormReflowsAsync(page, dark ? "dark" : "light", textSpacing: true);
            }
        }
        finally
        {
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ForcedColors = ForcedColors.None });
            await page.SetViewportSizeAsync(1280, 900);
            await SetThemeAsync(page, startedDark);
            if (created)
            {
                await DeleteAlbumAsync(page, name);
            }
        }
    }

    // ── AC 13: forced colours ──────────────────────────────────────────────────────────────────

    private static async Task AssertGlyphsUseCanvasTextAsync(IPage page, string name)
    {
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ForcedColors = ForcedColors.Active });

        // Album card: the meta row and the no-cover area.
        await GotoAlbumsAsync(page);
        var card = page.Locator(".pl-albcard").Filter(new LocatorFilterOptions { HasText = name });
        await AssertCanvasTextAsync(page, card.Locator(".pl-albname-row .pl-albglyph"), "album card meta row");
        await AssertCanvasTextAsync(page, card.Locator(".pl-albcover-empty .pl-albglyph-xl"), "album card no-cover area");

        // The open album's header.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Open album {name}" }).ClickAsync();
        await AssertCanvasTextAsync(page, page.Locator(".pl-backrow .pl-albglyph"), "open album header");

        // The add-to-album row: select the photos of a seeded album and offer to add them to the new one.
        await GotoAlbumsAsync(page);
        await page.Locator(".pl-albcard-hit").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Select", Exact = true }).ClickAsync();
        await page.Locator(".pl-sellink").ClickAsync();
        await page.Locator(".pl-selact").Filter(new LocatorFilterOptions { HasText = "Add to album" }).ClickAsync();
        var row = page.Locator(".pl-alblist .pl-albrow")
            .Filter(new LocatorFilterOptions { HasText = name });
        await AssertCanvasTextAsync(page, row.Locator(".pl-albrow-glyph .pl-albglyph"), "add-to-album row");
        await page.Keyboard.PressAsync("Escape");

        // The overview's "By album" row and the Album filter option, on the photo library.
        await page.GotoAsync("/photos", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await OpenRegionAsync(page, "Overview");
        var byAlbum = page.Locator(".odc-breakdown").Filter(new LocatorFilterOptions { HasText = "By album" });
        var overviewRow = byAlbum.Locator(".odc-breakdown-row").Filter(new LocatorFilterOptions { HasText = name });
        await AssertCanvasTextAsync(page, overviewRow.Locator(".material-icons").First, "overview By album row");

        await OpenRegionAsync(page, "Search");
        await page.Locator(".odc-ms-trigger").Filter(new LocatorFilterOptions { HasTextRegex = new Regex(@"^\s*Album(?!s)") }).First.ClickAsync();
        var option = page.Locator(".odc-ms-opt").Filter(new LocatorFilterOptions { HasText = name });
        await AssertCanvasTextAsync(page, option.Locator(".material-icons").Last, "Album filter option");
        await page.Keyboard.PressAsync("Escape");
    }

    private static async Task AssertCanvasTextAsync(IPage page, ILocator glyph, string surface)
    {
        await glyph.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        var result = await glyph.EvaluateAsync<string[]>(
            """
            (el) => {
                const probe = document.createElement('span');
                probe.style.color = 'CanvasText';
                document.body.appendChild(probe);
                const canvasText = getComputedStyle(probe).color;
                probe.remove();
                const s = getComputedStyle(el);
                const r = el.getBoundingClientRect();
                return [s.color, canvasText, s.visibility, s.opacity, String(r.width > 0 && r.height > 0)];
            }
            """);

        Assert.True(result[0] == result[1], $"{surface}: glyph is {result[0]}, not CanvasText ({result[1]}).");
        Assert.True(result[2] == "visible" && result[3] != "0" && result[4] == "true",
            $"{surface}: glyph is not visible (visibility {result[2]}, opacity {result[3]}, has box {result[4]}).");
    }

    // ── AC 15: reflow and text spacing ─────────────────────────────────────────────────────────

    private static async Task AssertFormReflowsAsync(IPage page, string theme, bool textSpacing)
    {
        await GotoAlbumsAsync(page);
        if (textSpacing)
        {
            await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = TextSpacingCss });
        }

        await page.Locator(".pl-albnew").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.Locator(".odc-iconpick-grid").WaitForAsync();

        var what = $"Album form at 320 px, {theme}{(textSpacing ? ", text spacing" : string.Empty)}";
        var measured = await page.EvaluateAsync<double[]>(
            """
            () => {
                const grid = document.querySelector('.mud-dialog .odc-iconpick-grid');
                const clipped = [...document.querySelectorAll(
                    '.mud-dialog .odc-field-help, .mud-dialog .odc-iconpick-caption, .mud-dialog .odc-colorpick-caption, .mud-dialog .odc-field-label')]
                    .filter((e) => e.scrollWidth > e.clientWidth + 1 || e.scrollHeight > e.clientHeight + 1).length;
                const dialogRight = document.querySelector('.mud-dialog').getBoundingClientRect().right;
                return [
                    document.documentElement.scrollWidth, window.innerWidth, clipped,
                    grid.scrollHeight, grid.clientHeight, grid.getBoundingClientRect().right, dialogRight,
                    getComputedStyle(grid).overflowY === 'auto' ? 1 : 0,
                ];
            }
            """);

        Assert.True(measured[0] <= measured[1], $"{what}: the page is {measured[0]}px wide in a {measured[1]}px viewport.");
        Assert.True(measured[2] == 0, $"{what}: {measured[2]} caption, help or label line(s) clipped.");
        Assert.True(measured[7] == 1 && measured[3] > measured[4],
            $"{what}: the icon grid does not scroll inside its own window ({measured[3]}px of content in {measured[4]}px).");
        Assert.True(measured[5] <= measured[6] + 1, $"{what}: the icon grid runs past the dialog's edge.");

        await page.Keyboard.PressAsync("Escape");
    }

    // ── Setup and teardown ─────────────────────────────────────────────────────────────────────

    private static async Task CreateAlbumAsync(IPage page, string name)
    {
        await GotoAlbumsAsync(page);
        await page.Locator(".pl-albnew").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByLabel("Album name").FillAsync(name);
        // The first cell of each picker is its default; the second is a real icon and a real colour.
        await dialog.Locator(".odc-iconpick-grid [role=radio]").Nth(1).ClickAsync();
        await dialog.Locator(".odc-colorpick-grid [role=radio]").Nth(1).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create" }).ClickAsync();
        await page.Locator(".pl-albcard").Filter(new LocatorFilterOptions { HasText = name })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
    }

    private static async Task DeleteAlbumAsync(IPage page, string name)
    {
        await GotoAlbumsAsync(page);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Edit {name}" }).ClickAsync();
        await page.Locator(".mud-dialog .pl-danger-link").ClickAsync();
        await page.Locator(".pl-albcard").Filter(new LocatorFilterOptions { HasText = name })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
    }

    /// <summary>Opens a page-header region unless it is already open: the page remembers which regions were open.</summary>
    private static async Task OpenRegionAsync(IPage page, string name)
    {
        var toggle = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = true });
        if (await toggle.GetAttributeAsync("aria-expanded") != "true")
        {
            await toggle.ClickAsync();
        }
    }

    private static async Task GotoAlbumsAsync(IPage page)
    {
        await page.GotoAsync("/albums", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator(".pl-albnew").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    private static async Task<bool> IsDarkAsync(IPage page)
    {
        await page.Locator("button[aria-label='Switch to light mode'], button[aria-label='Switch to dark mode']")
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return await page.Locator("button[aria-label='Switch to light mode']").CountAsync() > 0;
    }

    /// <summary>Flips the rail's theme toggle when needed and waits for the saved preference to follow.</summary>
    private static async Task SetThemeAsync(IPage page, bool dark)
    {
        if (await IsDarkAsync(page) == dark)
        {
            return;
        }

        await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = dark ? "Switch to dark mode" : "Switch to light mode" }).ClickAsync(),
            response => response.Url.Contains("preferences", StringComparison.Ordinal) && response.Request.Method == "PUT");
    }
}
