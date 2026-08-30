using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The send path's read of the four transport settings (issue #8 §5.9, §11.1 — ACs 10, 12, 13, 13b).
///
/// <para>
/// <strong>What is actually under test is a distinction, not a value.</strong> The sender must tell an
/// ABSENT row (healthy: mail is not configured, log and skip) from an UNUSABLE one (degraded: fail
/// closed, substitute nothing). <c>SystemSettingsReader</c> cannot make that distinction by design —
/// it resolves both to the compiled default so a display bound "degrades instead of disappearing" —
/// which is why these four keys have a reader of their own. Every assertion below fails if that reader
/// is swapped back for the defaulting one.
/// </para>
///
/// <para>
/// The sender is driven directly rather than through a host: an assertion about which of two log lines
/// was written is about this class's own branching, and a <c>WebApplicationFactory</c> would add a
/// request pipeline that decides none of it.
/// </para>
/// </summary>
public class EmailTransportReadPathTests
{
    private const string Recipient = "user@example.com";
    private const string ConfirmationLink = "https://api.test/confirmEmail?userId=1&code=2";

    [Fact]
    public async Task AnEmptyHostIsUnconfigured_NotDegraded()
    {
        // AC 10. The row is present and empty — the state the migration seeds and the state an
        // administrator clearing the field produces.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(logger, rows: new()
        {
            [SystemSettingsKeys.EmailSmtpHost] = string.Empty,
        });

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.Contains(logger.Messages, m => m.Contains("no SMTP host configured", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("cannot be used", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoRowAtAllIsAlsoUnconfigured()
    {
        // The pre-seed database. Absent and empty must behave identically — an empty dictionary cannot
        // say which of the two happened, which is why the reader returns an explicit per-key state.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(logger, rows: []);

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.Contains(logger.Messages, m => m.Contains("no SMTP host configured", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("cannot be used", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnUnparseableStartTlsFlagFailsClosed_AndNeverResolvesToTheCompiledDefault()
    {
        // AC 13, and the sharpest case in the suite. SystemSettingsReader.GetBoolAsync would resolve
        // "yes" to the compiled `true` and connect with STARTTLS — which reads as the safe direction
        // until the administrator's stored value was `false` for an implicit-TLS relay on 465.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(logger, rows: new()
        {
            [SystemSettingsKeys.EmailSmtpHost] = "smtp.example.test",
            [SystemSettingsKeys.EmailUseStartTls] = "yes",
        });

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        var refusal = Assert.Single(
            logger.Messages, m => m.Contains("cannot be used", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SystemSettingsKeys.EmailUseStartTls, refusal, StringComparison.Ordinal);

        // Never the value — a stored value can carry material a restore planted.
        Assert.DoesNotContain("yes", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnparseablePortFailsClosed()
    {
        // AC 13, the int half. Note the asymmetry with the next test: unparseable is refused, whereas
        // out-of-range is clamped. A port that parses is a usable number.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(logger, rows: new()
        {
            [SystemSettingsKeys.EmailSmtpHost] = "smtp.example.test",
            [SystemSettingsKeys.EmailSmtpPort] = "not-a-port",
        });

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        var refusal = Assert.Single(
            logger.Messages, m => m.Contains("cannot be used", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SystemSettingsKeys.EmailSmtpPort, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99999")]
    public async Task AnOutOfRangePortIsClamped_NotRefused(string stored)
    {
        // AC 8's read half. "0" is the below-floor case and is deliberately NOT the unparseable one —
        // conflating them is the mistake SystemSettingsBounds' own remarks warn about.
        //
        // The observable is that the send is ATTEMPTED: the host is unreachable, so delivery fails at
        // the relay rather than being skipped before a socket is opened. A refusal would have logged
        // "cannot be used" and never reached MailKit.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(logger, rows: new()
        {
            [SystemSettingsKeys.EmailSmtpHost] = SmtpEmailSenderTestHarness.UnreachableHost,
            [SystemSettingsKeys.EmailSmtpPort] = stored,
        });

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("cannot be used", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, m => m.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AStoredClientBaseUrlTheRuleRejectsFailsClosed()
    {
        // AC 13b. An http:// PUBLIC host planted by a restore or a hand edit never passed the PUT
        // path's validator, and this is the field issue #8 §10.2 calls the weakest point in the
        // feature — so the send refuses rather than composing a reset link against it.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(logger, rows: new()
        {
            [SystemSettingsKeys.EmailSmtpHost] = SmtpEmailSenderTestHarness.UnreachableHost,
            [SystemSettingsKeys.EmailClientBaseUrl] = "http://attacker.example.test",
        });

        await sender.SendPasswordResetCodeAsync(new ApplicationUser(), Recipient, "token");

        var refusal = Assert.Single(
            logger.Messages, m => m.Contains("cannot be used", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SystemSettingsKeys.EmailClientBaseUrl, refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example.test", refusal, StringComparison.OrdinalIgnoreCase);

        // And nothing was attempted: a refusal happens before any socket is opened.
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AChangedHostBindsOnTheNextSend_WithNoRestartAndNoCacheWait()
    {
        // AC 12. The same sender instance — MapIdentityApi resolves IEmailSender once from the root
        // provider and caches it for the app's lifetime, so "the next send" has to mean the next call
        // on THIS object, not the next process.
        var logger = new CapturingLogger<SmtpEmailSender>();
        var provider = CreateProvider();
        Write(provider, new() { [SystemSettingsKeys.EmailSmtpHost] = string.Empty });
        var sender = CreateSender(logger, provider);

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);
        Assert.Contains(logger.Messages, m => m.Contains("no SMTP host configured", StringComparison.OrdinalIgnoreCase));

        // Entries, not Messages: Messages is a projection built fresh on every read, so clearing it
        // clears a copy and the assertions below would still see the first send's lines.
        logger.Entries.Clear();
        Write(provider, new() { [SystemSettingsKeys.EmailSmtpHost] = SmtpEmailSenderTestHarness.UnreachableHost });

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("no SMTP host configured", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, m => m.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────
    //
    // Not SmtpEmailSenderTestHarness: that seeds exactly two rows and takes the values as parameters,
    // which is right for the tests that only care about the host. These need to write arbitrary — and
    // deliberately malformed — rows, which is the whole subject here.

    private static SmtpEmailSender CreateSender(
        CapturingLogger<SmtpEmailSender> logger, Dictionary<string, string> rows)
    {
        var provider = CreateProvider();
        Write(provider, rows);
        return CreateSender(logger, provider);
    }

    private static SmtpEmailSender CreateSender(
        CapturingLogger<SmtpEmailSender> logger, ServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AllowAllThrottle(),
            new StubEmailRecipientHashKey(),
            new StubHostEnvironment("Testing"),
            logger);

    // The database name is computed ONCE, outside the configuration delegate: AddDbContext re-invokes
    // that delegate per scope, so a Guid.NewGuid() inside it would hand each scope its own unrelated
    // InMemory store and the seed would land in none of them.
    private static ServiceProvider CreateProvider()
    {
        var databaseName = $"EmailTransportReadPathTests_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<ISecretSettingsReader>(new StubSecretSettingsReader());
        return services.BuildServiceProvider();
    }

    private static void Write(IServiceProvider provider, Dictionary<string, string> rows)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        foreach (var (key, value) in rows)
        {
            var existing = context.SystemSettings.FirstOrDefault(setting => setting.Key == key);
            if (existing is null)
            {
                context.SystemSettings.Add(new SystemSetting
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Value = value;
            }
        }

        context.SaveChanges();
    }

    private sealed class AllowAllThrottle : IEmailSendThrottle
    {
        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey) => true;
    }
}
