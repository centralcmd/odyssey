using Odyssey.Client.Auth;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="LocalReturnUrl"/> is a security control — it decides where an authenticated user's
/// browser goes after a gate completes, from a value an attacker can put in a link. It shipped
/// originally as an untested private method inside the page and was wrong (PR #407 security review,
/// CWE-601): it rejected <c>//evil.com</c> but not <c>/\evil.com</c>.
/// </summary>
public class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/accounts")]
    [InlineData("/transactions?status=open")]
    [InlineData("/journal#entry-3")]
    [InlineData("/")]
    public void ALocalPath_isAccepted(string candidate) =>
        Assert.Equal(candidate, LocalReturnUrl.Parse(candidate));

    /// <summary>
    /// The regression that motivated the extraction. A browser's URL parser treats a backslash as a
    /// slash in the authority position, so each of these resolves to a DIFFERENT ORIGIN despite
    /// starting with a single forward slash.
    /// </summary>
    [Theory]
    [InlineData("/\\evil.example.com")]
    [InlineData("\\/evil.example.com")]
    [InlineData("/\\/evil.example.com")]
    [InlineData("\\\\evil.example.com")]
    [InlineData("/accounts\\..\\evil.example.com")]
    public void ABackslashBypass_isRejected(string candidate) =>
        Assert.Null(LocalReturnUrl.Parse(candidate));

    [Theory]
    [InlineData("//evil.example.com")]
    [InlineData("https://evil.example.com")]
    [InlineData("http://evil.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("accounts")]
    public void AnythingNotAnAppRelativePath_isRejected(string candidate) =>
        Assert.Null(LocalReturnUrl.Parse(candidate));

    /// <summary>Browsers strip tab/CR/LF while parsing, so a control character can smuggle a slash run past a naive check.</summary>
    [Theory]
    [InlineData("/\t/evil.example.com")]
    [InlineData("/\n/evil.example.com")]
    [InlineData("/\r/evil.example.com")]
    [InlineData("/\0/evil.example.com")]
    public void AControlCharacter_isRejected(string candidate) =>
        Assert.Null(LocalReturnUrl.Parse(candidate));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentValue_isRejected(string? candidate) =>
        Assert.Null(LocalReturnUrl.Parse(candidate));

    /// <summary>The self-reference guard: a completed gate must never be able to return to itself.</summary>
    [Theory]
    [InlineData("/accept-terms")]
    [InlineData("/accept-terms/")]
    [InlineData("/ACCEPT-TERMS")]
    [InlineData("/accept-terms?returnUrl=%2Faccounts")]
    public void TheRejectedPrefix_isRejected(string candidate) =>
        Assert.Null(LocalReturnUrl.Parse(candidate, "/accept-terms"));

    /// <summary>...but only that prefix as a whole path segment — a page merely starting with the same letters is fine.</summary>
    [Fact]
    public void APathThatMerelySharesThePrefixesText_isStillAccepted() =>
        Assert.Equal("/accounts", LocalReturnUrl.Parse("/accounts", "/accept-terms"));

    // ── FromQuery: the same control, reached the way the gate pages actually reach it ──
    // Extracted (issue #406) from per-page parsing loops. Every rejection above must still apply when
    // the value arrives through a query string rather than as a bare candidate — a wrapper that read
    // the parameter but forgot to validate it would reopen CWE-601 while every test above stayed green.

    [Fact]
    public void FromQuery_readsAndAcceptsALocalReturnUrl() =>
        Assert.Equal(
            "/transactions?status=open",
            LocalReturnUrl.FromQuery("https://app.test/change-password-required?returnUrl=%2Ftransactions%3Fstatus%3Dopen"));

    [Theory]
    [InlineData("https://app.test/gate")]
    [InlineData("https://app.test/gate?other=1")]
    [InlineData("https://app.test/gate?returnUrl=")]
    public void FromQuery_withoutAUsableParameter_isNull(string uri) =>
        Assert.Null(LocalReturnUrl.FromQuery(uri));

    /// <summary>The whole point of the extraction: the wrapper validates, it does not merely read.</summary>
    [Theory]
    [InlineData("%2F%5Cevil.example.com")]
    [InlineData("%2F%2Fevil.example.com")]
    [InlineData("https%3A%2F%2Fevil.example.com")]
    public void FromQuery_stillRejectsAnOffSiteTarget(string encoded) =>
        Assert.Null(LocalReturnUrl.FromQuery($"https://app.test/gate?returnUrl={encoded}"));

    [Fact]
    public void FromQuery_stillRejectsTheGatesOwnRoute() =>
        Assert.Null(LocalReturnUrl.FromQuery(
            "https://app.test/gate?returnUrl=%2Fchange-password-required", "/change-password-required"));

    /// <summary>
    /// A repeated parameter takes the first occurrence that VALIDATES, not the first that appears — so a
    /// hostile value cannot shadow a safe one, and each candidate is checked independently either way.
    /// </summary>
    [Fact]
    public void FromQuery_withARepeatedParameter_takesTheFirstSafeOne() =>
        Assert.Equal(
            "/accounts",
            LocalReturnUrl.FromQuery("https://app.test/gate?returnUrl=%2F%5Cevil.example.com&returnUrl=%2Faccounts"));
}
