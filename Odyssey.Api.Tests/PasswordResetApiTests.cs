using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Testing.Logging;
using Xunit;
using Odyssey.Core.Email;

namespace Odyssey.Api.Tests;

/// <summary>
/// The self-service password-reset flow over its real HTTP surface (issue #405). Odyssey adds no
/// endpoint of its own here — <c>MapIdentityApi</c> maps <c>/forgotPassword</c> and
/// <c>/resetPassword</c> — so what these tests pin is the contract the client is written against and
/// the three things Odyssey does own: the composed link, the completion log, and the fact that none
/// of it discloses whether an address is registered.
///
/// <para>
/// Two hosts serve the class, one per service shape: <see cref="SharedHost"/> (the real sender, which logs the link)
/// and <see cref="RecordingHost"/> (a recording sender). Both are returned to their seeded transport, with no log
/// lines and no recorded sends, before every test (<see cref="InitializeAsync"/>). Their stores are not emptied, so
/// every test signs up addresses of its own — which also keeps each test inside its own window of the per-recipient
/// send throttle, an in-process singleton. A test that replaces the throttle, or changes a setting other than the
/// transport, keeps a host of its own.
/// </para>
/// </summary>
public class PasswordResetApiTests(PasswordResetApiTests.SharedHost sharedHost, PasswordResetApiTests.RecordingHost recordingHost)
    : IClassFixture<PasswordResetApiTests.SharedHost>, IClassFixture<PasswordResetApiTests.RecordingHost>, IAsyncLifetime
{
    private const string Password = "Password123!Safe";
    private const string NewPassword = "Renewed987!Passphrase";
    private const string ClientBaseUrl = "https://app.example.test";

    /// <summary>This test's registered address: one per test, so no two tests share an account or a throttle window.</summary>
    private readonly string registered = UniqueAddress("registered");

    private static string UniqueAddress(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    public async Task InitializeAsync()
    {
        await sharedHost.ResetAsync();
        await recordingHost.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ForgotPassword_HandsTheCodeToTheSender_AndNeverTheDeadLinkCallback()
    {
        var factory = recordingHost;
        var sender = factory.Recorder;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/forgotPassword", new { email = registered });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, sender.CodeCalls);
        Assert.Equal(0, sender.LinkCalls);
        Assert.NotNull(sender.LastResetCode);
    }

    [Fact]
    public async Task ForgotPassword_ForAnUnknownAddress_SendsNothing()
    {
        var factory = recordingHost;
        var sender = factory.Recorder;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/forgotPassword", new { email = UniqueAddress("nobody") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, sender.CodeCalls);
    }

    [Fact]
    public async Task ForgotPassword_ForAnUnconfirmedAccount_SendsNothing()
    {
        // Identity refuses both endpoints for an unconfirmed address — otherwise registering an
        // address you don't control would be a route to taking it over. Such a user is served by the
        // existing resend-confirmation flow instead.
        var factory = recordingHost;
        var sender = factory.Recorder;
        var unconfirmed = UniqueAddress("unconfirmed");
        await CreateUserAsync(factory, unconfirmed, confirmed: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/forgotPassword", new { email = unconfirmed });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, sender.CodeCalls);
    }

    [Fact]
    public async Task TheResponse_IsIdenticalAcrossRegisteredUnregisteredUnconfirmedAndThrottled()
    {
        // The whole point of the flow: nothing a caller can observe may differ between an address
        // that exists and one that doesn't. A status-code check alone would miss a differing header or
        // body, so all three are compared.
        // A host of its own: the throttle is a replaced service.
        var throttle = new SelectiveThrottle("throttled@example.com");
        await using var factory = new ApiFactory(
            throttle: throttle,
            // A refused connection rather than an unreachable name: the registered case really does
            // attempt delivery here, and this keeps that attempt fast and deterministic.
            smtpHost: "127.0.0.1",
            smtpPort: 1);
        await CreateUserAsync(factory, registered);
        await CreateUserAsync(factory, "throttled@example.com");
        await CreateUserAsync(factory, "unconfirmed@example.com", confirmed: false);
        using var client = factory.CreateClient();

        var registeredResponse = await client.PostAsJsonAsync("/forgotPassword", new { email = registered });
        var unregistered = await client.PostAsJsonAsync("/forgotPassword", new { email = "nobody@example.com" });
        var unconfirmed = await client.PostAsJsonAsync("/forgotPassword", new { email = "unconfirmed@example.com" });
        var throttled = await client.PostAsJsonAsync("/forgotPassword", new { email = "throttled@example.com" });

        Assert.Contains("throttled@example.com", throttle.Seen);
        foreach (var other in new[] { unregistered, unconfirmed, throttled })
        {
            Assert.Equal(registeredResponse.StatusCode, other.StatusCode);
            Assert.Equal(await registeredResponse.Content.ReadAsStringAsync(), await other.Content.ReadAsStringAsync());
            Assert.Equal(Comparable(registeredResponse), Comparable(other));
        }
    }

    [Fact]
    public async Task TheEmailedLink_CompletesAReset_AndRetiresTheOldPassword()
    {
        // The full chain, through the real SmtpEmailSender: Identity's token → the composed client
        // link → the code the client would read out of the query → /resetPassword.
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory, registered);
        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True(await LoginAsync(factory, registered, NewPassword), "The new password should sign in.");
        Assert.False(await LoginAsync(factory, registered, Password), "The old password should be refused.");
    }

    [Fact]
    public async Task TheEmailedLink_PointsAtTheClientResetPage_WithoutTheAddress()
    {
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = registered });

        var link = new Uri(LoggedLink(factory));
        Assert.Equal("app.example.test", link.Host);
        Assert.Equal("/reset-password", link.AbsolutePath);
        Assert.Contains("code=", link.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(registered, link.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutAClientBaseUrl_TheCodeStillReachesTheUser()
    {
        var factory = recordingHost;
        var sender = factory.Recorder;
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.EmailClientBaseUrl, string.Empty);
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/forgotPassword", new { email = registered });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = PasswordResetMail.Compose(sender.LastResetCode!, clientBaseUrl: null);
        Assert.Null(message.Link);
        Assert.Contains(sender.LastResetCode!, message.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The empirical source of truth for the error key the client branches on: a rejected token comes
    /// back as a <c>ValidationProblemDetails</c> whose <c>errors</c> dictionary is keyed by
    /// <see cref="IdentityError.Code"/>.
    /// </summary>
    [Fact]
    public async Task ATamperedCode_IsRejectedWithTheInvalidTokenErrorKey()
    {
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory, registered);
        var response = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code + "tampered", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidToken", await ErrorKeysAsync(response));
        Assert.False(await LoginAsync(factory, registered, NewPassword), "A rejected reset must not change the password.");
    }

    [Fact]
    public async Task AUsedCode_IsRejectedTheSecondTime()
    {
        // Single-use falls out of the security-stamp rotation a successful reset performs — the token
        // embeds the stamp it was minted against.
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory, registered);
        var first = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = NewPassword });
        var second = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = "Another765!Passphrase" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.True(await LoginAsync(factory, registered, NewPassword), "The first reset's password still stands.");
    }

    [Fact]
    public async Task TwoOutstandingCodes_BothStayValidUntilOneIsUsed()
    {
        // "Send again" issues an independent token, and validity is bound to the account's stamp
        // rather than to a single issued token — so both links work until one of them is redeemed,
        // which is the correct behaviour for the two-tab and delayed-delivery cases. Single-use here
        // means single-use-per-successful-reset, not single-outstanding-link.
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var first = await RequestCodeAsync(client, factory, registered);
        var second = await RequestCodeAsync(client, factory, registered);

        var usingSecond = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = second, newPassword = NewPassword });
        var usingFirst = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = first, newPassword = "Another765!Passphrase" });

        Assert.Equal(HttpStatusCode.OK, usingSecond.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, usingFirst.StatusCode);
    }

    [Fact]
    public async Task APolicyViolatingPassword_IsRejected_AndLeavesThePasswordAlone()
    {
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory, registered);
        var response = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = "Short1!aa" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var keys = await ErrorKeysAsync(response);
        Assert.DoesNotContain("InvalidToken", keys);
        Assert.NotEmpty(keys);
        Assert.True(await LoginAsync(factory, registered, Password), "The old password must still work.");
    }

    [Fact]
    public async Task ALockedOutAccount_CanReset_ButStillCannotSignIn()
    {
        // A reset must not double as a lockout-clearing side channel, so LockoutEnd is left alone and
        // the subsequent sign-in still fails until it elapses.
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();
        var code = await RequestCodeAsync(client, factory, registered);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(registered))!;
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
        }

        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(await LoginAsync(factory, registered, NewPassword), "Lockout still bars the sign-in.");
    }

    [Fact]
    public async Task ATwoFactorAccount_StillFacesTheChallengeAfterAReset()
    {
        // A future "sign the user in for convenience" change here would silently become a 2FA bypass.
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(registered))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }

        using var client = factory.CreateClient();
        var code = await RequestCodeAsync(client, factory, registered);
        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using var loginClient = factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync(
            "/login?useCookies=true", new { email = registered, password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Contains("RequiresTwoFactor", await login.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExhaustingTheRecipientThrottle_ChangesNothingTheCallerSees_AndStopsSending()
    {
        // A host of its own: the per-recipient limit it lowers is a store-wide setting.
        await using var factory = new ApiFactory(smtpHost: "127.0.0.1", smtpPort: 1);

        // The per-recipient limit moved into the settings store (issue #421 Wave 2), so setting it in
        // configuration would assert against a key nothing reads — and this test would pass three
        // sends while claiming to prove the throttle stops at one.
        await SystemSettingsSeed.SetAsync(factory.Services, SystemSettingsKeys.EmailPerRecipientLimit, "1");
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var responses = new List<HttpResponseMessage>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            responses.Add(await client.PostAsJsonAsync("/forgotPassword", new { email = registered }));
        }

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        // A refused connection is what an *attempted* delivery looks like here, so counting those
        // errors counts sends: the second and third requests must not have produced one.
        var attempted = factory.Logs
            .ForCategory(typeof(SmtpEmailSender).FullName!)
            .Count(entry => entry.Level == LogLevel.Error);
        Assert.Equal(1, attempted);
    }

    [Fact]
    public async Task ACompletedReset_LogsTheUserId_AndNeitherTheAddressNorTheToken()
    {
        var factory = sharedHost;
        var userId = await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory, registered);
        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var entry = Assert.Single(CompletionLines(factory));
        Assert.Contains(userId, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(registered, entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedReset_LogsNoCompletion()
    {
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var code = await RequestCodeAsync(client, factory, registered);
        var response = await client.PostAsJsonAsync(
            "/resetPassword", new { email = registered, resetCode = code + "tampered", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(CompletionLines(factory));
    }

    [Fact]
    public async Task TheOtherIdentityEndpoints_LogNoResetCompletion()
    {
        // MapIdentityApi hands back one convention builder for its whole group, so a filter added to
        // that builder would fire on every Identity route — this is the assertion that the filter is
        // attached to /resetPassword alone.
        var factory = sharedHost;
        await CreateUserAsync(factory, registered);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/login?useCookies=true", new { email = registered, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        // GET, since issue #246 closed the POST (it answers 405 before any handler runs).
        var manage = await client.GetAsync("/manage/info");
        Assert.Equal(HttpStatusCode.OK, manage.StatusCode);
        var register = await client.PostAsJsonAsync(
            "/register", new { email = UniqueAddress("fresh"), password = Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        Assert.Empty(CompletionLines(factory));
    }


    private static IEnumerable<LogEntry> CompletionLines(ApiFactory factory) =>
        factory.Logs.ForCategory(typeof(PasswordResetLogging).FullName!)
            .Where(entry => entry.Message.Contains("Password reset completed", StringComparison.Ordinal));

    /// <summary>Everything about a response a caller could compare, minus what legitimately varies.</summary>
    private static string Comparable(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Retry-After"))
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}: {string.Join(",", header.Value)}");
        return string.Join("\n", headers);
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name)];
    }

    /// <summary>
    /// Requests a reset and reads the code back out of the emailed link — the same value the client
    /// would take off the query string, so the tests exercise the composition rather than the token
    /// Identity happened to generate.
    /// </summary>
    private static async Task<string> RequestCodeAsync(HttpClient client, ApiFactory factory, string email)
    {
        var response = await client.PostAsJsonAsync("/forgotPassword", new { email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var query = new Uri(LoggedLink(factory)).Query;
        return Uri.UnescapeDataString(query["?code=".Length..]);
    }

    /// <summary>
    /// The most recent link the sender logged. With no SMTP host configured the Testing host logs the
    /// composed link instead of delivering it — which is exactly the local-dev affordance this feature
    /// relies on, so reading it here also proves it works.
    /// </summary>
    private static string LoggedLink(ApiFactory factory)
    {
        var matches = factory.Logs
            .ForCategory(typeof(SmtpEmailSender).FullName!)
            .Select(entry => Regex.Match(entry.Message, @"https?://\S+/reset-password\?code=\S+"))
            .Where(match => match.Success)
            .ToList();

        Assert.NotEmpty(matches);
        return matches[^1].Value;
    }

    private static async Task<string> CreateUserAsync(ApiFactory factory, string email, bool confirmed = true)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = confirmed,
            LockoutEnabled = true,
        };

        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));

        // The require-admin-approval gate disables every newly added account, with no first-user
        // exemption since issue #290, so a fixture user is created unable to sign in unless the lockout
        // is cleared here — never what these tests are trying to express.
        user.LockoutEnd = null;
        await users.UpdateAsync(user);

        // Without an acceptance row the legal gate (issue #354) answers authenticated endpoints with a
        // 451, which would make the /manage/info leg of the filter-scoping test measure the wrong thing.
        await LegalTestData.AcceptAllAsync(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), user.Id);
        return user.Id;
    }

    private static async Task<bool> LoginAsync(ApiFactory factory, string email, string password)
    {
        // A fresh client per attempt: the cookie container is per-client, so a previous sign-in must
        // not colour the next one.
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/login?useCookies=true", new { email, password });
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Records what Identity asks the sender to deliver, without composing or sending anything.
    /// </summary>
    public sealed class RecordingEmailSender : IEmailSender<ApplicationUser>
    {
        public int CodeCalls { get; private set; }

        public int LinkCalls { get; private set; }

        public string? LastResetCode { get; private set; }

        public void Reset()
        {
            CodeCalls = 0;
            LinkCalls = 0;
            LastResetCode = null;
        }

        public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
            Task.CompletedTask;

        public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
        {
            LinkCalls++;
            return Task.CompletedTask;
        }

        public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
        {
            CodeCalls++;
            LastResetCode = resetCode;
            return Task.CompletedTask;
        }
    }

    /// <summary>Refuses one address and allows every other, so one request in a set is throttled.</summary>
    private sealed class SelectiveThrottle(string blocked) : IEmailSendThrottle
    {
        public List<string> Seen { get; } = [];

        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey)
        {
            Seen.Add(emailAddress);
            return !string.Equals(emailAddress, blocked, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The mail TRANSPORT is seeded as settings rows now, not passed as configuration (issue #8), so
    /// what used to be two dictionary entries is two constructor parameters instead.
    ///
    /// <para>
    /// The defaults preserve exactly what this suite has always relied on: <strong>no SMTP host</strong>,
    /// under which the Testing host logs the composed link rather than delivering it — which is how
    /// every test here reads the reset code back — and a client base URL, because the link's shape is
    /// what most of them assert on.
    /// </para>
    /// </summary>
    /// <summary>The class's host with the real sender: no relay, so the composed link is logged.</summary>
    public sealed class SharedHost() : ApiFactory();

    /// <summary>The class's host with a recording sender, for what Identity hands the sender.</summary>
    public sealed class RecordingHost() : ApiFactory(emailSender: new RecordingEmailSender())
    {
        public RecordingEmailSender Recorder => (RecordingEmailSender)EmailSender!;
    }

    public class ApiFactory(
        IEmailSender<ApplicationUser>? emailSender = null,
        IEmailSendThrottle? throttle = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        string smtpHost = "",
        int? smtpPort = null,
        string clientBaseUrl = ClientBaseUrl)
        : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"PasswordResetApiTests-{Guid.NewGuid()}";

        public RecordingLoggerProvider Logs { get; } = new();

        protected IEmailSender<ApplicationUser>? EmailSender => emailSender;

        /// <summary>
        /// Returns a host shared across tests to what it was built with: the transport this factory seeds (a test may
        /// have rewritten a row), no captured log lines and no recorded sends. Accounts stay; each test makes its own.
        /// </summary>
        public async Task ResetAsync()
        {
            await SystemSettingsSeed.SetTransportAsync(Services, smtpHost, smtpPort, clientBaseUrl);
            Logs.Clear();
            (emailSender as RecordingEmailSender)?.Reset();
        }

        /// <summary>
        /// Seeds the transport once, on the host this factory builds, before any client can issue a
        /// request. A per-test <c>await</c> would work equally well but would have to be repeated at
        /// every call site and silently forgotten at one — and a forgotten seed here does not fail, it
        /// makes the send short-circuit and the test pass for the wrong reason.
        /// </summary>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            SystemSettingsSeed
                .SetTransportAsync(host.Services, smtpHost, smtpPort, clientBaseUrl)
                .GetAwaiter()
                .GetResult();

            return host;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                    // The transport is not configuration any more (issue #8) — see CreateHost.
                    ["Email:FromAddress"] = "no-reply@odyssey.test",
                    // Generous per-IP limits — the rate limiter has its own tests, and every request
                    // here shares one partition key.
                    ["RateLimiting:Identity:PermitLimit"] = "1000",
                    ["RateLimiting:IdentityEmail:PermitLimit"] = "1000",
                };

                if (configuration is not null)
                {
                    foreach (var entry in configuration)
                    {
                        settings[entry.Key] = entry.Value;
                    }
                }

                config.AddInMemoryCollection(settings);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<ILookupNormalizer>();
                services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();
                services.AddSingleton<ILoggerProvider>(Logs);

                if (emailSender is not null)
                {
                    services.RemoveAll<IEmailSender<ApplicationUser>>();
                    services.AddSingleton(emailSender);
                }

                if (throttle is not null)
                {
                    services.RemoveAll<IEmailSendThrottle>();
                    services.AddSingleton(throttle);
                }
            });
        }
    }

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();
        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }
}
