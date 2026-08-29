using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The per-recipient half of the mail-abuse defence (issue #393). The per-IP limiter cannot see the
/// recipient, so this is the only thing standing between a rotating-IP source and an unbounded
/// mailbomb at one address — and it has to do that without leaking, through either the response or
/// the logs, whether that address belongs to a real account.
/// </summary>
public class EmailSendThrottleTests
{
    private const string Recipient = "victim@example.com";

    /// <summary>
    /// The tracked-address ceiling every test but the flood one passes (issue #434 key 14). It is an
    /// argument now, not a constant on the throttle, because both of its read sites take it from the
    /// caller's single per-send snapshot.
    /// </summary>
    private const int MaxTracked = 20_000;

    [Fact]
    public void SendsWithinTheLimit_AreAllowed()
    {
        var (throttle, _, _) = CreateThrottle();

        for (var send = 1; send <= 3; send++)
        {
            Assert.True(throttle.TryAcquire(Recipient, limit: 3, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        }
    }

    [Fact]
    public void TheSendAfterTheLimit_IsRejected()
    {
        var (throttle, _, _) = CreateThrottle();

        for (var send = 1; send <= 3; send++)
        {
            throttle.TryAcquire(Recipient, limit: 3, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey);
        }

        Assert.False(throttle.TryAcquire(Recipient, limit: 3, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
    }

    [Fact]
    public void CaseAndWhitespaceVariants_ShareOneBucket()
    {
        // Without normalization the limit is bypassed by shifting the case of a single character.
        var (throttle, _, _) = CreateThrottle();

        Assert.True(throttle.TryAcquire("user@example.com", limit: 2, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        Assert.True(throttle.TryAcquire("  USER@Example.com ", limit: 2, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        Assert.False(throttle.TryAcquire("User@EXAMPLE.COM", limit: 2, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
    }

    [Fact]
    public void DifferentRecipients_GetTheirOwnBudgets()
    {
        var (throttle, _, _) = CreateThrottle();

        Assert.True(throttle.TryAcquire("one@example.com", limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        Assert.True(throttle.TryAcquire("two@example.com", limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        Assert.False(throttle.TryAcquire("one@example.com", limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
    }

    [Fact]
    public void TheBudgetRefills_OnceTheWindowElapses()
    {
        var (throttle, time, _) = CreateThrottle();

        Assert.True(throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        Assert.False(throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));

        time.Advance(TimeSpan.FromMinutes(61));

        Assert.True(throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
    }

    [Fact]
    public void ARejection_LogsAHashedRecipientAndNeverTheAddress()
    {
        // An email address is PII; these logs are shipped and retained like any other, so the digest
        // is what an operator correlates repeat offenders by.
        var (throttle, _, logger) = CreateThrottle();

        throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey);
        throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey);

        var rejection = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(Recipient, rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            EmailSendThrottle.HashRecipient(TestKey.Span, Recipient), rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHash_IsStableAcrossNormalizationVariants()
    {
        Assert.Equal(
            EmailSendThrottle.HashRecipient(TestKey.Span, EmailSendThrottle.Normalize("User@Example.com ")),
            EmailSendThrottle.HashRecipient(TestKey.Span, EmailSendThrottle.Normalize("user@example.com")));
    }

    [Fact]
    public void TheHash_IsKeyed_SoLogHoldersCannotReverseItByEnumeratingAddresses()
    {
        // The whole point of hashing here is to keep addresses from the audience that holds the
        // logs. An unkeyed digest of a guessable address is reversible offline in seconds, so two
        // deployments with different keys must not produce the same digest for the same address.
        Assert.NotEqual(
            EmailSendThrottle.HashRecipient(Key("key-one").Span, Recipient),
            EmailSendThrottle.HashRecipient(Key("key-two").Span, Recipient));
    }

    [Fact]
    public void ALimitChange_TakesEffectOnTheNextSend()
    {
        // The limit is the caller's argument now rather than something this class reads, so what has to
        // hold is that the SAME counter state is judged against whatever limit arrives next: an
        // operator raising the ceiling after a misfire must not need a restart, and must not have to
        // wait for the recipient's window to roll over either.
        var (throttle, _, _) = CreateThrottle();

        Assert.True(throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
        Assert.False(throttle.TryAcquire(Recipient, limit: 1, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));

        Assert.True(throttle.TryAcquire(Recipient, limit: 5, windowMinutes: 60, maxTrackedRecipients: MaxTracked, recipientHashKey: TestKey));
    }

    [Fact]
    public void AFloodOfDistinctAddresses_FailsOpenRatherThanGrowingUnbounded()
    {
        // The dictionary is capped: past the ceiling an unseen address is allowed through (and the
        // flood logged) rather than evicting a live counter. Dropping a real user's password reset
        // is the worse failure.
        var (throttle, _, logger) = CreateThrottle();

        // A small ceiling rather than the shipped 20,000: the property under test is what happens AT
        // capacity, and the ceiling is a parameter now, so filling a table of 32 proves exactly the same
        // thing in a fraction of the time.
        const int smallCeiling = 32;
        for (var recipient = 0; recipient < smallCeiling; recipient++)
        {
            Assert.True(throttle.TryAcquire(
                $"flood-{recipient}@example.com", limit: 1, windowMinutes: 60, maxTrackedRecipients: smallCeiling, recipientHashKey: TestKey));
        }

        Assert.True(throttle.TryAcquire(
            "one-too-many@example.com", limit: 1, windowMinutes: 60, maxTrackedRecipients: smallCeiling, recipientHashKey: TestKey));
        Assert.True(throttle.TryAcquire(
            "one-too-many@example.com", limit: 1, windowMinutes: 60, maxTrackedRecipients: smallCeiling, recipientHashKey: TestKey));
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The throttle under test. It takes neither a limit nor a hash key: both are arguments to
    /// <c>TryAcquire</c> (issue #421 Wave 2 for the limits, issue #445 Wave 3 for the key), because
    /// they live in the database and the compare-and-increment runs inside a <c>lock</c> where a read
    /// cannot be awaited — the caller reads one snapshot and passes it in.
    ///
    /// <para>
    /// The per-process fallback that applies when no key is stored moved out with it, to
    /// <c>EmailRecipientHashKey</c>; <see cref="MigratedSecretConsumerTests"/> covers that behaviour and
    /// the three read states now.
    /// </para>
    /// </summary>
    private static (EmailSendThrottle Throttle, FakeTimeProvider Time, CapturingLogger<EmailSendThrottle> Logger)
        CreateThrottle()
    {
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger<EmailSendThrottle>();

        return (new EmailSendThrottle(time, logger), time, logger);
    }

    private static readonly ReadOnlyMemory<byte> TestKey = Key("test-key");

    private static ReadOnlyMemory<byte> Key(string value) =>
        System.Text.Encoding.UTF8.GetBytes(value);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }
}
