using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The server-side half of the admin-initiated reset (issue #406 §5.6/§5.7) — the block itself, the five
/// endpoints that let a gated user out of it, and the first-party change-password endpoint that is the way
/// out. Everything here authenticates through the real cookie pipeline, because that is the only way the
/// gate's central questions ("does the same session still work afterwards?") can be asked at all.
/// </summary>
public class PasswordChangeRequiredGateTests
{
    private const string GatedEmail = "gated@example.com";
    private const string NewPassword = "Renewed987!Passphrase";

    /// <summary>
    /// A sample across modules. It is a smoke check, not the guarantee — that is
    /// <see cref="PasswordChangeExemptEndpointsTests"/>, which enumerates every endpoint.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/accounts")]
    [InlineData("GET", "/api/transactions")]
    [InlineData("GET", "/api/budgets")]
    [InlineData("GET", "/api/contacts")]
    [InlineData("GET", "/api/journal-entries")]
    [InlineData("GET", "/api/tasks")]
    [InlineData("GET", "/api/photos")]
    [InlineData("GET", "/api/calendars")]
    [InlineData("GET", "/api/files")]
    [InlineData("GET", "/api/tax-statements")]
    [InlineData("GET", "/api/insurance-policies")]
    [InlineData("GET", "/api/user-preferences/accounts-page")]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/system-settings")]
    [InlineData("POST", "/api/accounts")]
    [InlineData("POST", "/api/transactions")]
    [InlineData("PUT", "/api/profile")]
    [InlineData("POST", "/manage/info")]
    public async Task WhileGated_EveryModuleIsRefused(string method, string path)
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail, RoleDefinitions.AdminId);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            PasswordChangeRequiredMiddleware.ProblemCode,
            problem.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// The mirror of the test above. Without it, a middleware that refused everything unconditionally would
    /// still pass, and this suite would be measuring nothing.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/accounts")]
    [InlineData("GET", "/api/users")]
    [InlineData("POST", "/manage/info")]
    public async Task WithTheFlagClear_TheMiddlewareIsInert(string method, string path)
    {
        await using var factory = new PasswordGateFactory();
        await factory.CreateUserAsync(GatedEmail, RoleDefinitions.AdminId);
        using var client = await factory.LoginAsync(GatedEmail);

        var response = await SendAsync(client, method, path);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/profile")]
    [InlineData("GET", "/auth/claims")]
    [InlineData("GET", "/auth/permissions")]
    public async Task TheExemptReads_StillSucceedWhileGated(string method, string path)
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SigningOut_StillWorksWhileGated()
    {
        // The lockout escape hatch: a user who does not know their current password has to be able to
        // leave and use the emailed link instead.
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await client.PostAsync("/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // ...and the session is really gone, not merely acknowledged.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/profile")).StatusCode);
    }

    /// <summary>
    /// <c>POST /manage/info</c> changes the password <em>and</em> the email address, and a pending email
    /// change is confirmed from the <em>new</em> address — so exempting it would let a gated session move
    /// the account's sign-in identity to a mailbox the attacker controls. This is also the assertion that
    /// fails loudly if the <c>/logout</c> exemption is ever applied group-wide to the Identity routes.
    /// </summary>
    [Fact]
    public async Task TheEmailChangeEscalation_IsClosed()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await client.PostAsJsonAsync("/manage/info", new { newEmail = "attacker@example.com" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(GatedEmail, (await users.FindByIdAsync(user.Id))!.Email);
    }

    [Fact]
    public async Task AGatedAdmin_CannotResetAnyoneElse()
    {
        await using var factory = new PasswordGateFactory();
        var admin = await factory.CreateUserAsync("admin@example.com", RoleDefinitions.AdminId);
        var target = await factory.CreateUserAsync("target@example.com");
        using var client = await factory.LoginAsync("admin@example.com");
        await factory.SetMustChangePasswordAsync(admin.Id);

        var response = await client.PostAsync($"/api/users/{target.Id}/password-reset", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await factory.MustChangePasswordAsync(target.Id));
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_IsStill401_NotForbidden()
    {
        // The middleware must not change unauthenticated behaviour — and must not query the database for it.
        await using var factory = new PasswordGateFactory();
        await factory.CreateUserAsync(GatedEmail);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The anonymous endpoints are never blocked, which is what lets a user complete the emailed reset in a
    /// browser that is already signed in with the old password.
    /// </summary>
    [Fact]
    public async Task CompletingTheEmailedReset_WorksWhileGated_AndClearsTheFlag()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        string code;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var target = (await users.FindByIdAsync(user.Id))!;
            code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
                System.Text.Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(target)));
        }

        var reset = await client.PostAsJsonAsync(
            "/resetPassword", new { email = GatedEmail, resetCode = code, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(await factory.MustChangePasswordAsync(user.Id));
    }

    [Fact]
    public async Task TheChangePasswordEndpoint_ClearsTheFlag_AndKeepsTheSessionAlive()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail, RoleDefinitions.AdminId);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/accounts")).StatusCode);

        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = NewPassword,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await factory.MustChangePasswordAsync(user.Id));
        Assert.Equal(0, await factory.AccessFailedCountAsync(user.Id));

        // The same cookie, on an endpoint that was refusing it a moment ago. Without RefreshSignInAsync the
        // password change's own stamp rotation would sign this session out — which the one-minute
        // ValidationInterval would surface within the minute rather than in half an hour.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/accounts")).StatusCode);
    }

    [Fact]
    public async Task AWrongCurrentPassword_Is400_KeepsTheFlag_AndCountsAsAFailedAttempt()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = "Wrong123!Passphrase",
            newPassword = NewPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await factory.MustChangePasswordAsync(user.Id));

        // UserManager.ChangePasswordAsync does not touch AccessFailedCount — lockout accounting in Identity
        // runs exclusively through SignInManager's sign-in path — so this asserts the endpoint wires it
        // itself. Without it, an attacker holding a gated session cookie would have unlimited guesses at
        // the password the gate exists because of.
        Assert.Equal(1, await factory.AccessFailedCountAsync(user.Id));
    }

    [Fact]
    public async Task RepeatingAWrongCurrentPassword_LocksOut_AndStopsAttemptingTheChange()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        // Read the threshold from the options rather than hardcoding it: Program.cs does not configure
        // Lockout, so this is the framework default today and a literal would silently become wrong the
        // day someone sets one.
        var threshold = factory.Services.GetRequiredService<IOptions<IdentityOptions>>()
            .Value.Lockout.MaxFailedAccessAttempts;

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < threshold; attempt++)
        {
            last = await client.PostAsJsonAsync("/api/account/password", new
            {
                currentPassword = "Wrong123!Passphrase",
                newPassword = NewPassword,
            });
        }

        Assert.Equal(HttpStatusCode.Locked, last!.StatusCode);

        // Locked out, so the correct password is refused without a change being attempted at all.
        var afterLockout = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = NewPassword,
        });

        Assert.Equal(HttpStatusCode.Locked, afterLockout.StatusCode);
        Assert.True(await factory.MustChangePasswordAsync(user.Id));
    }

    [Fact]
    public async Task APolicyViolatingNewPassword_Is400_WithIdentitysOwnMessage()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        // Long enough to clear the DTO's MinimumLength, so this is Identity's character-class policy
        // talking rather than model validation.
        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = "aaaaaaaaaaaaaaaaaaaa",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await factory.MustChangePasswordAsync(user.Id));

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var detail = problem.GetProperty("detail").GetString();

        // Distinguishable from the wrong-password 400, which is what lets the gate page tell the user
        // which of the two fields to fix.
        Assert.NotNull(detail);
        Assert.DoesNotContain("current password is incorrect", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnchangedNewPassword_IsRejected()
    {
        // Identity accepts a no-op change (it simply rehashes), so the endpoint enforces this itself —
        // otherwise a reset user could "change" straight back to the password that was reset in the first
        // place and clear the gate with no real rotation.
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = PasswordGateFactory.Password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await factory.MustChangePasswordAsync(user.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOverLengthPassword_IsRejectedByModelValidation(bool overLengthCurrent)
    {
        // The point of the upper bound: IdentityOptions.Password sets only a minimum and the PBKDF2 hasher
        // has no inherent input-size cap, so without it a multi-megabyte string would reach the hasher.
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var oversized = new string('a', 257) + "A1!";
        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = overLengthCurrent ? oversized : PasswordGateFactory.Password,
            newPassword = overLengthCurrent ? NewPassword : oversized,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Rejected before the handler runs, so no failed-attempt was recorded against the account.
        Assert.Equal(0, await factory.AccessFailedCountAsync(user.Id));
        Assert.True(await factory.MustChangePasswordAsync(user.Id));
    }

    [Fact]
    public async Task AnOrdinarySelfServiceChange_StillWorks_AndKeepsTheSession()
    {
        // The /account page's existing flow, on the repointed endpoint: no gate involved.
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail, RoleDefinitions.AdminId);
        using var client = await factory.LoginAsync(GatedEmail);

        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = NewPassword,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/accounts")).StatusCode);
        Assert.False(await factory.MustChangePasswordAsync(user.Id));
    }

    [Fact]
    public async Task TheProfileEndpoint_ReportsTheFlagToTheGatedUserThemselves()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);

        var profile = await client.GetFromJsonAsync<ProfileDto>("/api/profile");

        Assert.True(profile!.MustChangePassword);
    }

    /// <summary>
    /// The two gates' precedence, which the client's redirect order mirrors. A user who owes both a
    /// password change and a legal acceptance must be able to do the password first — so the password
    /// middleware runs ahead of the legal one, and the change-password endpoint is on the legal
    /// allowlist. Without that pairing the two gates deadlock: this one refuses
    /// <c>POST /api/legal/respond</c>, and that one refuses the password change.
    /// </summary>
    [Fact]
    public async Task AUserWhoOwesBothGates_CanStillChangeTheirPassword()
    {
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);
        await factory.RevokeLegalAcceptanceAsync(user.Id);

        var response = await client.PostAsJsonAsync("/api/account/password", new
        {
            currentPassword = PasswordGateFactory.Password,
            newPassword = NewPassword,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await factory.MustChangePasswordAsync(user.Id));
    }

    [Fact]
    public async Task AUserWhoOwesBothGates_MeetsThePasswordGateFirst()
    {
        // Both middlewares would refuse this call; the password one runs first, so the client is told
        // which gate to route to — and it is the one that unblocks the other.
        await using var factory = new PasswordGateFactory();
        var user = await factory.CreateUserAsync(GatedEmail);
        using var client = await factory.LoginAsync(GatedEmail);
        await factory.SetMustChangePasswordAsync(user.Id);
        await factory.RevokeLegalAcceptanceAsync(user.Id);

        var response = await client.PostAsJsonAsync("/api/legal/respond", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            PasswordChangeRequiredMiddleware.ProblemCode,
            problem.RootElement.GetProperty("code").GetString());
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path) =>
        method switch
        {
            "GET" => client.GetAsync(path),
            "POST" => client.PostAsJsonAsync(path, new { }),
            "PUT" => client.PutAsJsonAsync(path, new { }),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported method."),
        };
}
