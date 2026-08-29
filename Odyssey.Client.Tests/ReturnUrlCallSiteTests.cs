using System.Text.RegularExpressions;
using Odyssey.Client.Auth;
using Odyssey.Client.Pages;
using Odyssey.Client.Pages.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The gate pages' own use of <see cref="LocalReturnUrl"/> (issue #408).
///
/// <para>
/// <see cref="LocalReturnUrlTests"/> proves the control is correct; these prove each call site actually
/// reaches it. That distinction is the whole issue: <c>/accept-terms</c> was fixed in PR #407 while
/// <c>/login</c> and <c>/onboarding</c> kept their own inline copies of the same check — including the
/// backslash bypass the fix existed to close — because nothing tied a page to the shared control. The
/// last test here is what stops a third copy appearing.
/// </para>
/// </summary>
public class ReturnUrlCallSiteTests
{
    /// <summary>
    /// The shapes that resolve OFF-ORIGIN in a browser despite looking app-relative. A gate that accepts
    /// one of these hands a freshly authenticated user's browser to an attacker-chosen host (CWE-601).
    /// </summary>
    public static TheoryData<string> HostileTargets =>
    [
        "/\\evil.example.com",
        "\\/evil.example.com",
        "\\\\evil.example.com",
        "//evil.example.com",
        "https://evil.example.com",
        "http://evil.example.com/accounts",
        "javascript:alert(1)",
        "/\t/evil.example.com",
        "/\r\n/evil.example.com",
    ];

    // ────────────────────────────── /login ──────────────────────────────

    [Theory]
    [MemberData(nameof(HostileTargets))]
    public void Login_fallsBackToTheDashboard_forAnOffSiteTarget(string returnUrl) =>
        Assert.Equal("/", Login.Destination(returnUrl));

    [Theory]
    [InlineData("/accounts")]
    [InlineData("/transactions?status=open&sort=date")]
    [InlineData("/journal#entry-3")]
    [InlineData("/")]
    public void Login_returnsToALocalTarget(string returnUrl) =>
        Assert.Equal(returnUrl, Login.Destination(returnUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Login_withoutAReturnUrl_goesToTheDashboard(string? returnUrl) =>
        Assert.Equal("/", Login.Destination(returnUrl));

    /// <summary>A completed sign-in must not land back on the sign-in form.</summary>
    [Theory]
    [InlineData("/login")]
    [InlineData("/LOGIN?returnUrl=%2Faccounts")]
    public void Login_refusesToReturnToItself(string returnUrl) =>
        Assert.Equal("/", Login.Destination(returnUrl));

    /// <summary>
    /// The producer half of the same contract: what the redirect stubs emit must be what
    /// <see cref="Login.Destination"/> accepts.
    /// </summary>
    /// <remarks>
    /// This end failed silently, which is why it needs its own test. The stubs used to hand
    /// <c>ToBaseRelativePath</c>'s output straight to <c>returnUrl</c>, unrooted — and an unrooted value
    /// is not rejected loudly, it is simply dropped by <see cref="LocalReturnUrl"/> and the user lands on
    /// the dashboard instead of the page they asked for. Round-tripping producer through consumer is what
    /// makes the two ends fail together rather than drift apart.
    /// </remarks>
    [Theory]
    [InlineData("accounts", "/accounts")]
    [InlineData("transactions?status=open&sort=date", "/transactions?status=open&sort=date")]
    [InlineData("journal#entry-3", "/journal#entry-3")]
    [InlineData("", "/")]
    public void SignInUrl_roundTripsBackToTheRequestedPage(string baseRelativePath, string expected)
    {
        var returnUrl = LocalReturnUrl.FromQuery("https://app.test" + Login.SignInUrlFor(baseRelativePath));

        Assert.Equal(expected, returnUrl);
        Assert.Equal(expected, Login.Destination(returnUrl));
    }

    /// <summary>
    /// A route whose name or query would otherwise be read as query-string structure still survives the
    /// trip: the stub escapes the whole value, so the <c>&amp;</c> below cannot split it into two
    /// parameters and truncate the destination.
    /// </summary>
    [Fact]
    public void SignInUrl_escapesTheReturnUrl() =>
        Assert.Equal(
            "/reports?name=a%26b",
            LocalReturnUrl.FromQuery("https://app.test" + Login.SignInUrlFor("reports?name=a%26b")));

    /// <summary>
    /// The stubs render under <c>/login</c>'s own sibling routes, so the self-reference guard has to hold
    /// on the produced URL too — a stub that captured <c>/login</c> would bounce the user back to the form
    /// they just completed.
    /// </summary>
    [Fact]
    public void SignInUrl_fromTheLoginRouteItself_fallsBackToTheDashboard() =>
        Assert.Equal("/", Login.Destination(
            LocalReturnUrl.FromQuery("https://app.test" + Login.SignInUrlFor("login"))));

    // ──────────────────────────── /onboarding ───────────────────────────

    [Theory]
    [MemberData(nameof(HostileTargets))]
    public void Onboarding_rejectsAnOffSiteTarget(string returnUrl) =>
        Assert.Null(Onboarding.ReadReturnUrl(
            $"https://app.test/onboarding?returnUrl={Uri.EscapeDataString(returnUrl)}"));

    [Theory]
    [InlineData("/accounts")]
    [InlineData("/transactions?status=open&sort=date")]
    [InlineData("/journal#entry-3")]
    [InlineData("/")]
    public void Onboarding_readsALocalTarget(string returnUrl) =>
        Assert.Equal(returnUrl, Onboarding.ReadReturnUrl(
            $"https://app.test/onboarding?returnUrl={Uri.EscapeDataString(returnUrl)}"));

    [Theory]
    [InlineData("https://app.test/onboarding")]
    [InlineData("https://app.test/onboarding?returnUrl=")]
    [InlineData("https://app.test/onboarding?other=1")]
    public void Onboarding_withoutAReturnUrl_isNull(string uri) =>
        Assert.Null(Onboarding.ReadReturnUrl(uri));

    /// <summary>A completed gate must not send the user back into the gate.</summary>
    [Fact]
    public void Onboarding_refusesToReturnToItself() =>
        Assert.Null(Onboarding.ReadReturnUrl("https://app.test/onboarding?returnUrl=%2Fonboarding"));

    // ───────────────────── one implementation, not three ────────────────

    /// <summary>
    /// Hand-rolled <c>returnUrl</c> validation, anywhere in the client but
    /// <see cref="LocalReturnUrl"/> itself.
    /// </summary>
    /// <remarks>
    /// Each of the three shapes below was a real inline check that was individually wrong: a leading-slash
    /// test that a backslash walks past, a <c>//</c> test that does the same, and the
    /// <c>TrimStart('/')</c> normalisation <c>Login.razor</c> used, which neutralised the protocol-relative
    /// form and so read as a deliberate guard while the backslash form went straight through it.
    /// </remarks>
    private static readonly Regex InlineValidation = new(
        @"StartsWith\(\s*""//|StartsWith\(\s*'/'|TrimStart\(\s*'/'\s*\)|Contains\(\s*'\\\\'",
        RegexOptions.Compiled);

    /// <summary>
    /// A page that validates <c>returnUrl</c> itself is the defect this issue is about, not a style
    /// preference: the fix landed on <c>/accept-terms</c> in PR #407 and the two identical copies
    /// elsewhere stayed vulnerable for another release, because nothing failed when they diverged.
    /// </summary>
    [Fact]
    public void No_client_source_validates_a_returnUrl_inline()
    {
        var violations = new List<string>();

        foreach (var file in ClientSource.SourceFiles())
        {
            var relative = ClientSource.Relative(file);
            if (relative.Replace('\\', '/') == "Auth/LocalReturnUrl.cs")
                continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("returnUrl", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*'))
                    continue;

                if (InlineValidation.IsMatch(code))
                    violations.Add($"{relative}:{i + 1} — {code.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "returnUrl validated inline instead of through LocalReturnUrl "
            + "(every copy is a place the backslash bypass can come back):\n"
            + string.Join('\n', violations));
    }
}
