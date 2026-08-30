using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The admin-initiated password reset (issue #406): <c>POST /api/users/{id}/password-reset</c>, the
/// derived <see cref="OdysseyUserManager"/> that clears the flag, and the two exposure points for it.
/// </summary>
public class AdminPasswordResetApiTests
{
    private const string ActorUserId = "admin-actor-id";
    private const string TargetUserId = "reset-target-id";
    private const string TargetEmail = "target@example.com";
    private const string Password = "Password123!Safe";
    private const string NewPassword = "Renewed987!Passphrase";

    [Fact]
    public async Task WithoutUsersUpdate_TheEndpointIsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await MustChangePasswordAsync(factory, TargetUserId));
    }

    [Fact]
    public async Task WithUsersUpdate_TheResetIsAppliedAndReportedAsDelivered()
    {
        // No SMTP host in Testing, so the link is logged rather than relayed — which in that environment
        // IS the delivery mechanism, hence emailDelivered: true.
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dispatch = await response.Content.ReadFromJsonAsync<PasswordResetDispatch>();
        Assert.True(dispatch!.EmailDelivered);
        Assert.True(await MustChangePasswordAsync(factory, TargetUserId));
        Assert.NotEqual(stampBefore, await SecurityStampAsync(factory, TargetUserId));
        Assert.Single(ResetLinks(factory));
    }

    [Fact]
    public async Task AnUnknownTarget_IsNotFound_AndSendsNothing()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/users/nobody/password-reset", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(ResetLinks(factory));
    }

    [Fact]
    public async Task AnUnconfirmedTarget_IsUnprocessable_AndChangesNothing()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail, emailConfirmed: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(await MustChangePasswordAsync(factory, TargetUserId));
        Assert.Empty(ResetLinks(factory));
    }

    [Fact]
    public async Task TheEmailedLink_MatchesTheSelfServiceShape()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var link = new Uri(Assert.Single(ResetLinks(factory)));
        Assert.True(link.IsAbsoluteUri);
        Assert.Equal("/reset-password", link.AbsolutePath);
        Assert.Contains("code=", link.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetEmail, link.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The criterion that catches a Base64Url-encoding mismatch with <c>MapIdentityApi</c>: its
    /// <c>/resetPassword</c> Base64Url-<em>decodes</em> the code it receives, so an admin path that
    /// forgets to encode would mint links only that one endpoint rejects.
    /// </summary>
    [Fact]
    public async Task TheAdminIssuedCode_CompletesAReset_AndRetiresTheOldPassword()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);
        var code = CodeFrom(Assert.Single(ResetLinks(factory)));

        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = TargetEmail, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True(await LoginSucceedsAsync(factory, TargetEmail, NewPassword), "The new password should sign in.");
        Assert.False(await LoginSucceedsAsync(factory, TargetEmail, Password), "The old password should be refused.");
    }

    /// <summary>
    /// Ordering: the stamp is rotated <em>before</em> the token is generated. Generating first would mint a
    /// token the rotation immediately invalidates — a defect no other assertion here would notice, because
    /// the reset would still be applied and the mail would still go out.
    /// </summary>
    [Fact]
    public async Task TheIssuedToken_IsStillValidAfterTheCallReturns()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);
        var code = CodeFrom(Assert.Single(ResetLinks(factory)));

        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = TargetEmail, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [Fact]
    public async Task APreviouslyIssuedResetLink_IsInvalidatedByTheAdminReset()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = TargetEmail });
        var selfServiceCode = CodeFrom(ResetLinks(factory)[^1]);

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = TargetEmail, resetCode = selfServiceCode, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    [Fact]
    public async Task AThrottledRecipient_Is429_AndNothingIsMutated()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.UsersUpdate],
            configureServices: services =>
            {
                services.RemoveAll<IEmailSendThrottle>();
                services.AddSingleton<IEmailSendThrottle>(new StubThrottle { Allow = false });
            });
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(await MustChangePasswordAsync(factory, TargetUserId));
        Assert.Equal(stampBefore, await SecurityStampAsync(factory, TargetUserId));
        Assert.Empty(ResetLinks(factory));
    }

    /// <summary>
    /// The per-recipient budget is consumed exactly once per admin reset. This is what fails if the admin
    /// path is ever rewired onto a permit-acquiring send on top of the service's own acquisition: that
    /// double-decrement would make the second call drop the mail inside the sender while still reporting
    /// <c>emailDelivered: true</c>.
    /// </summary>
    [Fact]
    public async Task ThePerRecipientBudget_IsConsumedOncePerReset()
    {
        var throttle = new CountingThrottle(budget: 3);
        await using var factory = new ApiFactory(
            [PermissionClaims.UsersUpdate],
            configureServices: services =>
            {
                services.RemoveAll<IEmailSendThrottle>();
                services.AddSingleton<IEmailSendThrottle>(throttle);
            });
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            statuses.Add((await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null)).StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            statuses);
        Assert.Equal(4, throttle.Calls);
        Assert.Equal(3, ResetLinks(factory).Count);
    }

    [Fact]
    public async Task WhenTheSendFails_TheResponseSaysSo_AndTheResetStands()
    {
        // A refused connection is a deterministic SMTP failure that resolves fast.
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate])
        {
            SmtpHost = "127.0.0.1",
            SmtpPort = 1,
        };
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dispatch = await response.Content.ReadFromJsonAsync<PasswordResetDispatch>();
        Assert.False(dispatch!.EmailDelivered);

        // The state change is committed, deliberately: the sessions are already revoked and the admin's
        // intent stands, so retrying the whole operation would be wrong.
        Assert.True(await MustChangePasswordAsync(factory, TargetUserId));
        Assert.NotEqual(stampBefore, await SecurityStampAsync(factory, TargetUserId));
    }

    /// <summary>
    /// The admin path really does reuse <see cref="SmtpEmailSender"/>, rather than having grown a parallel
    /// sender: with SMTP unconfigured it takes the same log-the-link path (and inherits the same
    /// Development/Testing-only gate on that logging) as <c>/forgotPassword</c>.
    /// </summary>
    [Fact]
    public async Task TheAdminPath_UsesTheSameSenderAsForgotPassword()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/forgotPassword", new { email = TargetEmail });
        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var senderLines = factory.Logs.ForCategory(typeof(SmtpEmailSender).FullName!).ToList();
        Assert.Equal(2, senderLines.Count(entry => entry.Message.Contains("/reset-password?code=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SelfTargeting_Succeeds_AndGatesTheActingAdmin()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, ActorUserId, "actor@example.com");
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/users/{ActorUserId}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await MustChangePasswordAsync(factory, ActorUserId));
    }

    /// <summary>
    /// The per-actor limit, which is what bounds one admin sweeping the whole user base. Distinct targets
    /// per call on purpose: the per-recipient throttle is tighter by default, so a same-target loop would
    /// trip the wrong limiter and pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task ExceedingThePerActorLimit_Is429_AndTheOverLimitCallMutatesNothing()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.UsersUpdate],
            configuration: new Dictionary<string, string?>
            {
                ["RateLimiting:AdminPasswordReset:PermitLimit"] = "2",
                ["RateLimiting:AdminPasswordReset:WindowSeconds"] = "3600",
            });
        await CreateUserAsync(factory, "sweep-1", "sweep1@example.com");
        await CreateUserAsync(factory, "sweep-2", "sweep2@example.com");
        await CreateUserAsync(factory, "sweep-3", "sweep3@example.com");
        using var client = factory.CreateClient();

        var first = await client.PostAsync("/api/users/sweep-1/password-reset", content: null);
        var second = await client.PostAsync("/api/users/sweep-2/password-reset", content: null);
        var third = await client.PostAsync("/api/users/sweep-3/password-reset", content: null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        // The rejection happens in the rate-limiting middleware, before the action runs at all.
        Assert.False(await MustChangePasswordAsync(factory, "sweep-3"));
        Assert.Equal(2, ResetLinks(factory).Count);

        // ...and it is visible to operators, so bulk abuse surfaces on the admin side before every
        // recipient's inbox does the alerting.
        Assert.Contains(
            factory.Logs.ForCategory(typeof(AdminActionRateLimiting).FullName!),
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(ActorUserId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryResetIsAttributable_WithoutLoggingTheAddressOrTheToken()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);
        var code = CodeFrom(Assert.Single(ResetLinks(factory)));

        var entry = Assert.Single(
            factory.Logs.ForCategory(typeof(UserAdministration.UserAdministrationService).FullName!),
            line => line.Message.Contains("Admin-initiated password reset", StringComparison.Ordinal));

        Assert.Contains(ActorUserId, entry.Message, StringComparison.Ordinal);
        Assert.Contains(TargetUserId, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetEmail, entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUserManager_ResolvesToTheDerivedOne()
    {
        // MapIdentityApi resolves UserManager<ApplicationUser>, not the derived type, so it is *that*
        // service type that has to resolve to OdysseyUserManager — registering the derived type alone
        // would leave every flag-clearing path silently unhooked.
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        using var scope = factory.Services.CreateScope();

        Assert.IsType<OdysseyUserManager>(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    [Fact]
    public async Task CompletingTheEmailedReset_ClearsTheFlag()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);
        var code = CodeFrom(Assert.Single(ResetLinks(factory)));
        Assert.True(await MustChangePasswordAsync(factory, TargetUserId));

        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = TargetEmail, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(await MustChangePasswordAsync(factory, TargetUserId));
    }

    /// <summary>
    /// 2FA enrolment rotates the security stamp too, which is exactly why the flag is cleared from the
    /// password-setting methods rather than inferred from a stamp comparison.
    /// </summary>
    [Fact]
    public async Task EnrollingInTwoFactor_DoesNotClearTheFlag()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();
        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(TargetUserId))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }

        Assert.True(await MustChangePasswordAsync(factory, TargetUserId));
    }

    /// <summary>An email change is the second of the three stamp-rotating operations, and equally must not clear it.</summary>
    [Fact]
    public async Task ChangingTheEmail_DoesNotClearTheFlag()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();
        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(TargetUserId))!;
            var token = await users.GenerateChangeEmailTokenAsync(user, "moved@example.com");
            var result = await users.ChangeEmailAsync(user, "moved@example.com", token);
            Assert.True(result.Succeeded);
        }

        Assert.True(await MustChangePasswordAsync(factory, TargetUserId));
    }

    [Fact]
    public async Task TheFlagIsVisibleToAdmins_OnBothUserReads()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate, PermissionClaims.UsersRead]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();
        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var single = await client.GetFromJsonAsync<ExistingUser>($"/api/users/{TargetUserId}");
        var page = await client.GetFromJsonAsync<PagedResult<ExistingUser>>("/api/users?offset=0&limit=100");

        Assert.True(single!.MustChangePassword);
        Assert.True(Assert.Single(page!.Items, user => user.Id == TargetUserId).MustChangePassword);
    }

    [Fact]
    public async Task TheFlagIsVisibleToTheTarget_OnTheirOwnProfile_AndCannotBeOverposted()
    {
        // The acting principal IS the target here, so the profile read is the target's own.
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate], actorUserId: TargetUserId);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.False(before!.MustChangePassword);

        await client.PostAsync($"/api/users/{TargetUserId}/password-reset", content: null);

        var after = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.True(after!.MustChangePassword);
    }

    [Fact]
    public async Task PuttingTheFlagOnTheProfile_CannotSetIt()
    {
        await using var factory = new ApiFactory([], actorUserId: TargetUserId);
        await CreateUserAsync(factory, TargetUserId, TargetEmail);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/profile", new
        {
            firstName = "Test",
            lastName = "User",
            birthDate = "1990-01-01",
            sex = 1,
            mustChangePassword = true,
        });

        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>();
        Assert.False(profile!.MustChangePassword);
        Assert.False(await MustChangePasswordAsync(factory, TargetUserId));
    }

    private static IReadOnlyList<string> ResetLinks(ApiFactory factory) =>
        factory.Logs
            .ForCategory(typeof(SmtpEmailSender).FullName!)
            .Select(entry => Regex.Match(entry.Message, @"https?://\S+/reset-password\?code=\S+"))
            .Where(match => match.Success)
            .Select(match => match.Value)
            .ToList();

    private static string CodeFrom(string link) =>
        Uri.UnescapeDataString(new Uri(link).Query["?code=".Length..]);

    private static async Task<bool> MustChangePasswordAsync(ApiFactory factory, string userId) =>
        await WithUserAsync(factory, userId, user => user.MustChangePassword);

    private static async Task<string?> SecurityStampAsync(ApiFactory factory, string userId) =>
        await WithUserAsync(factory, userId, user => user.SecurityStamp);

    private static async Task<T> WithUserAsync<T>(ApiFactory factory, string userId, Func<ApplicationUser, T> read)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var user = await context.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
        return read(user);
    }

    private static async Task<bool> LoginSucceedsAsync(ApiFactory factory, string email, string password)
    {
        // A fresh client per attempt: the cookie container is per-client, so a previous sign-in must not
        // colour the next one.
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/login?useCookies=true", new { email, password });
        return response.IsSuccessStatusCode;
    }

    private static async Task CreateUserAsync(
        ApiFactory factory, string id, string email, bool emailConfirmed = true)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            LockoutEnabled = true,
        };

        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));

        // The require-admin-approval gate disables every newly added account, with no first-user
        // exemption since issue #290, so a fixture user is created unable to sign in unless the lockout
        // is cleared here — never what these tests are trying to express.
        user.LockoutEnd = null;
        await users.UpdateAsync(user);
    }

    private sealed class StubThrottle : IEmailSendThrottle
    {
        public bool Allow { get; init; }

        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey) => Allow;
    }

    /// <summary>
    /// A real budget, so "how many permits did one reset consume?" is observable. The budget is the
    /// double's own, deliberately ignoring the caller-supplied limit: these tests are about how many
    /// permits one reset consumes, not about which limit the service resolved.
    /// </summary>
    private sealed class CountingThrottle(int budget) : IEmailSendThrottle
    {
        public int Calls { get; private set; }

        public bool TryAcquire(
            string emailAddress,
            int limit,
            int windowMinutes,
            int maxTrackedRecipients,
            ReadOnlyMemory<byte> recipientHashKey)
        {
            Calls++;
            return Calls <= budget;
        }
    }

    private sealed class ApiFactory(
        IReadOnlyCollection<string> permissions,
        string actorUserId = ActorUserId,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null)
        : OdysseyApiFactory(
            permissions,
            actorUserId,
            Merge(configuration),
            services =>
            {
                services.RemoveAll<ILookupNormalizer>();
                services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();
                configureServices?.Invoke(services);
            })
    {
        public CapturingLoggerProvider Logs { get; } = new();

        /// <summary>The relay this factory seeds. Empty means the link is logged, not delivered.</summary>
        public string SmtpHost { get; init; } = string.Empty;

        public int? SmtpPort { get; init; }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }

        /// <summary>
        /// Seeds the mail transport once, before any client request (issue #8). It is settings rows
        /// now, not configuration — and a forgotten seed does not fail loudly, it makes the send
        /// short-circuit and the test pass for the wrong reason, so it belongs here rather than at
        /// every call site.
        /// </summary>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            SystemSettingsSeed
                .SetTransportAsync(host.Services, SmtpHost, SmtpPort, "https://app.example.test")
                .GetAwaiter()
                .GetResult();

            return host;
        }

        private static IReadOnlyDictionary<string, string?> Merge(IReadOnlyDictionary<string, string?>? overrides)
        {
            var settings = new Dictionary<string, string?>
            {
                // The transport is not configuration any more (issue #8) — see CreateHost. The default
                // is NO SMTP host, under which the Testing host logs the composed link instead of
                // delivering it, which is how these tests read the code back.
                ["Email:FromAddress"] = "no-reply@odyssey.test",
                ["RateLimiting:Identity:PermitLimit"] = "1000",
                ["RateLimiting:IdentityEmail:PermitLimit"] = "1000",
            };

            if (overrides is not null)
            {
                foreach (var entry in overrides)
                {
                    settings[entry.Key] = entry.Value;
                }
            }

            return settings;
        }
    }

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();

        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }
}
