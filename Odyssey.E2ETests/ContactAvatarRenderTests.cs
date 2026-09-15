using Microsoft.Playwright;
using Odyssey.TestData;
using Odyssey.TestData.Catalog;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Odyssey.E2ETests;

/// <summary>
/// The seeded demo pair actually renders in a real browser (issue #86 AC 40): one person with a
/// picture, one organization with a logo.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place the <b>cross-origin</b> case is exercised. <c>Cross-Origin-Resource-Policy</c>
/// is enforced on no-cors subresource loads — which is exactly how an <c>&lt;img src&gt;</c> loads — and
/// it compares scheme, host <b>and port</b>. Under Docker nginx proxies <c>/api/</c> same-origin, so a
/// Docker-only run cannot tell <c>same-site</c> from <c>same-origin</c>; against a non-Docker stack
/// (client on 5199, API on 5188) <c>same-origin</c> would block every avatar. Silently, because a load
/// failure degrades to the type glyph with nothing surfaced to the user — which is why this asserts the
/// image element's <b>natural size</b> rather than merely that a mark is on screen.
/// </para>
/// </remarks>
[Collection(StackCollection.Name)]
public sealed class ContactAvatarRenderTests(StackFixture fixture) : IAsyncLifetime
{
    // ONE signed-in session for the whole class, held statically because xUnit constructs a new test
    // class instance per test method — so an instance field would sign in once per test.
    //
    // That is not a micro-optimisation. The sign-in endpoint is rate-limited (RateLimiting:Identity),
    // and the whole browser suite shares one collection, so every test that signs in separately spends
    // from the same small budget inside one window. Two more sign-ins is what pushes the suite over it,
    // and the symptom is a timeout on an unrelated test, which reads as a flake rather than as the
    // budget it actually is.
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static IPlaywright? playwright;
    private static IBrowser? browser;
    private static IBrowserContext? browserContext;
    private static IPage? signedInPage;

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
            browserContext = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = fixture.BaseUrl,
                IgnoreHTTPSErrors = true,
            });

            var page = await browserContext.NewPageAsync();

            // Warmed up before the timed wait: against a dev server the WASM payload is compiled on
            // demand, so the first load is far slower than any later one.
            await page.GotoAsync("/login", new PageGotoOptions { Timeout = 120_000 });
            await SignInAsync(page);

            signedInPage = page;
        }
        finally
        {
            SessionGate.Release();
        }
    }

    /// <summary>
    /// Nothing per-test to tear down — the session is class-wide and the browser goes with the test
    /// process, which is what keeps the sign-in count at one.
    /// </summary>
    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task The_seeded_person_and_organization_both_render_their_image()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var page = signedInPage!;
        await page.GotoAsync("/contacts");

        foreach (var (contactKey, isLogo) in Contacts.WithAvatars)
        {
            var name = DisplayNameFor(contactKey);
            var row = page.Locator(".odc-record", new PageLocatorOptions { HasText = name }).First;
            await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });

            var mark = row.Locator(".odc-record-mark.img img");
            await Expect(mark).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000 });

            // naturalWidth is 0 for an image that FAILED to load — a broken avatar is otherwise
            // indistinguishable from a working one, because the card falls back to the type glyph and
            // says nothing.
            var naturalWidth = await mark.EvaluateAsync<int>("img => img.naturalWidth");
            Assert.True(naturalWidth > 0, $"{name}'s image did not load (naturalWidth 0).");

            // The framing follows what the image IS, not the shape: a wordmark cropped to a circle is
            // unrecognisable, so a logo is contained on a neutral ground.
            // Split into CLASS TOKENS rather than substring-matched: "ground" contains "round", so a
            // naive Contains would report a logo's neutral ground as a circular photo mark.
            var markClasses = (await row.Locator(".odc-record-mark.img").GetAttributeAsync("class") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(isLogo, markClasses.Contains("ground"));
            Assert.Equal(!isLogo, markClasses.Contains("round"));

            // Decorative: the adjacent cell already names the contact.
            Assert.Equal(string.Empty, await mark.GetAttributeAsync("alt"));
            Assert.Equal("lazy", await mark.GetAttributeAsync("loading"));
        }
    }

    [SkippableFact]
    public async Task The_avatar_response_carries_same_site_rather_than_same_origin()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var page = signedInPage!;

        IResponse? avatarResponse = null;
        page.Response += (_, response) =>
        {
            if (response.Url.Contains("/avatar", StringComparison.Ordinal))
            {
                avatarResponse ??= response;
            }
        };

        await page.GotoAsync("/contacts");
        await page.Locator(".odc-record-mark.img img").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });

        Assert.NotNull(avatarResponse);
        var headers = await avatarResponse!.AllHeadersAsync();

        Assert.Equal("same-site", Header(headers, "cross-origin-resource-policy"));
        Assert.Equal("nosniff", Header(headers, "x-content-type-options"));

        // no-cache, not no-store: revocation and erasure are immediate — a deleted image 404s on the
        // next revalidation rather than hiding behind a cached 200 — while the 304 still carries no body.
        Assert.Contains("no-cache", Header(headers, "cache-control") ?? "", StringComparison.Ordinal);
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static string DisplayNameFor(string contactKey) =>
        // The landlord's catalogue key carries a parenthetical role the UI does not render.
        contactKey == Contacts.Landlord ? "Jane Smith" : contactKey;

    /// <summary>
    /// Signs in as <b>Owner</b>, not the Admin every other class uses. It makes no difference to the
    /// per-network limiter the shared helper handles, but the surface is ALSO limited per email over a
    /// much longer window — which a second class signing in as Admin would eventually trip.
    /// </summary>
    private static Task SignInAsync(IPage page)
    {
        var actor = DemoUsers.All.First(user => user.Role == "Owner");
        return E2ESignIn.SignInAsync(page, actor.Email, actor.Password);
    }
}
