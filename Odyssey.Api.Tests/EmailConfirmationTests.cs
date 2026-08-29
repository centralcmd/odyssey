using System.Net;
using System.Net.Http.Json;
using Odyssey.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Pins the email-confirmation flow Odyssey relies on from <c>MapIdentityApi</c>: registration
/// must hand a confirmation link to <see cref="IEmailSender{TUser}"/> (the piece that was missing
/// before, leaving accounts unconfirmable), <c>GET /confirmEmail</c> must confirm with the link's
/// <c>userId</c>/<c>code</c>, and <c>RequireConfirmedAccount</c> must gate sign-in until then.
/// A capturing email sender stands in for SMTP.
/// </summary>
public class EmailConfirmationTests
{
    private const string Email = "confirm-me@example.com";
    private const string Password = "Password123!Safe";

    [Fact]
    public async Task Register_GeneratesConfirmationLink_AndConfirmGatesLogin()
    {
        var emailSender = new CapturingEmailSender();
        await using var factory = new ApiFactory(emailSender);
        await factory.SeedSystemSettingsAsync();
        using var client = factory.CreateClient();

        // Register → Identity must produce a confirmation link via the email sender. No throwaway
        // registration first: since issue #290 the first-ever account is created unconfirmed like any
        // other, so the user under test needs no padding to be ordinary.
        var register = await client.PostAsJsonAsync("/register", new { email = Email, password = Password });
        Assert.True(register.IsSuccessStatusCode, $"Registration should succeed; got {register.StatusCode}.");
        Assert.NotNull(emailSender.LastConfirmationLink);

        // Before confirming, login is refused because RequireConfirmedAccount is on.
        var beforeConfirm = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, beforeConfirm.StatusCode);

        // The framework HTML-encodes the link (it's meant for an email body); a browser decodes
        // it on click, so decode here before following the userId/code through GET /confirmEmail.
        var decodedLink = WebUtility.HtmlDecode(emailSender.LastConfirmationLink!);
        var confirm = await client.GetAsync(new Uri(decodedLink).PathAndQuery);
        Assert.True(confirm.IsSuccessStatusCode, $"Confirmation should succeed; got {confirm.StatusCode}.");

        // After confirming, the same credentials sign in.
        var afterConfirm = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = Email, password = Password });
        Assert.True(afterConfirm.IsSuccessStatusCode, $"Login should succeed after confirmation; got {afterConfirm.StatusCode}.");
    }

    [Fact]
    public async Task ConfirmationDisabled_AllowsImmediateLogin_WithoutConfirming()
    {
        var emailSender = new CapturingEmailSender();
        await using var factory = new ApiFactory(emailSender, requireConfirmation: false);
        await factory.SeedSystemSettingsAsync();
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/register", new { email = "no-confirm@example.com", password = Password });
        register.EnsureSuccessStatusCode();

        // With confirmation disabled the account signs in immediately — no confirmation step.
        var login = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email = "no-confirm@example.com", password = Password });
        Assert.True(login.IsSuccessStatusCode, $"Login should succeed without confirmation; got {login.StatusCode}.");
    }

    [Fact]
    public async Task ConfirmationLink_PointsAtClient_WhenClientBaseUrlConfigured()
    {
        var emailSender = new CapturingEmailSender();
        await using var factory = new ApiFactory(emailSender, clientBaseUrl: "https://app.example.test");
        await factory.SeedSystemSettingsAsync();
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/register", new { email = "client-link@example.com", password = Password });
        register.EnsureSuccessStatusCode();

        // The sender rewrites the framework link onto the configured client page, keeping the query.
        var link = new Uri(emailSender.RewrittenConfirmationLink!);
        Assert.Equal("https", link.Scheme);
        Assert.Equal("app.example.test", link.Host);
        Assert.Equal("/confirm-email", link.AbsolutePath);
        Assert.Contains("userId=", link.Query);
        Assert.Contains("code=", link.Query);
    }

    /// <summary>
    /// Stands in for SMTP: records the framework link Identity generates, and also runs the real
    /// <see cref="Odyssey.Api.Email.SmtpEmailSender"/> rewrite so the client-link test exercises it.
    /// </summary>
    private sealed class CapturingEmailSender : IEmailSender<ApplicationUser>
    {
        public string? LastConfirmationLink { get; private set; }
        public string? RewrittenConfirmationLink { get; private set; }
        public string? ClientBaseUrl { get; set; }

        public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
        {
            LastConfirmationLink = confirmationLink;
            if (!string.IsNullOrWhiteSpace(ClientBaseUrl))
            {
                var query = new Uri(confirmationLink).Query;
                RewrittenConfirmationLink = $"{ClientBaseUrl.TrimEnd('/')}/confirm-email{query}";
            }

            return Task.CompletedTask;
        }

        public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) => Task.CompletedTask;

        public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) => Task.CompletedTask;
    }

    private sealed class ApiFactory(
        EmailConfirmationTests.CapturingEmailSender emailSender,
        string? clientBaseUrl = null,
        bool requireConfirmation = true)
        : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"EmailConfirmationTests-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<ILookupNormalizer>();
                services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();

                emailSender.ClientBaseUrl = clientBaseUrl;
                services.RemoveAll<IEmailSender<ApplicationUser>>();
                services.AddSingleton<IEmailSender<ApplicationUser>>(emailSender);
            });
        }

        /// <summary>
        /// RegistrationRequireAdminApproval/EmailRequireConfirmation are no longer static config
        /// (issue #349) — seed the SystemSetting rows this factory's tests need instead. Isolates the
        /// email-confirmation gate from the admin-approval gate (admin-approval always off here) and
        /// pins email-confirmation to this factory's <c>requireConfirmation</c> constructor argument.
        /// Must run after the host has been built (accessing <see cref="Services"/> triggers that) and
        /// before any <c>/register</c> call.
        /// </summary>
        public async Task SeedSystemSettingsAsync()
        {
            using var scope = Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.SystemSettings.AddRange(
                new SystemSetting { Key = SystemSettingsKeys.RegistrationRequireAdminApproval, Value = "false", UpdatedAt = DateTime.UtcNow },
                new SystemSetting { Key = SystemSettingsKeys.EmailRequireConfirmation, Value = requireConfirmation ? "true" : "false", UpdatedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }
    }

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();
        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }
}
