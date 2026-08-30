using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The <c>EmailRequireConfirmation</c> short-circuit in
/// <see cref="SmtpEmailSender.SendConfirmationLinkAsync"/> (issue #349).
///
/// <para>
/// <strong>Carried over from the deleted <c>EmailOptionsBindingTests</c>.</strong> That file went
/// when issue #8 removed <c>EmailOptions</c>, and two of its three tests went with it legitimately —
/// they asserted the binding behaviour of a class that no longer exists. This one did not: it is the
/// only place the REAL sender is driven through the confirmation-disabled path.
/// <c>EmailConfirmationTests</c> covers the same outcome, but through a fake
/// <c>IEmailSender&lt;ApplicationUser&gt;</c>, so it would still pass if this branch were deleted.
/// </para>
///
/// <para>
/// The branch matters more after issue #8 than before it. It returns before the transport is read at
/// all, which is what keeps a deployment with confirmation switched off from logging "mail is not
/// configured" on every registration — noise that would train an operator to ignore the one signal
/// that says their relay is broken.
/// </para>
/// </summary>
public class SmtpEmailSenderConfirmationTests
{
    private const string Recipient = "user@example.com";
    private const string ConfirmationLink = "https://api.test/confirmEmail?userId=1&code=2";

    [Fact]
    public async Task WithConfirmationDisabled_TheSendIsSkippedBeforeTheTransportIsConsulted()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();

        // A host IS set, so a sender that did not short-circuit would attempt a real connection and
        // log a delivery failure. That is what makes the absence of those lines meaningful.
        var sender = CreateSender(
            logger, requireConfirmation: false, host: SmtpEmailSenderTestHarness.UnreachableHost);

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.Contains(logger.Messages, m => m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("no SMTP host", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The positive control. Without it the assertion above would still hold if the sender had simply
    /// stopped sending confirmations altogether.
    /// </summary>
    [Fact]
    public async Task WithConfirmationEnabled_TheSendIsAttempted()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(
            logger, requireConfirmation: true, host: SmtpEmailSenderTestHarness.UnreachableHost);

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, m => m.Contains("Failed to send email", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A MISSING row means confirmation is on — its compiled default — so registration on a database
    /// whose seed has not run still sends. Asserted because "absent" and "false" are different states
    /// everywhere else in this feature, and conflating them here would silently disable confirmation.
    /// </summary>
    [Fact]
    public async Task WithNoStoredRow_ConfirmationIsOnByDefault()
    {
        var logger = new CapturingLogger<SmtpEmailSender>();
        var sender = CreateSender(
            logger, requireConfirmation: null, host: SmtpEmailSenderTestHarness.UnreachableHost);

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), Recipient, ConfirmationLink);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    // The database name is computed ONCE, outside the configuration delegate: AddDbContext re-invokes
    // that delegate per scope, so a Guid.NewGuid() inside it would hand each scope its own unrelated
    // InMemory store and the seed would land in none of them.
    private static SmtpEmailSender CreateSender(
        CapturingLogger<SmtpEmailSender> logger, bool? requireConfirmation, string host)
    {
        var databaseName = $"SmtpEmailSenderConfirmationTests_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<ISecretSettingsReader>(new StubSecretSettingsReader());
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

            context.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingsKeys.EmailSmtpHost,
                Value = host,
                UpdatedAt = DateTime.UtcNow,
            });

            if (requireConfirmation is { } value)
            {
                context.SystemSettings.Add(new SystemSetting
                {
                    Key = SystemSettingsKeys.EmailRequireConfirmation,
                    Value = value ? "true" : "false",
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            context.SaveChanges();
        }

        return new SmtpEmailSender(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AllowAllThrottle(),
            new StubEmailRecipientHashKey(),
            new StubHostEnvironment("Testing"),
            logger);
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
