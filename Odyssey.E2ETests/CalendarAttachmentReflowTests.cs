using Odyssey.Testing;
using System.Text.Json;
using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests;

/// <summary>
/// The event dialog's #522 surfaces reflow (issue #522 AC 12): in both themes, at 200 % text and at a 320 px viewport,
/// the Photos and Attachments fields, the read-only "From the series" section and the export-attachments step neither
/// clip nor scroll sideways. The sibling of <see cref="CalendarTimeZoneReflowTests"/>; layout is what bUnit cannot see.
/// </summary>
/// <remarks>
/// The fixtures are made over the API with the signed-in browser's own cookies: a throwaway calendar holding one event
/// and one daily series, each linked to a stored file and a library photo. The event, the series and then the (now
/// empty) calendar are deleted at the end. One test, one sign-in.
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class CalendarAttachmentReflowTests(StackFixture fixture) : IAsyncLifetime
{
    private const string EventTitle = "Reflow 522 event";
    private const string SeriesTitle = "Reflow 522 series";
    private const string ApiBaseUrlEnvVar = "E2E_API_BASE_URL";

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

    /// <summary>Every element named must exist, stay inside its dialog and not clip; nothing may scroll sideways.</summary>
    private static async Task<string[]> OverflowsAsync(IPage page, string dialogSelector, params string[] selectors) =>
        (await page.EvaluateAsync<Measurement>(
            """
            ([dialogSelector, selectors]) => {
                const overflows = [];
                const dialog = document.querySelector(dialogSelector);
                if (!dialog) return { overflows: ['no dialog ' + dialogSelector] };
                const d = dialog.getBoundingClientRect();
                for (const sel of selectors) {
                    const el = dialog.querySelector(sel);
                    if (!el) { overflows.push(sel + ' missing'); continue; }
                    const r = el.getBoundingClientRect();
                    if (r.right > d.right + 1 || r.left < d.left - 1) overflows.push(sel + ' leaves the dialog');
                    if (el.scrollWidth > el.clientWidth + 1) overflows.push(sel + ' clips its content');
                }
                if (dialog.scrollWidth > dialog.clientWidth + 1) overflows.push('the dialog scrolls sideways');
                if (document.documentElement.scrollWidth > window.innerWidth + 1) overflows.push('the page scrolls sideways');
                return { overflows };
            }
            """, new object[] { dialogSelector, selectors })).Overflows;

    private static async Task AssertReflowsAsync(IPage page, string pass, string dialogSelector, params string[] selectors)
    {
        await SetTextScaleAsync(page, "200%");
        var overflows = await OverflowsAsync(page, dialogSelector, selectors);
        Assert.True(overflows.Length == 0, $"{pass}, 200% text: " + string.Join("; ", overflows));

        await SetTextScaleAsync(page, "100%");
        await page.SetViewportSizeAsync(320, 800);
        overflows = await OverflowsAsync(page, dialogSelector, selectors);
        Assert.True(overflows.Length == 0, $"{pass}, 320 px: " + string.Join("; ", overflows));
        await page.SetViewportSizeAsync(1366, 900);
    }

    private static Task SetTextScaleAsync(IPage page, string scale) =>
        page.EvaluateAsync("s => { document.documentElement.style.fontSize = s; }", scale);

    private static async Task<bool> IsDarkAsync(IPage page) =>
        await page.EvaluateAsync<bool>("() => document.documentElement.dataset.theme === 'dark'");

    private static Task ToggleThemeAsync(IPage page, bool fromDark) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = fromDark ? "Switch to light mode" : "Switch to dark mode" }).ClickAsync();

    // ── Fixtures over the API ────────────────────────────────────────────────

    private sealed record Api(IAPIRequestContext Request, string BaseUrl, string Token);

    /// <summary>
    /// The API as the signed-in browser reaches it: same-origin <c>/api</c> behind the Compose stack's NGINX, else the
    /// Aspire API port (or <c>E2E_API_BASE_URL</c>). The cookies are the browser context's; the antiforgery token is read
    /// once and echoed on every write, as the client does.
    /// </summary>
    private async Task<Api> ApiAsync(IBrowserContext context)
    {
        var candidates = new[] { Environment.GetEnvironmentVariable(ApiBaseUrlEnvVar), fixture.BaseUrl, "http://localhost:5188" }
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.TrimEnd('/'));
        foreach (var baseUrl in candidates)
        {
            var response = await context.APIRequest.GetAsync($"{baseUrl}/api/antiforgery/token");
            if (response.Ok && (response.Headers.GetValueOrDefault("content-type") ?? string.Empty).Contains("json", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(await response.TextAsync());
                return new Api(context.APIRequest, baseUrl, doc.RootElement.GetProperty("token").GetString()!);
            }
        }

        throw new InvalidOperationException($"No API answered for the signed-in browser; set {ApiBaseUrlEnvVar}.");
    }

    private static async Task<JsonElement> SendAsync(Api api, string method, string path, object? body = null)
    {
        var response = await api.Request.FetchAsync($"{api.BaseUrl}{path}", new APIRequestContextOptions
        {
            Method = method,
            DataObject = body,
            Headers = new Dictionary<string, string> { ["X-XSRF-TOKEN"] = api.Token },
        });
        var text = await response.TextAsync();
        Assert.True(response.Ok, $"{method} {path}: {response.Status} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Guid FirstId(JsonElement page, string property) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(property).GetGuid()).FirstOrDefault();

    // ── The test ─────────────────────────────────────────────────────────────

    [SkippableFact] // AC 12
    public async Task The_attachment_fields_the_series_section_and_the_export_step_reflow_in_both_themes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            TimezoneId = "UTC",
        });
        var page = await context.NewPageAsync();
        var owner = DemoUsers.All.First(candidate => candidate.Role == "Owner");
        await page.GotoAsync("/login");
        await E2ESignIn.SignInAsync(page, owner.Email, owner.Password);

        var api = await ApiAsync(context);
        var fileId = Guid.Empty;
        var photoId = Guid.Empty;
        // The worker writes the demo files and photos after the stack comes up (DemoFileSeeder), so wait for them.
        await DemoFileWait.UntilAsync(async () =>
        {
            fileId = FirstId(await SendAsync(api, "GET", "/api/files?limit=1"), "id");
            photoId = FirstId(await SendAsync(api, "GET", "/api/photos?limit=1"), "photoId");
            return fileId != Guid.Empty && photoId != Guid.Empty;
        });
        Skip.If(fileId == Guid.Empty || photoId == Guid.Empty, "The seeded stack has no visible file or photo to link.");

        var calendarId = (await SendAsync(api, "POST", "/api/calendars", new { name = $"Reflow 522 {Guid.NewGuid():N}", color = "#0369A1" }))
            .GetProperty("calendarId").GetGuid();
        var startedDark = false;
        var flipped = false;
        Guid? eventId = null;
        Guid? seriesId = null;
        try
        {
            var today = DateTime.UtcNow.Date;
            eventId = (await SendAsync(api, "POST", "/api/calendar-events", new
            {
                calendarId,
                title = EventTitle,
                startDateTime = today.AddHours(9),
                endDateTime = today.AddHours(10),
                attachmentFileIds = new[] { fileId },
                photoIds = new[] { photoId },
            })).GetProperty("calendarEventId").GetGuid();
            seriesId = (await SendAsync(api, "POST", "/api/recurrence-patterns", new
            {
                calendarId,
                title = SeriesTitle,
                startDateTime = today.AddHours(18),
                endDateTime = today.AddHours(19),
                frequency = 0,
                interval = 1,
                occurrenceCount = 3,
                attachmentFileIds = new[] { fileId },
                photoIds = new[] { photoId },
            })).GetProperty("recurrencePatternId").GetGuid();

            await page.GotoAsync("/calendar");
            await page.GetByRole(AriaRole.Radio, new PageGetByRoleOptions { Name = "Month" }).ClickAsync();
            startedDark = await IsDarkAsync(page);

            foreach (var pass in new[] { "first theme", "second theme" })
            {
                await page.SetViewportSizeAsync(1366, 900);

                // The editable fields, then the export step stacked over them.
                await page.Locator($"button.odc-cal-chip[title=\"{EventTitle}\"]").First.ClickAsync();
                await page.Locator(".cal-ce-links .jef-photo").WaitForAsync();
                await AssertReflowsAsync(page, $"{pass}, fields", ".mud-dialog.cal-event-dialog",
                    ".cal-ce-links", ".jef-photos", ".jef-files", ".jec-file", ".alf-caphelp", ".mud-dialog-actions");

                await page.Locator("#cal-export-btn").ClickAsync();
                await page.Locator(".cal-exp-att-body").WaitForAsync();
                await AssertReflowsAsync(page, $"{pass}, export step", ".mud-dialog.cal-exp-att",
                    ".cal-exp-att-body", ".cal-exp-att-check", ".mud-dialog-actions");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).Last.ClickAsync();
                await page.Locator(".mud-dialog.cal-exp-att").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).Last.ClickAsync();
                await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

                // An occurrence: the read-only "From the series".
                await page.Locator($"button.odc-cal-chip[title=\"{SeriesTitle}\"]").First.ClickAsync();
                await page.Locator(".cal-ce-fromseries .jef-photo").WaitForAsync();
                await AssertReflowsAsync(page, $"{pass}, from the series", ".mud-dialog.cal-event-dialog",
                    ".cal-ce-fromseries", ".cal-ce-fromseries-help", ".jef-photos", ".jec-file");
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).Last.ClickAsync();
                await page.Locator(".mud-dialog").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

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

            if (eventId is { } e)
            {
                await SendAsync(api, "DELETE", $"/api/calendar-events/{e}");
            }

            if (seriesId is { } s)
            {
                await SendAsync(api, "DELETE", $"/api/recurrence-patterns/{s}");
            }

            await SendAsync(api, "DELETE", $"/api/calendars/{calendarId}");
        }
    }
}
