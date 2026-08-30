using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The dedicated, shorter-lived reset-token provider (issue #405). Identity's default
/// <c>DataProtectorTokenProvider</c> lives for a day and is shared with email confirmation, so the
/// reset lifespan could not be cut without cutting confirmation links too — hence a second, named
/// provider. These tests pin both halves: reset tokens expire, and confirmation's did not change.
/// </summary>
public class PasswordResetTokenProviderTests
{
    private const string Password = "Password123!Safe";
    private const string NewPassword = "Renewed987!Passphrase";
    private const string Email = "resetter@example.com";

    /// <summary>
    /// The lifespan both timing tests configure. Long enough that a token minted and redeemed back
    /// to back survives a loaded machine — a one-second window made the control flaky when the whole
    /// suite ran in parallel — and short enough to wait out in one bounded delay.
    /// </summary>
    private static readonly TimeSpan ShortLifespan = TimeSpan.FromSeconds(10);

    [Fact]
    public void ThePasswordResetProvider_IsRegisteredAndSelected()
    {
        using var factory = new ApiFactory();

        var identity = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.Equal(PasswordResetTokenProviderOptions.ProviderName, identity.Tokens.PasswordResetTokenProvider);
        Assert.True(identity.Tokens.ProviderMap.ContainsKey(PasswordResetTokenProviderOptions.ProviderName));
    }

    [Fact]
    public void TheDefaultProvider_KeepsItsOneDayLifespan()
    {
        // The regression this guards: configuring DataProtectionTokenProviderOptions directly (it is
        // bound as a non-named IOptions<T>) would have retuned the shared "Default" provider and
        // silently shortened every email-confirmation link along with the reset ones.
        using var factory = new ApiFactory();

        var shared = factory.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        var reset = factory.Services.GetRequiredService<IOptions<PasswordResetTokenProviderOptions>>().Value;

        Assert.Equal(TimeSpan.FromDays(1), shared.TokenLifespan);
        Assert.Equal(TimeSpan.FromHours(1), reset.TokenLifespan);
        Assert.NotEqual(shared.Name, reset.Name);
    }

    [Fact]
    public async Task AFreshCode_IsAccepted_UnderTheSameShortLifespan()
    {
        // The control for the expiry test below. Without it, that test would stay green even if the
        // named provider were wired up wrongly: a bad purpose string or an unselected provider makes
        // *every* validation fail with the very same 400 the expiry test asserts.
        await using var factory = new ApiFactory(ShortLifespan);
        await CreateUserAsync(factory);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory);
        var response = await client.PostAsJsonAsync(
            "/resetPassword", new { email = Email, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredCode_IsRejected()
    {
        // A single bounded wait, not a poll: DataProtectorTokenProvider<TUser> reads
        // DateTimeOffset.UtcNow directly in both GenerateAsync and ValidateAsync and takes no
        // TimeProvider, so this repo's fake-clock pattern cannot reach it.
        await using var factory = new ApiFactory(ShortLifespan);
        await CreateUserAsync(factory);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory);
        await Task.Delay(ShortLifespan + TimeSpan.FromSeconds(2));
        var response = await client.PostAsJsonAsync(
            "/resetPassword", new { email = Email, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidToken", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<string> RequestCodeAsync(HttpClient client, ApiFactory factory)
    {
        var response = await client.PostAsJsonAsync("/forgotPassword", new { email = Email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var link = factory.Logs
            .ForCategory(typeof(SmtpEmailSender).FullName!)
            .Select(entry => Regex.Match(entry.Message, @"https?://\S+/reset-password\?code=(\S+)"))
            .Last(match => match.Success);

        return Uri.UnescapeDataString(link.Groups[1].Value);
    }

    private static async Task CreateUserAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(
            new ApplicationUser { UserName = Email, Email = Email, EmailConfirmed = true }, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    private sealed class ApiFactory(TimeSpan? tokenLifespan = null) : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"PasswordResetTokenProviderTests-{Guid.NewGuid()}";

        public CapturingLoggerProvider Logs { get; } = new();

        /// <summary>
        /// The client base URL is a settings row now, not configuration (issue #8), and it has to be
        /// written before the first request: these tests read the reset CODE back out of the composed
        /// link in the log, so an unset origin leaves nothing to match.
        ///
        /// <para>
        /// The SMTP host is deliberately left unseeded. Absent means "mail is not configured", which is
        /// exactly the no-relay path this suite depends on — the Testing host logs the link instead of
        /// delivering it.
        /// </para>
        /// </summary>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            SystemSettingsSeed
                .SetTransportAsync(host.Services, host: string.Empty, clientBaseUrl: "https://app.example.test")
                .GetAwaiter()
                .GetResult();

            return host;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                    // No Email:* here any more (issue #8). These tests exercise the token provider, not
                    // delivery, and the no-relay path they rely on is what an unseeded database already
                    // gives: an absent SMTP host row means "mail is not configured", so the Testing host
                    // logs the composed link instead of sending it.
                    ["RateLimiting:Identity:PermitLimit"] = "1000",
                    ["RateLimiting:IdentityEmail:PermitLimit"] = "1000",
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
                services.AddSingleton<ILoggerProvider>(Logs);

                // A second Configure on the same options type, applied after Program.cs's — the later
                // registration wins, which is what lets a test shorten the lifespan to something it
                // can actually wait out.
                if (tokenLifespan is { } lifespan)
                {
                    services.Configure<PasswordResetTokenProviderOptions>(
                        options => options.TokenLifespan = lifespan);
                }
            });
        }
    }
}
