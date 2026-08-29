using System.Net;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The reset email Odyssey composes itself (issue #405). <c>/forgotPassword</c> hands the sender a
/// bare token rather than a link — the asymmetry with confirmation that made the previous message a
/// dead end for the user — so the client URL, the anchor text and the no-client-URL fallback are all
/// this code's responsibility.
/// </summary>
public class PasswordResetMailTests
{
    private const string ClientBaseUrl = "https://app.example.test";

    private const string Code = "CfDJ8Aq-fake-reset-token_09";

    [Fact]
    public void TheLink_PointsAtTheClientResetPage_CarryingOnlyTheCode()
    {
        var message = PasswordResetMail.Compose(Code, ClientBaseUrl);

        var link = new Uri(message.Link!);
        Assert.Equal("https", link.Scheme);
        Assert.Equal("app.example.test", link.Host);
        Assert.Equal("/reset-password", link.AbsolutePath);
        Assert.Equal($"?code={Code}", link.Query);
    }

    [Fact]
    public void TheLink_NeverCarriesTheEmailAddress()
    {
        // NGINX logs the query string, and the URL also lands in browser history and mail-scanner
        // logs; the reset page asks for the address instead of writing it into all three.
        var message = PasswordResetMail.Compose(Code, ClientBaseUrl);

        Assert.DoesNotContain("@", message.Link, StringComparison.Ordinal);
        Assert.DoesNotContain("email", message.Link!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheAnchorText_IsDescriptive_NotTheUrlAndNotClickHere()
    {
        var message = PasswordResetMail.Compose(Code, ClientBaseUrl);

        Assert.Contains($"""<a href="{message.Link}">{PasswordResetMail.LinkText}</a>""", message.Body, StringComparison.Ordinal);
        Assert.NotEmpty(PasswordResetMail.LinkText);
        Assert.NotEqual(PasswordResetMail.LinkText, message.Link);
        Assert.DoesNotContain("click here", PasswordResetMail.LinkText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBody_StatesTheOneHourExpiry()
    {
        var message = PasswordResetMail.Compose(Code, ClientBaseUrl);

        Assert.Contains("1 hour", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHtmlEncodedToken_IsDecodedBeforeItGoesIntoTheQuery()
    {
        // Identity HTML-encodes the value for an email body. Base64Url's alphabet needs neither the
        // decode nor the percent-encode in practice, but neither may be assumed away.
        var message = PasswordResetMail.Compose("a+b&amp;c", ClientBaseUrl);

        var code = WebUtility.UrlDecode(new Uri(message.Link!).Query["?code=".Length..]);
        Assert.Equal("a+b&c", code);
    }

    [Fact]
    public void WithoutAClientBaseUrl_TheBodyCarriesTheCodeAndNoLink()
    {
        var message = PasswordResetMail.Compose(Code, clientBaseUrl: null);

        Assert.Null(message.Link);
        Assert.Contains(Code, message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href", message.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task WithNoSmtpHost_TheLinkIsLoggedInDevelopmentAndTesting(string environmentName)
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger, environmentName: environmentName);

        await sender.SendPasswordResetCodeAsync(new ApplicationUser(), "user@example.com", Code);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains($"{ClientBaseUrl}/reset-password?code={Code}", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task WithNoSmtpHost_TheLinkIsNeverLoggedElsewhere(string environmentName)
    {
        // A reset token is a direct account-takeover primitive. Production additionally refuses to
        // start with no SMTP host (EmailOptionsProductionValidationTests), so it should never reach
        // this branch at all — but the gate is what makes that a defence in depth rather than the
        // only thing standing between a misconfiguration and a token in the log.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger, environmentName: environmentName);

        await sender.SendPasswordResetCodeAsync(new ApplicationUser(), "user@example.com", Code);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("no SMTP host", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Code, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reset-password", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeadLinkCallback_SendsNothing()
    {
        // MapIdentityApi never calls SendPasswordResetLinkAsync; it is kept as an explicit no-op so
        // nobody re-implements the reset mail in the overload that is never invoked.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger);

        await sender.SendPasswordResetLinkAsync(
            new ApplicationUser(), "user@example.com", "https://api.test/resetPassword?code=x");

        Assert.Empty(logger.Entries);
    }
}
