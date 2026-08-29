extern alias migrations;

using System.Net;
using System.Net.Http.Json;
using RoleClaimSeeder = migrations::Odyssey.MigrationService.RoleClaimSeeder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Disabling an account revokes its live sessions (issue #442). Before this, the lockout sentinel barred
/// the next sign-in and nothing else: <c>LockoutEnd</c> is read by <c>SignInManager</c> on the sign-in
/// path only, so every cookie already issued kept working — including, for a disabled Admin,
/// <c>users.manage</c> itself. Rotating the security stamp on the disable branch is what closes it.
/// </summary>
/// <remarks>
/// Two halves, deliberately. The stamp assertions below pin the mechanism and, just as importantly, its
/// <em>limits</em> — a rotation on an unrelated flag change would sign users out because an administrator
/// ticked "email confirmed". <see cref="ADisabledUsersLiveSessionIsRefused"/> then pins the consequence
/// over the real cookie pipeline, which is the only place the question "does the cookie they already hold
/// still work?" can actually be asked.
/// </remarks>
public class DisableRevokesSessionsTests
{
    private const string ActorUserId = "revoke-admin-actor-id";
    private const string TargetUserId = "revoke-target-id";
    private const string TargetEmail = "revoke-target@example.com";

    [Fact]
    public async Task DisablingAUser_RotatesTheSecurityStamp()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail, RoleDefinitions.User);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync($"/api/users/{TargetUserId}", new UpdatedUser { Enabled = false });

        response.EnsureSuccessStatusCode();
        Assert.NotEqual(stampBefore, await SecurityStampAsync(factory, TargetUserId));

        // The rotation rides on the same store write as the lockout, so neither can land without the other.
        Assert.Equal(AccountLockout.DisabledLockoutEnd, await LockoutEndAsync(factory, TargetUserId));
    }

    /// <summary>
    /// The branch is taken on every requested disable, not only on an enabled→disabled transition — so
    /// re-disabling stays a working way to end a session that outlived an earlier disable.
    /// </summary>
    [Fact]
    public async Task DisablingAnAlreadyDisabledUser_RotatesTheStampAgain()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail, RoleDefinitions.User);
        await DisableDirectlyAsync(factory, TargetUserId);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync($"/api/users/{TargetUserId}", new UpdatedUser { Enabled = false });

        response.EnsureSuccessStatusCode();
        Assert.NotEqual(stampBefore, await SecurityStampAsync(factory, TargetUserId));
    }

    [Fact]
    public async Task EnablingAUser_LeavesTheStampAlone()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail, RoleDefinitions.User);
        await DisableDirectlyAsync(factory, TargetUserId);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync($"/api/users/{TargetUserId}", new UpdatedUser { Enabled = true });

        response.EnsureSuccessStatusCode();
        Assert.Equal(stampBefore, await SecurityStampAsync(factory, TargetUserId));
        Assert.Null(await LockoutEndAsync(factory, TargetUserId));
    }

    /// <summary>
    /// The half that stops the fix from becoming a bigger bug: an administrator confirming an email
    /// address must not sign that user out of everything.
    /// </summary>
    [Fact]
    public async Task AnUnrelatedFlagChange_LeavesTheStampAlone()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, TargetUserId, TargetEmail, RoleDefinitions.User, emailConfirmed: false);
        var stampBefore = await SecurityStampAsync(factory, TargetUserId);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync($"/api/users/{TargetUserId}", new UpdatedUser { EmailConfirmed = true });

        response.EnsureSuccessStatusCode();
        Assert.Equal(stampBefore, await SecurityStampAsync(factory, TargetUserId));
    }

    /// <summary>
    /// A refused disable must change nothing at all. The last-admin guard runs before the write, so the
    /// account keeps both its lockout state and its sessions.
    /// </summary>
    [Fact]
    public async Task ARefusedDisable_LeavesTheStampAlone()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, "revoke-last-admin-id", "revoke-last-admin@example.com", RoleDefinitions.Admin);
        var stampBefore = await SecurityStampAsync(factory, "revoke-last-admin-id");
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/users/revoke-last-admin-id", new UpdatedUser { Enabled = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(stampBefore, await SecurityStampAsync(factory, "revoke-last-admin-id"));
    }

    /// <summary>
    /// The reproduction from the issue, end to end: a signed-in user whose account an administrator
    /// disables from another session loses the cookie they already hold.
    /// </summary>
    /// <remarks>
    /// The fixture revalidates on every request rather than on the production one-minute interval.
    /// Revocation is bounded, not instant — the interval is a deliberate latency trade-off documented in
    /// <c>Program.cs</c> — and a test that waited it out would spend a minute proving a property the
    /// framework already owns. What is under test is that the stamp the validator compares against no
    /// longer matches, which the zero interval simply brings forward.
    /// </remarks>
    [Fact]
    public async Task ADisabledUsersLiveSessionIsRefused()
    {
        await using var factory = new PasswordGateFactory(securityStampValidationInterval: TimeSpan.Zero);
        var target = await factory.CreateUserAsync(TargetEmail, RoleDefinitions.UserId);
        await factory.CreateUserAsync("revoke-admin@example.com", RoleDefinitions.AdminId);
        using var targetClient = await factory.LoginAsync(TargetEmail);
        using var adminClient = await factory.LoginAsync("revoke-admin@example.com");

        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/accounts")).StatusCode);

        var disable = await adminClient.PatchAsJsonAsync($"/api/users/{target.Id}", new UpdatedUser { Enabled = false });
        disable.EnsureSuccessStatusCode();

        // The same client, the same cookie, the request that worked a moment ago.
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/accounts")).StatusCode);

        // ...and the administrator who did it is still signed in: the rotation is the target's alone.
        Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/accounts")).StatusCode);
    }

    /// <summary>
    /// The mirror of the test above. Without it, a host that rejected every principal — or a fix that
    /// rotated on every <c>PATCH</c> — would pass it just as well.
    /// </summary>
    [Fact]
    public async Task AnUnrelatedFlagChange_LeavesTheLiveSessionAlive()
    {
        await using var factory = new PasswordGateFactory(securityStampValidationInterval: TimeSpan.Zero);
        var target = await factory.CreateUserAsync(TargetEmail, RoleDefinitions.UserId);
        await factory.CreateUserAsync("revoke-admin@example.com", RoleDefinitions.AdminId);
        using var targetClient = await factory.LoginAsync(TargetEmail);
        using var adminClient = await factory.LoginAsync("revoke-admin@example.com");

        var patch = await adminClient.PatchAsJsonAsync($"/api/users/{target.Id}", new UpdatedUser { EmailConfirmed = true });
        patch.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/accounts")).StatusCode);
    }

    private static async Task<string?> SecurityStampAsync(WebApplicationFactory<Program> factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SecurityStamp)
            .SingleAsync();
    }

    private static async Task<DateTimeOffset?> LockoutEndAsync(WebApplicationFactory<Program> factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.LockoutEnd)
            .SingleAsync();
    }

    /// <summary>Write the sentinel without going through the service, so no stamp rotation is implied.</summary>
    private static async Task DisableDirectlyAsync(WebApplicationFactory<Program> factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);
        user.LockoutEnabled = true;
        user.LockoutEnd = AccountLockout.DisabledLockoutEnd;
        await context.SaveChangesAsync();
    }

    private static async Task CreateUserAsync(
        WebApplicationFactory<Program> factory,
        string id,
        string email,
        string role,
        bool emailConfirmed = true)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        await new RoleClaimSeeder(factory.Services, NullLogger<RoleClaimSeeder>.Instance)
            .ExecuteAsync(CancellationToken.None);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            LockoutEnabled = true,
        };

        var result = await userManager.CreateAsync(user, "Password123!Safe");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));

        // The approval gate disables every newly added account, and the last-admin guards count *enabled*
        // admins — so a fixture user starts out both unable to sign in and invisible to those guards.
        user.LockoutEnd = null;
        await userManager.UpdateAsync(user);

        var identityRole = await context.Roles.SingleAsync(candidate => candidate.Name == role);
        context.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = identityRole.Id });
        await context.SaveChangesAsync();
    }

    private sealed class ApiFactory(IReadOnlyCollection<string> permissions)
        : OdysseyApiFactory(
            permissions,
            ActorUserId,
            configureServices: services =>
            {
                services.RemoveAll<ILookupNormalizer>();
                services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();
            });

    private sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
    {
        public string? NormalizeName(string? name) => name?.ToLowerInvariant();

        public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
    }
}
