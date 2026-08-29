using Odyssey.Api.SystemSettings;
using Odyssey.Context;
using Xunit;
using Odyssey.Dtos;

namespace Odyssey.Api.Tests;

/// <summary>
/// The sharpest risk issue #421 Wave 1 introduces (AC 16/17).
///
/// <para>
/// The privacy-notice URL becomes admin-editable and is rendered into an <c>href</c> in the
/// analyze-file consent panel. Blazor does not sanitise <c>href</c>, and
/// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> accepts <c>javascript:</c> and <c>data:</c>
/// as perfectly well-formed absolute URIs — so the https-only scheme allow-list is the only thing
/// standing between a settings write and stored XSS in a GDPR consent gate.
/// </para>
///
/// <para>
/// Both ends are tested, and the second is the one that matters most: write-time validation protects
/// only values that arrived through the API. It does nothing for one planted by a database restore, a
/// hand edit, or an older build with weaker rules — which is why the read projection re-validates.
/// </para>
/// </summary>
public class PrivacyNoticeUrlTests
{
    [Theory]
    // The two that TryCreate happily accepts as absolute — the whole reason the allow-list exists.
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    // Other schemes that are absolute and wrong.
    [InlineData("http://example.test/policy")]
    [InlineData("ftp://example.test/policy")]
    [InlineData("file:///etc/passwd")]
    // Not absolute at all.
    [InlineData("/legal/privacy")]
    [InlineData("example.test/policy")]
    [InlineData("")]
    [InlineData("   ")]
    // Credentials in a URL rendered as a link are a phishing affordance.
    [InlineData("https://user:pass@example.test/policy")]
    public void A_value_that_is_not_a_plain_absolute_https_url_is_rejected(string value)
    {
        Assert.NotNull(PrivacyNoticeUrl.Validate(value));
    }

    [Theory]
    [InlineData("https://www.anthropic.com/legal/privacy")]
    [InlineData("https://example.test")]
    [InlineData("https://example.test/a/b?c=d#e")]
    public void An_absolute_https_url_is_accepted(string value)
    {
        Assert.Null(PrivacyNoticeUrl.Validate(value));
    }

    /// <summary>
    /// AC 17. A value planted directly in the database — bypassing the API entirely — must not be
    /// served to the client, or the write-time check is security theatre.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("http://example.test/policy")]
    [InlineData("not a url at all")]
    public void A_database_planted_value_is_replaced_on_the_read_path(string planted)
    {
        var projected = PrivacyNoticeUrl.Project(planted, SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl);

        Assert.Equal(SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl, projected);
        Assert.DoesNotContain("javascript:", projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", projected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_valid_stored_value_is_served_canonicalised()
    {
        // Canonicalised rather than echoed, so what reaches the href is a form the Uri parser produced
        // rather than whatever string happened to be stored.
        var projected = PrivacyNoticeUrl.Project(
            "https://Example.test/Policy", SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl);

        Assert.StartsWith("https://example.test/", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_default_is_itself_valid()
    {
        // Otherwise the fallback path would substitute a value that the validator rejects — an
        // infinite-fallback bug that only shows up when something else has already gone wrong.
        Assert.Null(PrivacyNoticeUrl.Validate(SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl));
    }
}
