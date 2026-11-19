using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The event dialog's #520 surfaces reflow (issue #520 §6.7, AC 16): in both themes, at 200 % text and at a 320 px
/// viewport, the time-zone field, the two-zone line and the series confirm step neither clip nor scroll sideways.
/// Layout is the one thing bUnit cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The browser runs in America/New_York, so a new event defaults to that zone, and picking Europe/Oslo brings up the
/// two-zone line.
/// </para>
/// <para>
/// The confirm step needs a keyed series carrying a future override (PR #528 tester review): the test builds one through
/// the page — a daily series created from the New event dialog, one future occurrence edited on its own — then opens
/// "Edit series…" and shortens the series to one occurrence, which would discard the override, so Save shows the
/// confirm step. Nothing in the confirm step is saved; the series is deleted at the end. One test, one sign-in.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class CalendarTimeZoneReflowTests(StackFixture fixture) : IAsyncLifetime
{
    private const string SeriesTitle = "Reflow 520 series";
    private const string OverrideTitle = "Reflow 520 override";

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

    public sealed class Measurement
    {
        public string[] Overflows { get; set; } = [];
    }

    /// <summary>Every element named must exist, stay inside the dialog and not clip; nothing may scroll sideways.</summary>
    private static async Task<string[]> OverflowsAsync(IPage page, params string[] selectors) =>
        (await page.EvaluateAsync<Measurement>(
            """
            (selectors) => {
                const overflows = [];
                const dialog = document.querySelector('.mud-dialog');
                if (!dialog) return { overflows: ['no dialog'] };
                const d = dialog.getBoundingClientRect();
                for (const sel of selectors) {
                    const el = document.querySelector(sel);
                    if (!el) { overflows.push(sel + ' missing'); continue; }
                    const r = el.getBoundingClientRect();
                    if (r.right > d.right + 1 || r.left < d.left - 1) overflows.push(sel + ' leaves the dialog');
                    if (el.scrollWidth > el.clientWidth + 1) overflows.push(sel + ' clips its content');
                }
                if (dialog.scrollWidth > dialog.clientWidth + 1) overflows.push('the dialog scrolls sideways');
                if (document.documentElement.scrollWidth > window.innerWidth + 1) overflows.push('the page scrolls sideways');
                return { overflows };
            }
            """, selectors)).Overflows;

    private static async Task AssertReflowsAsync(IPage page, string pass, params string[] selectors)
    {
        await SetTextScaleAsync(page, "200%");
        var overflows = await OverflowsAsync(page, selectors);
        Assert.True(overflows.Length == 0, $"{pass}, 200% text: " + string.Join("; ", overflows));

        await SetTextScaleAsync(page, "100%");
        await page.SetViewportSizeAsync(320, 800);
        overflows = await OverflowsAsync(page, selectors);
        Assert.True(overflows.Length == 0, $"{pass}, 320 px: " + string.Join("; ", overflows));
        await page.SetViewportSizeAsync(1366, 900);
    }

    private static Task SetTextScaleAsync(IPage page, string scale) =>
        page.EvaluateAsync("s => { document.documentElement.style.fontSize = s; }", scale);

    private static async Task<bool> IsDarkAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.dataset.theme === 'dark'");

    private static Task ToggleThemeAsync(IPage page, bool fromDark) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = fromDark ? "Switch to light mode" : "Switch to dark mode" }).ClickAsync();

    private static ILocator TitleInput(IPage page) => page.Locator(".mud-dialog").GetByRole(AriaRole.Textbox).First;

    private static async Task CloseDialogAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).Last.ClickAsync();
        await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    private static async Task OpenWithOsloAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New event" }).First.ClickAsync();
        var zone = page.Locator("#cal-ev-tz");
        await zone.WaitForAsync();
        await zone.ClickAsync();
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.TypeAsync("oslo", new KeyboardTypeOptions { Delay = 50 });
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Europe/Oslo" }).First.ClickAsync();
        await page.Locator("#cal-ev-twozone").WaitForAsync();
    }

    /// <summary>A daily series from today, then its last visible occurrence (in the future) edited on its own.</summary>
    private static async Task BuildSeriesWithOverrideAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New event" }).First.ClickAsync();
        await page.Locator("#cal-ev-tz").WaitForAsync();
        await TitleInput(page).FillAsync(SeriesTitle);
        await page.GetByRole(AriaRole.Radio, new PageGetByRoleOptions { Name = "Repeats" }).ClickAsync();
        foreach (var day in new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" })
        {
            var toggle = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = day, Exact = true });
            if (await toggle.GetAttributeAsync("aria-pressed") != "true")
            {
                await toggle.ClickAsync();
            }
        }

        await page.Locator("#cal-ev-save").ClickAsync();
        await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        var occurrence = await LastOccurrenceAsync(page, SeriesTitle);
        await occurrence.ClickAsync();
        await TitleInput(page).WaitForAsync();
        await TitleInput(page).FillAsync(OverrideTitle);
        await page.Locator("#cal-ev-save").ClickAsync();
        await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    /// <summary>The last chip of <paramref name="title"/> in the month on screen, stepping a month on when there is none.</summary>
    private static async Task<ILocator> LastOccurrenceAsync(IPage page, string title)
    {
        for (var step = 0; step < 2; step++)
        {
            var chips = page.Locator($"[aria-label^=\"{title}\"]");
            try
            {
                await chips.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
                return chips.Last;
            }
            catch (TimeoutException)
            {
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next period" }).ClickAsync();
            }
        }

        throw new InvalidOperationException($"No occurrence of \"{title}\" on screen.");
    }

    /// <summary>Opens the override, then its series, and shortens it to one occurrence: Save shows the confirm step.</summary>
    private static async Task OpenConfirmStepAsync(IPage page)
    {
        await (await LastOccurrenceAsync(page, OverrideTitle)).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit series…" }).ClickAsync();
        await page.Locator(".cal-rule").WaitForAsync();
        var count = page.Locator(".cal-ends-opt").Nth(1).Locator("input[type=number]");
        await count.FillAsync("1");
        await count.PressAsync("Tab");
        await page.Locator("#cal-ev-save").ClickAsync();
        await page.Locator(".cal-ce-confirm").WaitForAsync();
    }

    private static async Task DeleteSeriesAsync(IPage page)
    {
        await (await LastOccurrenceAsync(page, OverrideTitle)).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit series…" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete series" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Delete", Exact = true }).Last.ClickAsync();
    }

    [SkippableFact] // AC 16
    public async Task The_zone_field_two_zone_line_and_confirm_step_reflow_in_both_themes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            TimezoneId = "America/New_York",
        });
        var page = await context.NewPageAsync();
        var owner = DemoUsers.All.First(candidate => candidate.Role == "Owner");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, owner.Email, owner.Password);

        await page.GotoAsync("/calendar");
        var startedDark = await IsDarkAsync(page);
        var flipped = false;
        var built = false;
        try
        {
            await BuildSeriesWithOverrideAsync(page);
            built = true;

            foreach (var pass in new[] { "first theme", "second theme" })
            {
                await page.SetViewportSizeAsync(1366, 900);

                await OpenWithOsloAsync(page);
                // The input itself, not only its field: a clipped zone name is the failure the field check would miss.
                await AssertReflowsAsync(page, $"{pass}, zone field", "#cal-ev-tz", ".cal-ev-tz", "#cal-ev-twozone", "#cal-ev-tz-help");
                await CloseDialogAsync(page);

                await OpenConfirmStepAsync(page);
                await AssertReflowsAsync(page, $"{pass}, confirm step", ".cal-ce-confirm", "#cal-ev-confirm-h", ".cal-ce-confirm-actions");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Back to editing" }).ClickAsync();
                await CloseDialogAsync(page);

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
            await SetTextScaleAsync(page, "100%");
            await page.SetViewportSizeAsync(1366, 900);
            if (flipped)
            {
                await page.Keyboard.PressAsync("Escape");
                await ToggleThemeAsync(page, !startedDark);
            }

            if (built)
            {
                await DeleteSeriesAsync(page);
            }
        }
    }
}
