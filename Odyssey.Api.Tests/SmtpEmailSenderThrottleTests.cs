using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// How <see cref="SmtpEmailSender"/> reacts to the per-recipient throttle (issue #393). The
/// unreachable SMTP host is the probe: a send that is attempted logs a delivery error, a send that
/// is skipped logs nothing at all — which is how these tests tell "throttled" from "sent".
/// </summary>
public class SmtpEmailSenderThrottleTests
{
    private const string UnreachableHost = SmtpEmailSenderTestHarness.UnreachableHost;

    private const string ResetCode = "CfDJ8-fake-reset-token";

    [Fact]
    public async Task AThrottledRecipient_IsNotSentTo()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(new StubThrottle { Allow = false }, logger);

        await sender.SendPasswordResetCodeAsync(
            new ApplicationUser(), "victim@example.com", ResetCode);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AnAllowedRecipient_IsSentTo()
    {
        // The mirror of the test above: with the throttle open, delivery is attempted (and fails
        // against the unreachable host), proving the assertion above measures the skip and not the
        // absence of an SMTP attempt in general.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(new StubThrottle { Allow = true }, logger);

        await sender.SendPasswordResetCodeAsync(
            new ApplicationUser(), "user@example.com", ResetCode);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AThrowingThrottle_FailsOpenAndWarns()
    {
        // A broken throttle is an availability problem; a silently dropped reset link is a lockout.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(new StubThrottle { Throw = true }, logger);

        await sender.SendPasswordResetCodeAsync(
            new ApplicationUser(), "user@example.com", ResetCode);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("throttle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TheThrottle_IsNotConsultedWhenNoSmtpHostIsConfigured()
    {
        // The unconfigured-dev path logs the action link so confirmation still works locally; the
        // throttle must not swallow that (nothing is being sent for it to bound).
        var throttle = new StubThrottle { Allow = false };
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(throttle, logger, smtpHost: string.Empty);

        await sender.SendPasswordResetCodeAsync(
            new ApplicationUser(), "user@example.com", ResetCode);

        Assert.Equal(0, throttle.Calls);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("no SMTP host", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The admin-initiated reset's seam acquires <b>no</b> permit: its caller already did, before mutating
    /// anything. Reusing the throttling entry point on top of that acquisition would consume two permits
    /// per reset and — worse — let the second one silently drop the mail after the caller's writes were
    /// committed, while the caller still reported it delivered (issue #406 §5.1).
    /// </summary>
    [Fact]
    public async Task TheResetLinkSeam_DoesNotConsultTheThrottle()
    {
        var throttle = new StubThrottle { Allow = false };
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(throttle, logger);

        var delivery = await sender.SendResetLinkAsync("user@example.com", ResetCode);

        Assert.Equal(0, throttle.Calls);
        // A closed throttle did not stop it: delivery was attempted, and failed against the unreachable host.
        Assert.Equal(PasswordResetLinkDelivery.Failed, delivery);
    }

    [Fact]
    public async Task TheResetLinkSeam_ReportsAnUnconfiguredHostDistinctlyFromAFailure()
    {
        // NotConfigured is the intended development behaviour — the link is logged instead — and the caller
        // reports it to the admin as delivered, because there logging IS the delivery mechanism. Conflating
        // it with Failed would make every dev-stack reset look broken.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(new StubThrottle { Allow = true }, logger, smtpHost: string.Empty);

        var delivery = await sender.SendResetLinkAsync("user@example.com", ResetCode);

        Assert.Equal(PasswordResetLinkDelivery.NotConfigured, delivery);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains("/reset-password?code=", StringComparison.Ordinal));
    }

    private static SmtpEmailSender CreateSender(
        IEmailSendThrottle throttle, ILogger<SmtpEmailSender> logger, string smtpHost = UnreachableHost) =>
        SmtpEmailSenderTestHarness.Create(logger, throttle, smtpHost);

    private sealed class StubThrottle : IEmailSendThrottle
    {
        public bool Allow { get; init; }

        public bool Throw { get; init; }

        public int Calls { get; private set; }

        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey)
        {
            Calls++;
            return Throw ? throw new InvalidOperationException("throttle is broken") : Allow;
        }
    }
}
