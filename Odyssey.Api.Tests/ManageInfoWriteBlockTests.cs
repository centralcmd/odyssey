using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The convention and fail-fast startup check behind closing <c>POST /manage/info</c> (issue #246), plus
/// the two email-change messages. The end-to-end behaviour is in <see cref="EmailChangeTests"/>.
/// </summary>
public class ManageInfoWriteBlockTests
{
    [Fact]
    public void OnlyThePost_IsBlocked()
    {
        var builder = new FakeConventionBuilder();
        builder.BlockManageInfoWrites();
        var post = Route("/manage/info", HttpMethods.Post);
        var get = Route("/manage/info", HttpMethods.Get);
        var twoFactor = Route("/manage/2fa", HttpMethods.Post);

        builder.ApplyTo(post, get, twoFactor);

        Assert.NotNull(post.Metadata.OfType<ManageInfoWriteBlockMetadata>().SingleOrDefault());
        Assert.Single(post.FilterFactories);
        Assert.Empty(get.FilterFactories);
        Assert.Empty(get.Metadata.OfType<ManageInfoWriteBlockMetadata>());
        Assert.Empty(twoFactor.FilterFactories);
    }

    [Fact]
    public void ValidateBlocked_PassesWhenThePostCarriesTheBlock()
    {
        var builder = new FakeConventionBuilder();
        builder.BlockManageInfoWrites();
        var post = Route("/manage/info", HttpMethods.Post);
        builder.ApplyTo(post);

        ManageInfoWriteBlock.ValidateBlocked([post.Build(), Route("/manage/info", HttpMethods.Get).Build()]);
    }

    [Fact]
    public void ValidateBlocked_ThrowsWhenThePostIsReachable()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManageInfoWriteBlock.ValidateBlocked([Route("/manage/info", HttpMethods.Post).Build()]));

        Assert.Contains("reachable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateBlocked_ThrowsWhenTheRouteHasMoved()
    {
        // A rename in MapIdentityApi would leave the new route unblocked and this one absent; passing
        // silently on "nothing to block" is how that would ship.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ManageInfoWriteBlock.ValidateBlocked([Route("/manage/info", HttpMethods.Get).Build()]));

        Assert.Contains("renamed", error.Message, StringComparison.Ordinal);
    }

    // ── The messages ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jane@example.com", "j•••@example.com")]
    [InlineData("a@b.test", "a•••@b.test")]
    [InlineData("no-at-sign", "•••")]
    public void TheNotice_MasksTheNewAddress(string email, string expected) =>
        Assert.Equal(expected, EmailChangeMail.Mask(email));

    [Fact]
    public void TheBodies_EncodeWhatTheyEmbed()
    {
        var notice = EmailChangeMail.NoticeBody("<script>@evil.test");
        var confirmation = EmailChangeMail.ConfirmationBody("https://app.test/confirm-email?a=1&b=\"2\"");

        Assert.DoesNotContain("<script>", notice, StringComparison.Ordinal);
        Assert.Contains("&lt;•••@evil.test", notice, StringComparison.Ordinal);
        Assert.Contains("a=1&amp;b=&quot;2&quot;", confirmation, StringComparison.Ordinal);
    }

    // ── The sender ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TheNotice_IsSentEvenToAThrottledAddress()
    {
        // The per-recipient budget can be spent anonymously through /forgotPassword; a throttled notice
        // could be suppressed by exactly the person it warns about. The unreachable host is the probe: an
        // attempted delivery logs an error, a skipped one logs nothing.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger, new ClosedThrottle(), smtpHost: SmtpEmailSenderTestHarness.UnreachableHost);

        await sender.SendChangeNoticeAsync("owner@example.com", "moved@example.com");

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains(EmailChangeMail.NoticeSubject, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheConfirmation_RespectsTheThrottle()
    {
        // It goes to an address the caller names, so it must not be a way around the mail-bombing bound.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger, new ClosedThrottle(), smtpHost: SmtpEmailSenderTestHarness.UnreachableHost);

        await sender.SendChangeConfirmationAsync("moved@example.com", "https://api.test/confirmEmail?userId=1&code=x");

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TheConfirmationLink_IsRewrittenOntoTheClientPage()
    {
        // No relay: the composed link is logged, which is what makes it observable here.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(logger);

        await sender.SendChangeConfirmationAsync(
            "moved@example.com", "https://api.test/confirmEmail?userId=1&code=x&changedEmail=moved%40example.com");

        Assert.Contains(logger.Entries, entry => entry.Message.Contains(
            $"{SmtpEmailSenderTestHarness.ClientBaseUrl}/confirm-email?userId=1&code=x&changedEmail=moved%40example.com",
            StringComparison.Ordinal));
    }

    private static RouteEndpointBuilder Route(string pattern, string method)
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), order: 0);
        builder.Metadata.Add(new HttpMethodMetadata([method]));
        return builder;
    }

    private sealed class ClosedThrottle : IEmailSendThrottle
    {
        public bool TryAcquire(
            string recipient, int limit, int windowMinutes, int maxTrackedRecipients, ReadOnlyMemory<byte> hashKey) =>
            false;
    }
}
