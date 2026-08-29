using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Api.Email;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Guards the configuration contract for <see cref="EmailOptions"/>: when no SMTP settings are
/// supplied the options must still bind (so resolving <see cref="SmtpEmailSender"/> during
/// <c>/register</c> never throws), and the sender must quietly no-op rather than fail. This pins
/// the fix for an AppHost run that forwarded empty <c>Email__*</c> values — an empty
/// <c>Email:SmtpPort</c> fails to bind to the non-nullable int and 500s registration.
/// </summary>
public class EmailOptionsBindingTests
{
    [Fact]
    public void EmailOptions_BindToDefaults_WhenNoEmailConfigPresent()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;

        Assert.Equal(587, options.SmtpPort);
        Assert.Equal(string.Empty, options.SmtpHost);
    }

    [Fact]
    public async Task SmtpEmailSender_NoOps_WhenSmtpHostMissing()
    {
        // No SystemSettings row seeded: EmailRequireConfirmation falls back to its documented
        // default (true, issue #349), so the confirmation-disabled short-circuit doesn't fire either.
        using var provider = CreateProvider();
        var sender = new SmtpEmailSender(
            Options.Create(new EmailOptions()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AllowAllEmailSendThrottle(),
            new StubEmailRecipientHashKey(),
            new StubHostEnvironment("Testing"),
            NullLogger<SmtpEmailSender>.Instance);

        // No SMTP host configured: must complete without throwing (registration must not 500).
        await sender.SendConfirmationLinkAsync(new ApplicationUser(), "user@example.com", "https://x/confirmEmail?userId=1&code=2");
    }

    [Fact]
    public async Task SmtpEmailSender_SkipsSending_WhenConfirmationDisabled()
    {
        using var provider = CreateProvider();
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.SystemSettings.Add(new SystemSetting { Key = SystemSettingsKeys.EmailRequireConfirmation, Value = "false", UpdatedAt = DateTime.UtcNow });
            context.SaveChanges();
        }

        var logger = new CapturingLogger<SmtpEmailSender>();
        // A host is set, so a non-skipping path would attempt an SMTP connection; with confirmation
        // disabled it must short-circuit before touching SMTP.
        var sender = new SmtpEmailSender(
            Options.Create(new EmailOptions { SmtpHost = "smtp.example.test" }),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AllowAllEmailSendThrottle(),
            new StubEmailRecipientHashKey(),
            new StubHostEnvironment("Testing"),
            logger);

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), "user@example.com", "https://x/confirmEmail?userId=1&code=2");

        Assert.Contains(logger.Messages, m => m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("no SMTP host", StringComparison.OrdinalIgnoreCase));
    }

    // SmtpEmailSender takes IServiceScopeFactory, not OdysseyContext, directly (see its class
    // remarks) — so the fake DI container it opens scopes against must be the SAME container a
    // seeding scope writes through, or the two would land on unrelated InMemory stores. The database
    // name is computed ONCE, outside the configuration delegate: AddDbContext re-invokes that
    // delegate for every scope's DbContextOptions, so a Guid.NewGuid() call inside it would hand each
    // scope a different, unrelated database.
    private static ServiceProvider CreateProvider()
    {
        var databaseName = $"EmailOptionsBindingTests_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<OdysseyContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        // Every key NotSet — the unauthenticated relay these tests have always exercised (issue #445).
        services.AddSingleton<ISecretSettingsReader>(new StubSecretSettingsReader());
        return services.BuildServiceProvider();
    }

    private sealed class AllowAllEmailSendThrottle : IEmailSendThrottle
    {
        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey) => true;
    }
}
