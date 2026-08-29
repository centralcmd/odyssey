using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Api.Email;
using Odyssey.Context;
using Odyssey.Context.Secrets;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Builds a hand-constructed <see cref="SmtpEmailSender"/> for the tests that observe it directly —
/// what it logs, what it composes — rather than through a host.
/// </summary>
public static class SmtpEmailSenderTestHarness
{
    /// <summary>An SMTP host that never resolves, so an attempted delivery fails fast and logs.</summary>
    public const string UnreachableHost = "smtp.invalid.test";

    public const string ClientBaseUrl = "https://app.example.test";

    /// <summary>
    /// The sender under test. <paramref name="smtpHost"/> defaults to empty — the no-SMTP path, where
    /// the composed link is the observable — and <paramref name="environmentName"/> to Development,
    /// where that logging is enabled.
    /// </summary>
    public static SmtpEmailSender Create(
        ILogger<SmtpEmailSender> logger,
        IEmailSendThrottle? throttle = null,
        string smtpHost = "",
        string clientBaseUrl = ClientBaseUrl,
        string environmentName = "Development",
        StubSecretSettingsReader? secrets = null)
    {
        // Its own container: the sender opens scopes against this for the live
        // EmailRequireConfirmation read (see its class remarks) and, since issue #445, for the SMTP
        // credential pair. The stub reader defaults every key to NotSet, which is the unauthenticated
        // relay every existing test in this harness already assumed.
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options =>
            options.UseInMemoryDatabase($"SmtpEmailSenderTestHarness_{Guid.NewGuid()}"));
        services.AddSingleton<ISecretSettingsReader>(secrets ?? new StubSecretSettingsReader());
        var provider = services.BuildServiceProvider();

        return new SmtpEmailSender(
            Options.Create(new EmailOptions
            {
                SmtpHost = smtpHost,
                FromAddress = "no-reply@odyssey.test",
                ClientBaseUrl = clientBaseUrl,
            }),
            provider.GetRequiredService<IServiceScopeFactory>(),
            throttle ?? new AlwaysAllowThrottle(),
            new StubEmailRecipientHashKey(),
            new StubHostEnvironment(environmentName),
            logger);
    }

    private sealed class AlwaysAllowThrottle : IEmailSendThrottle
    {
        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey) => true;
    }
}
