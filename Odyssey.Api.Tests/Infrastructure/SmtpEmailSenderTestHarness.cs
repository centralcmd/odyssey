using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Context;
using Odyssey.Context.Secrets;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Builds a hand-constructed <see cref="SmtpEmailSender"/> for the tests that observe it directly —
/// what it logs, what it composes — rather than through a host.
///
/// <para>
/// Since issue #8 the transport is <em>seeded as settings rows</em>, not passed as options: the sender
/// takes no <c>EmailOptions</c> because that class no longer exists, and every value it reads comes
/// from <c>SystemSettings</c> on the context it opens a scope against. The parameters below are
/// unchanged in meaning, so the calling tests read the same; only the plumbing behind them moved.
/// </para>
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
        // EmailRequireConfirmation read (see its class remarks), for the SMTP credential pair since
        // issue #445, and for the transport since #8. The stub reader defaults every key to NotSet,
        // which is the unauthenticated relay every existing test in this harness already assumed.
        //
        // The database name is computed ONCE, outside the configuration delegate: AddDbContext
        // re-invokes that delegate per scope, so a Guid.NewGuid() inside it would hand each scope a
        // different, unrelated InMemory store — and the seed below would land in none of them.
        var databaseName = $"SmtpEmailSenderTestHarness_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<ISecretSettingsReader>(secrets ?? new StubSecretSettingsReader());
        var provider = services.BuildServiceProvider();

        SeedTransport(provider, smtpHost, clientBaseUrl);

        return new SmtpEmailSender(
            provider.GetRequiredService<IServiceScopeFactory>(),
            throttle ?? new AlwaysAllowThrottle(),
            new StubEmailRecipientHashKey(),
            new StubHostEnvironment(environmentName),
            logger);
    }

    /// <summary>
    /// Writes the transport rows the sender reads (issue #8).
    ///
    /// <para>
    /// Only the two the callers vary are written. The port and the STARTTLS flag are left ABSENT
    /// deliberately, which is the healthy not-configured state the reader resolves to its compiled
    /// default — a row written here would test the seed rather than the read. A test that needs an
    /// unusable value writes it itself.
    /// </para>
    ///
    /// <para>
    /// An empty host is stored as an empty row rather than skipped, because that is what the migration
    /// seeds and what an administrator clearing the field produces; the reader treats an absent row
    /// and an empty one identically, and this exercises the spelling a real database has.
    /// </para>
    /// </summary>
    private static void SeedTransport(IServiceProvider provider, string smtpHost, string clientBaseUrl)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        context.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingsKeys.EmailSmtpHost,
            Value = smtpHost,
            UpdatedAt = DateTime.UtcNow,
        });
        context.SystemSettings.Add(new SystemSetting
        {
            Key = SystemSettingsKeys.EmailClientBaseUrl,
            Value = clientBaseUrl,
            UpdatedAt = DateTime.UtcNow,
        });

        context.SaveChanges();
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
