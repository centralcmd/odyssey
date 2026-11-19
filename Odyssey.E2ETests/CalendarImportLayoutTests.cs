using System.Text;
using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The calendar import's four-tile result reflows (issue #517 §6.8, AC 18): in both themes, at most two columns in
/// the 560 px dialog at 100 % text, and at 200 % text and a 320 px viewport no tile clips and nothing scrolls
/// sideways. Layout is the one thing bUnit cannot see.
/// </summary>
/// <remarks>
/// It drives the real import: a one-event file, then the same UID marked cancelled with the removal option ticked, so
/// the second result carries the removed tile — and the event it created is gone again afterwards. One test, one
/// sign-in (the identity surface is rate-limited by network across this collection); the theme it flips is flipped
/// back in a <c>finally</c>.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class CalendarImportLayoutTests(StackFixture fixture) : IAsyncLifetime
{
    private const string Uid = "odyssey-e2e-517-layout@example.test";

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

    private static string Ics(bool cancelled) =>
        string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Odyssey E2E//517//EN",
            "BEGIN:VEVENT", $"UID:{Uid}", "DTSTAMP:20261101T090000Z", "DTSTART:20261120T090000Z", "DTEND:20261120T100000Z",
            "SUMMARY:Layout check (issue 517)", cancelled ? "STATUS:CANCELLED" : "STATUS:CONFIRMED",
            "END:VEVENT", "END:VCALENDAR", string.Empty);

    private static async Task ImportAsync(IPage page, bool cancelled)
    {
        await page.Locator("#cal-import-upload input[type=file]").First.SetInputFilesAsync(new FilePayload
        {
            Name = cancelled ? "cancelled.ics" : "layout.ics",
            MimeType = "text/calendar",
            Buffer = Encoding.UTF8.GetBytes(Ics(cancelled)),
        });
        if (cancelled)
        {
            await page.Locator("label[for=cal-imp-allow-removals]").ClickAsync();
        }

        await page.Locator("#cal-imp-import").ClickAsync();
        await page.Locator(".cal-imp-result").WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
    }

    /// <summary>Columns of the four-tile grid, and every overflow the reflow rules forbid.</summary>
    private static async Task<(int Columns, string[] Overflows)> MeasureAsync(IPage page)
    {
        var measured = await page.EvaluateAsync<Measurement>(
            """
            () => {
                const grid = document.querySelector('.cal-imp-stats--four');
                if (!grid) return { columns: 0, overflows: ['no four-tile grid'] };
                const columns = getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length;
                const overflows = [];
                for (const tile of grid.querySelectorAll('.cal-imp-stat')) {
                    if (tile.scrollWidth > tile.clientWidth + 1) overflows.push('tile ' + tile.className + ' clips');
                    const r = tile.getBoundingClientRect(), g = grid.getBoundingClientRect();
                    if (r.right > g.right + 1) overflows.push('tile ' + tile.className + ' leaves the grid');
                }
                const dialog = grid.closest('.mud-dialog');
                if (dialog && dialog.scrollWidth > dialog.clientWidth + 1) overflows.push('the dialog scrolls sideways');
                if (document.documentElement.scrollWidth > window.innerWidth + 1) overflows.push('the page scrolls sideways');
                return { columns, overflows };
            }
            """);
        return (measured.Columns, measured.Overflows);
    }

    public sealed class Measurement
    {
        public int Columns { get; set; }

        public string[] Overflows { get; set; } = [];
    }

    private static async Task SetTextScaleAsync(IPage page, string scale) =>
        await page.EvaluateAsync("s => { document.documentElement.style.fontSize = s; }", scale);

    private static async Task<bool> IsDarkAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.dataset.theme === 'dark'");

    private static Task ToggleThemeAsync(IPage page, bool fromDark) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = fromDark ? "Switch to light mode" : "Switch to dark mode" }).ClickAsync();

    [SkippableFact] // AC 18
    public async Task The_four_tile_result_fits_two_columns_and_reflows_in_both_themes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
        var page = await context.NewPageAsync();
        var owner = DemoUsers.All.First(candidate => candidate.Role == "Owner");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, owner.Email, owner.Password);

        await page.GotoAsync("/calendar");
        var startedDark = await IsDarkAsync(page);
        var flipped = false;
        try
        {
            foreach (var pass in new[] { "first theme", "second theme" })
            {
                // The rail's theme toggle sits behind the dialog's scrim, so each theme gets its own pair of imports.
                await page.SetViewportSizeAsync(1366, 900);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "More actions" }).ClickAsync();
                await page.GetByText("Import from file…").ClickAsync();
                await ImportAsync(page, cancelled: false);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Import another file" }).ClickAsync();
                await ImportAsync(page, cancelled: true);

                await SetTextScaleAsync(page, "100%");
                var (columns, overflows) = await MeasureAsync(page);
                Assert.True(columns is >= 1 and <= 2, $"{pass}: the four tiles use {columns} columns in the dialog; at most 2.");
                Assert.True(overflows.Length == 0, $"{pass}, 100%: " + string.Join("; ", overflows));

                await SetTextScaleAsync(page, "200%");
                (_, overflows) = await MeasureAsync(page);
                Assert.True(overflows.Length == 0, $"{pass}, 200% text: " + string.Join("; ", overflows));

                await SetTextScaleAsync(page, "100%");
                await page.SetViewportSizeAsync(320, 800);
                (_, overflows) = await MeasureAsync(page);
                Assert.True(overflows.Length == 0, $"{pass}, 320 px: " + string.Join("; ", overflows));

                await page.SetViewportSizeAsync(1366, 900);
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Done" }).ClickAsync();
                await page.Locator(".cal-imp-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
                if (!flipped)
                {
                    await ToggleThemeAsync(page, startedDark);
                    flipped = true;
                    await page.WaitForFunctionAsync(
                        $"() => (document.documentElement.dataset.theme === 'dark') === {(!startedDark).ToString().ToLowerInvariant()}");
                }
            }
        }
        finally
        {
            if (flipped)
            {
                await page.Keyboard.PressAsync("Escape");
                await ToggleThemeAsync(page, !startedDark);
            }
        }
    }
}
