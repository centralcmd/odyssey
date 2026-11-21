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
/// confirm step. Nothing in the confirm step is saved. One test, one sign-in.
/// </para>
/// <para>
/// It runs against a shared database, so it touches only its own rows: every run's titles carry a unique suffix, its
/// rows are found in the agenda (one row per occurrence, never hidden in a month cell's overflow), and the cleanup in
/// <c>finally</c> deletes, by id, every event and the series this run created — whatever step failed. The cleanup goes
/// through the API rather than "Delete series", which DETACHES a series' past and current rows into standalone events
/// and so left today's occurrence behind on every run: the leak behind the stale rows earlier runs found.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class CalendarTimeZoneReflowTests(StackFixture fixture) : IAsyncLifetime
{
    // This run's own titles: a shared database can hold any number of other runs' rows.
    private readonly string run = Guid.NewGuid().ToString("N")[..8];

    private string SeriesTitle => $"Reflow 520 series {run}";

    private string OverrideTitle => $"Reflow 520 override {run}";

    // What this run created, recorded as it is created, so the cleanup can remove it whatever failed after.
    private string? apiBase;
    private string? seriesId;

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

    /// <summary>
    /// A daily series from today, then its last occurrence in the agenda's month (a future one) edited on its own. The
    /// series' id and the API's address are taken from the create response, before anything else can fail.
    /// </summary>
    private async Task BuildSeriesWithOverrideAsync(IPage page)
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

        var created = await page.RunAndWaitForResponseAsync(
            () => page.Locator("#cal-ev-save").ClickAsync(),
            r => r.Request.Method == "POST" && r.Url.Contains("/api/recurrence-patterns", StringComparison.Ordinal));
        apiBase = created.Url[..created.Url.IndexOf("/api/", StringComparison.Ordinal)];
        seriesId = (await created.JsonAsync())?.GetProperty("recurrencePatternId").GetString();
        await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        await page.GetByRole(AriaRole.Radio, new PageGetByRoleOptions { Name = "Agenda" }).ClickAsync();
        await (await LastAgendaRowAsync(page, SeriesTitle, minimum: 2)).ClickAsync();
        await TitleInput(page).WaitForAsync();
        await TitleInput(page).FillAsync(OverrideTitle);
        await page.Locator("#cal-ev-save").ClickAsync();
        await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    /// <summary>
    /// The last agenda row of exactly this run's <paramref name="title"/> in the month on screen, stepping a month on when
    /// it holds fewer than <paramref name="minimum"/> — two guarantees the last is after today, so it is a future one.
    /// </summary>
    private static async Task<ILocator> LastAgendaRowAsync(IPage page, string title, int minimum = 1)
    {
        for (var step = 0; step < 2; step++)
        {
            var rows = page.Locator(".cal-agenda-row", new PageLocatorOptions { HasText = title });
            try
            {
                await rows.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
                if (await rows.CountAsync() >= minimum)
                {
                    return rows.Last;
                }
            }
            catch (TimeoutException)
            {
                // None in this month yet.
            }

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Next period" }).ClickAsync();
        }

        throw new InvalidOperationException($"No agenda row of \"{title}\" on screen.");
    }

    /// <summary>Opens the override, then its series, and shortens it to one occurrence: Save shows the confirm step.</summary>
    private async Task OpenConfirmStepAsync(IPage page)
    {
        await (await LastAgendaRowAsync(page, OverrideTitle)).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit series…" }).ClickAsync();
        await page.Locator(".cal-rule").WaitForAsync();
        var count = page.Locator(".cal-ends-opt").Nth(1).Locator("input[type=number]");
        await count.FillAsync("1");
        await count.PressAsync("Tab");
        await page.Locator("#cal-ev-save").ClickAsync();
        await page.Locator(".cal-ce-confirm").WaitForAsync();
    }

    public sealed class Cleanup
    {
        public int Deleted { get; set; }

        public string[] Failures { get; set; } = [];
    }

    /// <summary>
    /// Deletes, by id through the API, the series and then every event carrying this run's suffix in a window around today —
    /// the override and any row the series' delete left standalone. Other runs' rows are never matched.
    /// </summary>
    private async Task<Cleanup> DeleteThisRunAsync(IPage page)
    {
        if (apiBase is null)
        {
            return new Cleanup();
        }

        var from = DateTime.UtcNow.Date.AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var to = DateTime.UtcNow.Date.AddDays(84).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        return await page.EvaluateAsync<Cleanup>(
            """
            async ({ apiBase, run, seriesId, from, to }) => {
                const failures = [];
                const token = (await (await fetch(apiBase + '/api/antiforgery/token', { credentials: 'include' })).json()).token;
                const send = (method, path) => fetch(apiBase + path, { method, credentials: 'include', headers: { 'X-XSRF-TOKEN': token } });
                let deleted = 0;
                // The series first: its delete removes the future rows and detaches today's, which the sweep then takes.
                if (seriesId) {
                    const r = await send('DELETE', '/api/recurrence-patterns/' + seriesId);
                    if (r.ok || r.status === 404) deleted++; else failures.push('series ' + r.status);
                }
                const list = await send('GET', `/api/calendar-events?from=${from}&to=${to}&viewTimeZoneId=UTC&search=${run}&limit=200`);
                if (list.ok) {
                    const body = await list.json();
                    for (const e of (body.items || body)) {
                        if (!(e.title || '').endsWith(' ' + run) && e.recurrencePatternId !== seriesId) continue;
                        const r = await send('DELETE', '/api/calendar-events/' + e.calendarEventId);
                        if (r.ok || r.status === 404) deleted++; else failures.push('event ' + r.status);
                    }
                } else {
                    failures.push('list ' + list.status);
                }
                return { deleted, failures };
            }
            """,
            new { apiBase, run, seriesId, from, to });
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
        var passed = false;
        try
        {
            await BuildSeriesWithOverrideAsync(page);

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

            passed = true;
        }
        finally
        {
            // First, and through the API alone: a UI reset below can throw on a page a failed step left in any state, and
            // must not be what decides whether this run's rows outlive it. By id, never by a title another run could share.
            // Caught, so a throwing cleanup neither replaces the step's own error nor skips the resets below.
            Cleanup cleanup;
            try
            {
                cleanup = await DeleteThisRunAsync(page);
            }
            catch (Exception ex)
            {
                cleanup = new Cleanup { Failures = [$"cleanup threw {ex.GetType().Name}: {ex.Message}"] };
            }

            await SetTextScaleAsync(page, "100%");
            await page.SetViewportSizeAsync(1366, 900);
            if (flipped)
            {
                await page.Keyboard.PressAsync("Escape");
                await ToggleThemeAsync(page, !startedDark);
            }

            // A failed step's own error is the one to report; a leak is reported when nothing else failed.
            if (passed)
            {
                Assert.True(cleanup.Failures.Length == 0, "Cleanup left rows behind: " + string.Join("; ", cleanup.Failures));
            }
        }
    }
}
