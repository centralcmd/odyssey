using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The five migrated credentials at their consuming code (issue #445 §16).
///
/// <para>
/// The rule every test here circles is AC 12: <strong>an <c>Unreadable</c> row never causes the
/// consuming code to read the configuration value.</strong> It is asserted the strongest way
/// available — the configuration properties those consumers would have read no longer exist, so a
/// regression is a compile error rather than a test failure — and then again behaviourally, with a
/// distinctive sentinel that must not appear anywhere in the outcome or the log.
/// </para>
/// </summary>
public class MigratedSecretConsumerTests
{
    /// <summary>
    /// A value no code should ever produce on its own. If it turns up in a header, a log line or an
    /// error message, something read a configured fallback.
    /// </summary>
    private const string Sentinel = "sentinel-must-never-appear-0f1e2d3c";

    // ── Wave 1: FileAnalysis:ApiKey ─────────────────────────────────────────────────────────────

    /// <summary>AC 1. A stored key is attached as <c>x-api-key</c>.</summary>
    [Fact]
    public async Task AStoredApiKey_IsAttachedToTheRequest()
    {
        var reader = new StubSecretSettingsReader().Found(SecretSettingKeys.FileAnalysisApiKey, "sk-ant-first");
        var (client, inner) = RecordingClientFor(reader);

        await client.PostAsync("https://provider.test/v1/messages", new StringContent("{}"));

        Assert.Equal("sk-ant-first", Assert.Single(inner.LastRequest!.Headers.GetValues("x-api-key")));
    }

    /// <summary>
    /// AC 2 — the property the <c>DelegatingHandler</c> refactor exists to deliver. The
    /// <c>DefaultRequestHeaders</c> approach it replaced was evaluated once at client construction, so
    /// this could not have held under it at any price.
    /// </summary>
    [Fact]
    public async Task ARotatedApiKey_BindsOnTheNextRequest_WithNoRestart()
    {
        var reader = new StubSecretSettingsReader().Found(SecretSettingKeys.FileAnalysisApiKey, "sk-ant-first");
        var (client, inner) = RecordingClientFor(reader);

        await client.PostAsync("https://provider.test/v1/messages", new StringContent("{}"));
        Assert.Equal("sk-ant-first", Assert.Single(inner.LastRequest!.Headers.GetValues("x-api-key")));

        // The same client instance, the same handler instance — only the stored value changed.
        reader.Found(SecretSettingKeys.FileAnalysisApiKey, "sk-ant-rotated");
        await client.PostAsync("https://provider.test/v1/messages", new StringContent("{}"));

        Assert.Equal("sk-ant-rotated", Assert.Single(inner.LastRequest!.Headers.GetValues("x-api-key")));
    }

    /// <summary>
    /// AC 3 + AC 12. An unreadable row fails closed: the request is never sent, the exception names a
    /// credential problem, and the sentinel — standing in for a configured <c>FileAnalysis__ApiKey</c>
    /// still present in the environment — appears nowhere.
    /// </summary>
    [Fact]
    public async Task AnUnreadableApiKey_FailsClosed_AndNeverFallsBackToConfiguration()
    {
        var reader = new StubSecretSettingsReader().Unreadable(SecretSettingKeys.FileAnalysisApiKey);
        var logger = new CapturingLogger<FileAnalysisApiKeyHandler>();
        var (client, inner) = RecordingClientFor(reader, logger);

        var exception = await Assert.ThrowsAsync<Odyssey.Core.Finance.FileAnalysisCredentialException>(
            () => client.PostAsync("https://provider.test/v1/messages", new StringContent("{}")));

        Assert.Null(inner.LastRequest);
        Assert.Contains("cannot be decrypted", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(Sentinel, StringComparison.Ordinal));

        // …and the credential exception is still a provider error, so every existing catch keeps working.
        Assert.IsAssignableFrom<Odyssey.Core.Finance.FileAnalysisProviderException>(exception);
    }

    /// <summary>
    /// An absent row is HEALTHY, not degraded: the request goes out with no key, the provider rejects
    /// it, and the job is recorded failed — byte-identical to the pre-migration behaviour with
    /// <c>FileAnalysis:ApiKey</c> left empty.
    /// </summary>
    [Fact]
    public async Task AnUnsetApiKey_SendsWithoutTheHeader_RatherThanThrowing()
    {
        var (client, inner) = RecordingClientFor(new StubSecretSettingsReader());

        await client.PostAsync("https://provider.test/v1/messages", new StringContent("{}"));

        Assert.NotNull(inner.LastRequest);
        Assert.False(inner.LastRequest!.Headers.Contains("x-api-key"));
    }

    /// <summary>
    /// AC 4. The key does not survive a cross-origin redirect.
    ///
    /// <para>
    /// Two mechanisms, and the test drives both. The primary handler is built with
    /// <c>AllowAutoRedirect = false</c>, so a <c>3xx</c> is returned rather than followed and no second
    /// request is ever made — the .NET default would have followed it, and .NET strips only
    /// <c>Authorization</c> across origins, so a custom <c>x-api-key</c> would have travelled with the
    /// whole document to a host nobody configured. The handler additionally strips any
    /// <c>x-api-key</c> already on a request before attaching its own, so a re-sent message cannot
    /// accumulate a stale value.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheApiKeyDoesNotFollowARedirect_AndIsNeverAttachedTwice()
    {
        var reader = new StubSecretSettingsReader().Found(SecretSettingKeys.FileAnalysisApiKey, "sk-ant-first");
        var inner = new RecordingHandler
        {
            Respond = () => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://attacker.test/v1/messages") },
            },
        };
        var client = ClientFor(reader, inner: inner);

        var response = await client.PostAsync("https://provider.test/v1/messages", new StringContent("{}"));

        // Returned, not followed: exactly one request, and it went to the configured host.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(1, inner.Requests);
        Assert.Equal("provider.test", inner.LastRequest!.RequestUri!.Host);

        // A request arriving with a stale key carries exactly one value afterwards — the current one.
        var stale = new HttpRequestMessage(HttpMethod.Post, "https://provider.test/v1/messages");
        stale.Headers.TryAddWithoutValidation("x-api-key", Sentinel);
        await client.SendAsync(stale);

        Assert.Equal(["sk-ant-first"], inner.LastRequest!.Headers.GetValues("x-api-key"));
    }

    // ── Wave 2: Email:Username + Email:Password ─────────────────────────────────────────────────

    /// <summary>
    /// AC 8. With both halves unset, the send proceeds unauthenticated — today's behaviour, and a
    /// legitimate configuration for a relay that accepts unauthenticated mail on a trusted network.
    /// The observable is that delivery is ATTEMPTED at all: the host is unreachable, so it fails at
    /// the transport rather than being skipped before it.
    /// </summary>
    [Fact]
    public async Task WithNeitherHalfStored_TheSendIsAttemptedUnauthenticated()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger, smtpHost: SmtpEmailSenderTestHarness.UnreachableHost);

        var delivery = await sender.SendResetLinkAsync("user@example.com", "code");

        Assert.Equal(PasswordResetLinkDelivery.Failed, delivery);
        Assert.Contains(logger.Messages, message =>
            message.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AC 7 + AC 12. Either half unreadable means the send is logged and SKIPPED — no unauthenticated
    /// attempt, no connection, and no trace of a configured value.
    /// </summary>
    [Theory]
    [InlineData(SecretSettingKeys.EmailUsername)]
    [InlineData(SecretSettingKeys.EmailPassword)]
    public async Task WithEitherHalfUnreadable_TheSendIsSkipped(string unreadableKey)
    {
        var secrets = new StubSecretSettingsReader()
            .Found(SecretSettingKeys.EmailUsername, "relay-user")
            .Found(SecretSettingKeys.EmailPassword, "relay-password");
        secrets.Unreadable(unreadableKey);

        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger, smtpHost: SmtpEmailSenderTestHarness.UnreachableHost, secrets: secrets);

        var delivery = await sender.SendResetLinkAsync("user@example.com", "code");

        Assert.Equal(PasswordResetLinkDelivery.NotConfigured, delivery);
        Assert.Contains(logger.Messages, message =>
            message.Contains("SMTP credential", StringComparison.OrdinalIgnoreCase));

        // Skipped BEFORE the transport: an unreachable host would otherwise have logged its own failure.
        Assert.DoesNotContain(logger.Messages, message =>
            message.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The pair rule. A username stored beside an unset password is half a credential, and there is
    /// nothing useful to do with it — authenticating would fail, and sending unauthenticated would use
    /// an identity the relay is not expecting. So it is skipped, in both directions.
    /// </summary>
    [Theory]
    [InlineData(SecretSettingKeys.EmailUsername)]
    [InlineData(SecretSettingKeys.EmailPassword)]
    public async Task WithOnlyOneHalfStored_TheSendIsSkipped(string storedKey)
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger,
            smtpHost: SmtpEmailSenderTestHarness.UnreachableHost,
            secrets: new StubSecretSettingsReader().Found(storedKey, "half-a-credential"));

        Assert.Equal(
            PasswordResetLinkDelivery.NotConfigured,
            await sender.SendResetLinkAsync("user@example.com", "code"));
    }

    /// <summary>
    /// AC 6. With both halves stored the send is ATTEMPTED — it gets past the credential gate and
    /// fails at the (unreachable) transport, which is the observable difference from the skip paths
    /// above. Asserting the <c>AuthenticateAsync</c> arguments themselves would need a real relay; what
    /// this pins is the branch that reaches it, and that it is reached only when both halves resolve.
    /// </summary>
    [Fact]
    public async Task WithBothHalvesStored_TheSendReachesTheTransport()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger,
            smtpHost: SmtpEmailSenderTestHarness.UnreachableHost,
            secrets: new StubSecretSettingsReader()
                .Found(SecretSettingKeys.EmailUsername, "relay-user")
                .Found(SecretSettingKeys.EmailPassword, "relay-password"));

        Assert.Equal(
            PasswordResetLinkDelivery.Failed,
            await sender.SendResetLinkAsync("user@example.com", "code"));
        Assert.Contains(logger.Messages, message =>
            message.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AC 15. Nothing echoes a stored credential — not the outcome, not any log line — across a full
    /// send cycle with both halves stored as sentinels.
    /// </summary>
    [Fact]
    public async Task ASendCycle_NeverLogsTheStoredCredential()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = SmtpEmailSenderTestHarness.Create(
            logger,
            smtpHost: SmtpEmailSenderTestHarness.UnreachableHost,
            secrets: new StubSecretSettingsReader()
                .Found(SecretSettingKeys.EmailUsername, Sentinel + "-user")
                .Found(SecretSettingKeys.EmailPassword, Sentinel + "-password"));

        await sender.SendResetLinkAsync("user@example.com", "code");

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(Sentinel, StringComparison.Ordinal));
    }

    // ── Wave 3: Email:RecipientHashKey ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC 10. An unset key falls back to a per-process key and emits the existing warning — the
    /// message is asserted verbatim on its distinguishing phrase, because "byte-identical to today's
    /// behaviour" is the criterion.
    /// </summary>
    [Fact]
    public async Task AnUnsetHashKey_UsesAPerProcessKey_AndSaysSo()
    {
        var logger = new CapturingLogger<EmailRecipientHashKey>();
        var first = HashKeyFor(new StubSecretSettingsReader(), logger);
        var second = HashKeyFor(new StubSecretSettingsReader());

        var firstKey = (await first.ResolveAsync()).ToArray();
        var firstAgain = (await first.ResolveAsync()).ToArray();
        var secondKey = (await second.ResolveAsync()).ToArray();

        // Stable WITHIN a process — otherwise digests would not correlate at all…
        Assert.Equal(firstKey, firstAgain);
        // …and different ACROSS processes, which is exactly the documented limitation.
        Assert.NotEqual(firstKey, secondKey);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("per-process hash key", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 11. An unreadable key logs an ERROR and falls back to the per-process key. The level and the
    /// wording are the whole point: both states end at the same key, so the log is the only thing that
    /// tells an administrator their rotation silently broke log correlation rather than succeeding.
    /// </summary>
    [Fact]
    public async Task AnUnreadableHashKey_LogsAnError_AndIsDistinguishableFromUnset()
    {
        var logger = new CapturingLogger<EmailRecipientHashKey>();
        var provider = HashKeyFor(
            new StubSecretSettingsReader().Unreadable(SecretSettingKeys.EmailRecipientHashKey), logger);

        Assert.False((await provider.ResolveAsync()).IsEmpty);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("could not be decrypted", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("per-process hash key", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>A stored key is the one used, and it keys the digests the throttle writes.</summary>
    [Fact]
    public async Task AStoredHashKey_IsTheOneTheThrottleDigestsWith()
    {
        var provider = HashKeyFor(
            new StubSecretSettingsReader().Found(SecretSettingKeys.EmailRecipientHashKey, "stored-key"));

        var key = (await provider.ResolveAsync()).ToArray();

        Assert.Equal(
            EmailSendThrottle.HashRecipient(
                System.Text.Encoding.UTF8.GetBytes("stored-key"), "victim@example.com"),
            EmailSendThrottle.HashRecipient(key, "victim@example.com"));
    }

    /// <summary>
    /// AC 15 for this consumer: the key itself never reaches the log, in any of the three states. The
    /// value is a credential like any other even though it only keys a digest.
    /// </summary>
    [Fact]
    public async Task TheHashKeyProvider_NeverLogsTheKey()
    {
        var logger = new CapturingLogger<EmailRecipientHashKey>();
        var provider = HashKeyFor(
            new StubSecretSettingsReader().Found(SecretSettingKeys.EmailRecipientHashKey, Sentinel), logger);

        await provider.ResolveAsync();

        Assert.DoesNotContain(logger.Messages, message => message.Contains(Sentinel, StringComparison.Ordinal));
    }

    // ── Cross-cutting ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 12, in its strongest available form. The configuration properties a fallback would have read
    /// are GONE — so the rule is enforced by the compiler, not by vigilance. A test is the wrong tool
    /// for "nobody will write <c>?? configured</c>"; a missing property is the right one.
    ///
    /// <para>
    /// The three <c>EmailOptions</c> members this used to name went further still: issue #8 moved the
    /// last four <c>Email:*</c> values into the settings store and deleted the whole class, so there is
    /// no type left to reflect over. That is asserted below by name rather than dropped — a
    /// reintroduced <c>EmailOptions</c> would be a resurrected fallback path, and the point of naming
    /// it is that the next person to add one has to delete this line first.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRetiredConfigurationProperties_NoLongerExist()
    {
        Assert.Null(typeof(SmtpEmailSender).Assembly.GetType("Odyssey.Api.Email.EmailOptions"));
        Assert.Null(typeof(Odyssey.Core.Finance.FileAnalysisOptions).GetProperty("ApiKey"));
        Assert.Null(typeof(Odyssey.Api.Legal.LegalOptions).GetProperty("PseudonymizationSecret"));
    }

    /// <summary>
    /// The handler is registered OUTSIDE the resilience pipeline, so an unreadable credential fails
    /// once instead of being retried twice at the far end of a circuit breaker.
    ///
    /// <para>
    /// A source-lint, because the property is one of ordering in <c>Program.cs</c> and
    /// <c>IHttpClientFactory</c> exposes no way to inspect a built pipeline's handler order. Nothing
    /// depends on it today — <c>AddStandardResilienceHandler()</c>'s default predicates match
    /// <c>HttpRequestException</c> and timeouts, not a custom
    /// <see cref="Odyssey.Core.Finance.FileAnalysisCredentialException"/> — which is exactly why it is worth
    /// pinning: a future retry predicate widened to "any exception" would silently start retrying a
    /// credential fault, and the ordering is the thing that would still make it harmless.
    /// </para>
    /// </summary>
    [Fact]
    public void TheApiKeyHandler_IsRegisteredOutsideTheResiliencePipeline()
    {
        var program = RepositoryRoot.ReadAllText(
            System.IO.Path.Combine("Odyssey.Api", "Program.cs"));

        var handler = program.IndexOf(
            ".AddHttpMessageHandler<Odyssey.Api.SystemSettings.FileAnalysisApiKeyHandler>()",
            StringComparison.Ordinal);
        var resilience = program.IndexOf(".AddStandardResilienceHandler()", StringComparison.Ordinal);

        Assert.True(handler >= 0, "The api-key handler is no longer registered on the typed client.");
        Assert.True(resilience >= 0, "The resilience handler registration moved; re-pin this lint.");

        // Registered earlier means further OUT: the credential read happens before the retry ladder,
        // and throwing there ends the call rather than starting one.
        Assert.True(
            handler < resilience,
            "FileAnalysisApiKeyHandler is registered after AddStandardResilienceHandler, which puts it "
            + "INSIDE the retry pipeline — an unreadable credential would be retried.");
    }

    /// <summary>
    /// AC 13. Every migrated descriptor declares a <c>Kind</c>, and the two derivation keys are the two
    /// that cannot be re-issued at a provider — the recipient hash key and the pseudonymization
    /// secret. The classification is what drives the Clear confirmation's copy, so getting it backwards
    /// would tell an administrator a permanent loss is recoverable.
    /// </summary>
    [Fact]
    public void TheTwoDerivationKeys_AreTheOnesNoProviderCanReissue()
    {
        var kinds = SecretSettingsRegistry.AllUnfiltered.ToDictionary(d => d.Key, d => d.Kind, StringComparer.Ordinal);

        Assert.Equal(SecretKind.RotatableCredential, kinds[SecretSettingKeys.FileAnalysisApiKey]);
        Assert.Equal(SecretKind.RotatableCredential, kinds[SecretSettingKeys.EmailUsername]);
        Assert.Equal(SecretKind.RotatableCredential, kinds[SecretSettingKeys.EmailPassword]);
        Assert.Equal(SecretKind.DerivationKey, kinds[SecretSettingKeys.EmailRecipientHashKey]);
        Assert.Equal(SecretKind.DerivationKey, kinds[SecretSettingKeys.LegalPseudonymizationSecret]);

        Assert.Equal(
            [SecretSettingKeys.EmailRecipientHashKey, SecretSettingKeys.LegalPseudonymizationSecret],
            SecretSettingsRegistry.AllUnfiltered
                .Where(descriptor => descriptor.Kind == SecretKind.DerivationKey)
                .Select(descriptor => descriptor.Key));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static (HttpClient Client, RecordingHandler Inner) RecordingClientFor(
        StubSecretSettingsReader reader, CapturingLogger<FileAnalysisApiKeyHandler>? logger = null)
    {
        var inner = new RecordingHandler();
        return (ClientFor(reader, logger, inner), inner);
    }

    /// <summary>
    /// The real handler over a recording inner handler. Not the application's own client: this test is
    /// about what the handler attaches, and standing up the resilience pipeline would only add retries
    /// that make "exactly one request" harder to observe.
    /// </summary>
    private static HttpClient ClientFor(
        StubSecretSettingsReader reader,
        CapturingLogger<FileAnalysisApiKeyHandler>? logger = null,
        RecordingHandler? inner = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretSettingsReader>(reader);

        var handler = new FileAnalysisApiKeyHandler(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger ?? new CapturingLogger<FileAnalysisApiKeyHandler>())
        {
            InnerHandler = inner ?? new RecordingHandler(),
        };

        return new HttpClient(handler);
    }

    private static EmailRecipientHashKey HashKeyFor(
        StubSecretSettingsReader reader, CapturingLogger<EmailRecipientHashKey>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretSettingsReader>(reader);

        return new EmailRecipientHashKey(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger ?? new CapturingLogger<EmailRecipientHashKey>());
    }

    /// <summary>
    /// Records what actually reached the wire. It never follows anything: a redirect response is
    /// returned to the caller, which is the behaviour <c>AllowAutoRedirect = false</c> produces on the
    /// real client's primary handler.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public int Requests { get; private set; }

        public Func<HttpResponseMessage> Respond { get; init; } = () => new(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests++;
            return Task.FromResult(Respond());
        }
    }
}
