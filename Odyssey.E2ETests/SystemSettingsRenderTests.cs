using Microsoft.Playwright;
using Odyssey.TestData;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// The system-settings grid renders in a browser, and its problems rollup actually moves focus.
/// </summary>
/// <remarks>
/// <para>
/// Every other test over this page reads its source as text (<c>Odyssey.Client.Tests</c>'s
/// <c>SettingFieldTests</c> and friends), because the client tier has no bUnit and no render harness.
/// That tier can prove the markup SAYS the right thing; it cannot prove the page renders at all, and it
/// cannot execute a single interaction. This is the largest render in the app — 40+ rows across
/// seventeen sections, six control shapes, and a notched <c>fieldset</c>/<c>legend</c> per row — so a
/// render-time refusal here is both likelier and more expensive than most.
/// </para>
/// <para>
/// <c>OdsErrorSummary</c> is the specific gap this closes. It exists to rescue a disabled Save whose
/// cause is off-screen, and every claim in that sentence — the count appears, the panel lists the
/// offending row, pressing an entry lands focus on it — is runtime behaviour that source text cannot
/// demonstrate. A summary that silently focused nothing would pass every source lint.
/// </para>
/// <para>
/// <strong>One test, one sign-in, on purpose.</strong> The identity endpoints are rate-limited (30 per
/// minute, shared across this whole suite), and xUnit builds a fresh instance of a test class for every
/// test method — so three test methods here meant three logins, and the suite already has two other
/// sign-in tests. Exceeding the limit surfaces as a login navigation timeout rather than a visible
/// rejection, which is the least debuggable shape the failure could take. The phases below are ordered
/// so each builds on the last page state, and every assertion carries its own message.
/// </para>
/// <para>
/// Nothing here saves. The page is driven dirty and then abandoned, so the deployment's stored settings
/// are never written — which matters because this suite runs against a real seeded stack.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class SystemSettingsRenderTests(StackFixture fixture) : IAsyncLifetime
{
    /// <summary>The row driven into an error: an int row with published bounds and a stable title.</summary>
    private const string RowKey = "insurance-window";

    private const string RowTitle = "\"Expiring soon\" window";

    /// <summary>Comfortably outside the row's published maximum, so it reports out-of-range.</summary>
    private const string OutOfRangeValue = "999999";

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
    public async Task The_settings_page_renders_and_its_problems_rollup_recovers_a_blocked_save()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        await using var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.BaseUrl,
            IgnoreHTTPSErrors = true,
        });

        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => errors.Add(error);

        var user = DemoUsers.All.First(candidate => candidate.Role == "Admin");
        await page.GotoAsync("/login");
        await page.GetByLabel("Username or Email").FillAsync(user.Email);
        await page.GetByLabel("Password").FillAsync(user.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/login"), new PageWaitForURLOptions { Timeout = 30_000 });

        // Only failures from the page under test, not any the login flow happened to log.
        errors.Clear();
        await page.GotoAsync("/settings");

        // Wait for a real row, and specifically for the FIELDSET. The loading skeleton deliberately
        // mirrors the loaded shape — same `.odc-sfield` wrapper, same `.odc-sfield-frame` class — so
        // nothing reflows when the catalogue arrives, which means waiting on either of those matches the
        // skeleton and returns while the page is still loading. Only a loaded row uses a real
        // <fieldset>; the skeleton's frame is a <div>.
        await Expect(page.Locator("fieldset.odc-sfield-frame").First).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // ── The grid rendered, not just the page chrome ──────────────────────────────────────────
        Assert.True(await page.Locator("section.ss-sect").CountAsync() > 0, "No setting sections rendered.");

        var frames = await page.Locator("fieldset.odc-sfield-frame").CountAsync();
        var legends = await page.Locator("fieldset.odc-sfield-frame > legend").CountAsync();
        Assert.True(frames > 0, "No notched field frames rendered.");
        Assert.True(frames == legends,
            $"{frames} notched frames but {legends} legends — the browser cuts the notch from the legend, "
            + "so a frame without one is an empty outline.");

        // Every rendered field carries its always-visible helper line.
        Assert.Equal(
            await page.Locator(".odc-sfield").CountAsync(),
            await page.Locator(".odc-sfield .odc-sfield-help").CountAsync());

        // ── A Toggle row's description reaches assistive tech (WCAG 1.3.1) ───────────────────────
        // Asserted on the rendered input rather than in source: MudSwitch exposes no AriaDescribedBy,
        // so OdsSwitch gets the attribute there by splatting, and whether a splatted attribute lands on
        // the underlying input is a MudBlazor implementation detail only a real render can settle. The
        // id must also RESOLVE — an aria-describedby pointing at nothing reads exactly like a correct one.
        var describedBy = await page.Locator("input.mud-switch-input").First.GetAttributeAsync("aria-describedby");
        Assert.False(string.IsNullOrWhiteSpace(describedBy), "A settings toggle has no accessible description.");

        foreach (var id in describedBy!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var target = page.Locator($"#{id}");
            Assert.True(await target.CountAsync() == 1, $"aria-describedby names '{id}', which is not in the DOM.");
            Assert.False(string.IsNullOrWhiteSpace(await target.InnerTextAsync()),
                $"The description '{id}' resolves to an empty element.");
        }

        // ── The problems rollup ──────────────────────────────────────────────────────────────────
        var save = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save changes" });
        await Expect(save).ToBeEnabledAsync();

        // Drive one row out of range. Fill + explicit blur, so the change handler has certainly run.
        var input = page.Locator($"#ss-in-{RowKey}");
        await input.FillAsync(OutOfRangeValue);
        await input.BlurAsync();

        await Expect(save).ToBeDisabledAsync();

        var summary = page.Locator("button.odc-errsum");
        await Expect(summary).ToBeVisibleAsync();

        // The count is folded into the accessible name — "1 · Review" would announce as noise.
        var name = await summary.GetAttributeAsync("aria-label");
        Assert.Contains("problem", name ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // It is a disclosure: pressing it lists the problems rather than jumping blindly.
        await Expect(summary).ToHaveAttributeAsync("aria-expanded", "false");
        await summary.ClickAsync();
        await Expect(summary).ToHaveAttributeAsync("aria-expanded", "true");

        var entries = page.Locator("button.odc-errsum-item");
        Assert.True(await entries.CountAsync() > 0, "The rollup opened with no problems listed.");
        await Expect(entries.First).ToContainTextAsync(RowTitle);

        // …and pressing an entry lands focus on that row, which is what makes a disabled Save
        // recoverable by keyboard when the offending field is off-screen.
        await entries.First.ClickAsync();
        await Expect(page.Locator($"#ss-ttl-{RowKey}")).ToBeFocusedAsync();

        // A render failure inside a child component can leave the surrounding page looking fine, so the
        // console is checked alongside the visible assertions.
        var componentErrors = errors
            .Where(error => error.Contains("UserAttributes", StringComparison.Ordinal)
                            || error.Contains("Unhandled exception rendering component", StringComparison.Ordinal))
            .ToList();

        Assert.True(componentErrors.Count == 0, string.Join("\n", componentErrors));
    }
}
