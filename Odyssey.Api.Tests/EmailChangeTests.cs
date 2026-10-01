using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Odyssey.Api.Email;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #246: <c>POST /manage/info</c> is closed, and the email change it offered without
/// re-authentication lives on <c>POST /api/account/email</c>, which requires the current password, counts
/// failures, rotates the security stamp and notifies the old address. Everything authenticates through the
/// real cookie pipeline, because "does the other session survive?" cannot be asked any other way.
/// </summary>
public class EmailChangeTests
{
    private const string OwnerEmail = "owner@example.com";
    private const string NewEmail = "moved@example.com";
    private const string WrongPassword = "Wrong123!Passphrase";

    // 257 characters: one over the Identity column width the DTO's [StringLength(256)] mirrors.
    private const string LongestValidEmailPlusOne =
        "a234567890123456789012345678901234567890123456789012345678901234@"
        + "b2345678901234567890123456789012345678901234567890123456789012.c2345678901234567890123456789012345678901234567890123456789012.d234567890123456789012345678901234567890123456789012345678.example";

    // ── POST /manage/info is closed ──────────────────────────────────────────

    [Fact]
    public async Task ManageInfoPost_WithNewEmail_Is405_AndSendsNothing()
    {
        var mailer = new RecordingMailer();
        var sender = new RecordingIdentitySender();
        await using var factory = Factory(mailer, sender);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await client.PostAsJsonAsync("/manage/info", new { newEmail = "attacker@example.com" });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains(HttpMethods.Get, response.Content.Headers.Allow);
        Assert.Empty(sender.ConfirmationLinks);
        Assert.Empty(mailer.Confirmations);
        Assert.Equal(OwnerEmail, (await FindAsync(factory, user.Id)).Email);
    }

    [Fact]
    public async Task ManageInfoPost_WithNewPassword_Is405_AndChangesNothing()
    {
        await using var factory = Factory();
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await client.PostAsJsonAsync("/manage/info", new
        {
            oldPassword = PasswordGateFactory.Password,
            newPassword = "Renewed987!Passphrase",
        });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using var stillWorks = await factory.LoginAsync(OwnerEmail);
    }

    [Fact]
    public async Task ManageInfoPost_WithAWrongOldPassword_IsNotAGuessingOracle()
    {
        // The stock handler would answer a wrong oldPassword with a distinguishable 400 and never count it.
        // Blocked, every guess gets the same 405 — there is nothing left to compare.
        await using var factory = Factory();
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var wrong = await client.PostAsJsonAsync("/manage/info", new
        {
            oldPassword = WrongPassword,
            newPassword = "Renewed987!Passphrase",
        });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrong.StatusCode);
        Assert.Equal(0, await factory.AccessFailedCountAsync(user.Id));
    }

    [Fact]
    public async Task ManageInfoGet_IsStillServed()
    {
        // The client resolves its session through GET manage/info; the block must not touch it.
        await using var factory = Factory();
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await client.GetAsync("/manage/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ManageInfoPost_Anonymous_IsStill401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/manage/info", new { newEmail = "attacker@example.com" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void TheBuiltApp_CarriesTheBlock()
    {
        // ValidateBlocked runs at startup and throws; reaching here means it passed. Asserted directly as
        // well so the property under test is visible, not implied by "the factory booted".
        using var factory = Factory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var write = Assert.Single(endpoints.OfType<RouteEndpoint>(), endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, ManageInfoWriteBlock.Route, StringComparison.OrdinalIgnoreCase)
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) == true);
        Assert.NotNull(write.Metadata.GetMetadata<ManageInfoWriteBlockMetadata>());
    }

    // ── POST /api/account/email ──────────────────────────────────────────────

    [Fact]
    public async Task ChangeEmail_WithTheCurrentPassword_Is202_MailsBothAddresses_AndChangesNothingYet()
    {
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(client, NewEmail, PasswordGateFactory.Password);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var confirmation = Assert.Single(mailer.Confirmations);
        Assert.Equal(NewEmail, confirmation.To);
        var notice = Assert.Single(mailer.Notices);
        Assert.Equal(OwnerEmail, notice.To);
        Assert.Equal(NewEmail, notice.NewEmail);

        // Pending, not applied: the sign-in identity moves only when the new mailbox proves itself.
        Assert.Equal(OwnerEmail, (await FindAsync(factory, user.Id)).Email);
    }

    [Fact]
    public async Task OpeningTheConfirmationLink_MovesTheSignInIdentity()
    {
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);
        await RequestAsync(client, NewEmail, PasswordGateFactory.Password);

        // The link targets Identity's own /confirmEmail — the client page forwards the same query there.
        var link = new Uri(Assert.Single(mailer.Confirmations).Link);
        Assert.Equal("/confirmEmail", link.AbsolutePath);
        using var anonymous = factory.CreateClient();
        var confirm = await anonymous.GetAsync(link.PathAndQuery);

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var changed = await FindAsync(factory, user.Id);
        Assert.Equal(NewEmail, changed.Email);
        Assert.Equal(NewEmail, changed.UserName);
        using var newIdentity = await factory.LoginAsync(NewEmail);
    }

    [Fact]
    public async Task ANewerRequest_VoidsTheEarlierLink()
    {
        // The stamp rotates before each token is issued, so only the newest pending change can land.
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        await RequestAsync(client, "first@example.com", PasswordGateFactory.Password);
        await RequestAsync(client, "second@example.com", PasswordGateFactory.Password);

        using var anonymous = factory.CreateClient();
        var stale = await anonymous.GetAsync(new Uri(mailer.Confirmations[0].Link).PathAndQuery);

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(OwnerEmail, (await FindAsync(factory, user.Id)).Email);
    }

    [Fact]
    public async Task ChangingThePasswordAfterwards_CancelsThePendingChange()
    {
        // What the notice tells an owner whose session was misused to do — so it has to be true.
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);
        await RequestAsync(client, NewEmail, PasswordGateFactory.Password);

        var change = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = "Renewed987!Passphrase",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var anonymous = factory.CreateClient();
        var confirm = await anonymous.GetAsync(new Uri(Assert.Single(mailer.Confirmations).Link).PathAndQuery);

        Assert.Equal(HttpStatusCode.Unauthorized, confirm.StatusCode);
        Assert.Equal(OwnerEmail, (await FindAsync(factory, user.Id)).Email);
    }

    [Fact]
    public async Task ARequest_SignsOutOtherSessions_ButKeepsTheCallersOwn()
    {
        await using var factory = Factory(securityStampValidationInterval: TimeSpan.Zero);
        await factory.CreateUserAsync(OwnerEmail);
        using var caller = await factory.LoginAsync(OwnerEmail);
        using var other = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(caller, NewEmail, PasswordGateFactory.Password);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/profile")).StatusCode);
    }

    [Fact]
    public async Task AWrongPassword_Is400_CountsAFailure_AndSendsNothing()
    {
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(client, NewEmail, WrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The current password is incorrect.", await DetailAsync(response));
        Assert.Equal(1, await factory.AccessFailedCountAsync(user.Id));
        Assert.Empty(mailer.Confirmations);
        Assert.Empty(mailer.Notices);
    }

    [Fact]
    public async Task RepeatedWrongPasswords_LockOut_AndThenRefuseTheRightOne()
    {
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        // From the options, not a literal — Program.cs does not configure Lockout, so this is the
        // framework default today and a literal would silently go stale the day someone sets one.
        var threshold = factory.Services.GetRequiredService<IOptions<IdentityOptions>>()
            .Value.Lockout.MaxFailedAccessAttempts;

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < threshold; attempt++)
        {
            last = await RequestAsync(client, NewEmail, WrongPassword);
        }

        Assert.Equal(HttpStatusCode.Locked, last!.StatusCode);
        var afterLockout = await RequestAsync(client, NewEmail, PasswordGateFactory.Password);
        Assert.Equal(HttpStatusCode.Locked, afterLockout.StatusCode);
        Assert.Empty(mailer.Confirmations);
    }

    [Fact]
    public async Task ASuccess_ResetsTheFailureCount()
    {
        await using var factory = Factory();
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);
        await RequestAsync(client, NewEmail, WrongPassword);

        await RequestAsync(client, NewEmail, PasswordGateFactory.Password);

        Assert.Equal(0, await factory.AccessFailedCountAsync(user.Id));
    }

    [Fact]
    public async Task AnAddressAlreadyInUse_Is202_MailsNoLink_ButStillNotifies()
    {
        // Same answer as a free address, so this is no existence oracle; no link, because Identity's
        // /confirmEmail would apply the email and then fail on the user name.
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        await factory.CreateUserAsync("taken@example.com");
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(client, "taken@example.com", PasswordGateFactory.Password);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(mailer.Confirmations);
        Assert.Single(mailer.Notices);
    }

    [Fact]
    public async Task AnAddressThatIsOnlyAnotherAccountsUserName_IsAlsoTreatedAsTaken()
    {
        // The user name is the email in this app, and /confirmEmail sets both — so a clash on the name
        // alone is as unconfirmable as a clash on the email.
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var other = await factory.CreateUserAsync("other@example.com");
        await SetEmailAsync(factory, other.Id, "elsewhere@example.com");
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(client, "other@example.com", PasswordGateFactory.Password);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(mailer.Confirmations);
        Assert.Single(mailer.Notices);
    }

    [Theory]
    [InlineData(OwnerEmail)]
    [InlineData("OWNER@example.com")]
    public async Task TheCurrentAddress_Is400_BeforeThePasswordIsChecked(string sameAddress)
    {
        await using var factory = Factory();
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(client, sameAddress, WrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("This is already your email address.", await DetailAsync(response));
        Assert.Equal(0, await factory.AccessFailedCountAsync(user.Id));
    }

    [Theory]
    [InlineData("not-an-address", PasswordGateFactory.Password)]
    [InlineData(NewEmail, "")]
    [InlineData(LongestValidEmailPlusOne, PasswordGateFactory.Password)]
    public async Task AMalformedBody_Is400(string newEmail, string password)
    {
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var response = await RequestAsync(client, newEmail, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(mailer.Notices);
    }

    [Fact]
    public async Task AnAnonymousCaller_Is401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await RequestAsync(client, NewEmail, PasswordGateFactory.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AGatedSession_CannotChangeTheEmail()
    {
        // A session forced to change its password may be holding a compromised one — the very case in
        // which moving the sign-in email must be impossible. So this endpoint is not exempt.
        var mailer = new RecordingMailer();
        await using var factory = Factory(mailer);
        var user = await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await RequestAsync(client, NewEmail, PasswordGateFactory.Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(mailer.Confirmations);
    }

    [Fact]
    public async Task EmailAndPasswordChanges_ShareOneRateLimitBudget()
    {
        // Both verify the current password, so a separate budget each would double the guesses allowed.
        await using var factory = Factory(
            configuration: new Dictionary<string, string?> { ["RateLimiting:PasswordChange:PermitLimit"] = "1" });
        await factory.CreateUserAsync(OwnerEmail);
        using var client = await factory.LoginAsync(OwnerEmail);

        var password = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = "Renewed987!Passphrase",
        });
        var email = await RequestAsync(client, NewEmail, "Renewed987!Passphrase");

        Assert.Equal(HttpStatusCode.NoContent, password.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, email.StatusCode);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static PasswordGateFactory Factory(
        RecordingMailer? mailer = null,
        RecordingIdentitySender? identitySender = null,
        TimeSpan? securityStampValidationInterval = null,
        IReadOnlyDictionary<string, string?>? configuration = null) =>
        new(configuration, securityStampValidationInterval, services =>
        {
            services.RemoveAll<IEmailChangeMailer>();
            services.AddSingleton<IEmailChangeMailer>(mailer ?? new RecordingMailer());
            if (identitySender is not null)
            {
                services.RemoveAll<IEmailSender<ApplicationUser>>();
                services.AddSingleton<IEmailSender<ApplicationUser>>(identitySender);
            }
        });

    private static Task<HttpResponseMessage> RequestAsync(HttpClient client, string newEmail, string password) =>
        client.PostAsJsonAsync("/api/account/email", new { newEmail, currentPassword = password });

    private static async Task<ApplicationUser> FindAsync(PasswordGateFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByIdAsync(userId))!;
    }

    private static async Task SetEmailAsync(PasswordGateFactory factory, string userId, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        user.Email = email;
        user.NormalizedEmail = users.NormalizeEmail(email);
        await users.UpdateAsync(user);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }

    private sealed record Sent(string To, string Link, string NewEmail);

    private sealed class RecordingMailer : IEmailChangeMailer
    {
        private readonly ConcurrentQueue<Sent> confirmations = new();
        private readonly ConcurrentQueue<Sent> notices = new();

        public IReadOnlyList<Sent> Confirmations => confirmations.ToArray();

        public IReadOnlyList<Sent> Notices => notices.ToArray();

        public Task SendChangeConfirmationAsync(string newEmail, string confirmationLink)
        {
            confirmations.Enqueue(new Sent(newEmail, confirmationLink, newEmail));
            return Task.CompletedTask;
        }

        public Task SendChangeNoticeAsync(string currentEmail, string newEmail)
        {
            notices.Enqueue(new Sent(currentEmail, string.Empty, newEmail));
            return Task.CompletedTask;
        }
    }

    /// <summary>Records what the stock Identity endpoints would have mailed.</summary>
    private sealed class RecordingIdentitySender : IEmailSender<ApplicationUser>
    {
        private readonly ConcurrentQueue<string> links = new();

        public IReadOnlyList<string> ConfirmationLinks => links.ToArray();

        public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
        {
            links.Enqueue(confirmationLink);
            return Task.CompletedTask;
        }

        public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
            Task.CompletedTask;

        public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
            Task.CompletedTask;
    }
}
