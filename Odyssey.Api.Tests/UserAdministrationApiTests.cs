extern alias migrations;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RoleClaimSeeder = migrations::Odyssey.MigrationService.RoleClaimSeeder;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Dtos.Application;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;
// Contact's Sex enum now also lives in Odyssey.Dtos; this test means the user-profile Sex.
using Sex = Odyssey.Dtos.Application.Sex;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

public class UserAdministrationApiTests
{
    private const string ActorUserId = "admin-actor-id";

    [Fact]
    public async Task GetUsers_WithoutUsersReadPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_WithUsersReadPermission_ReturnsUsers()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "list-user-id", "list@example.com");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/users?offset=0&limit=100");

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<ExistingUser>>();
        Assert.NotNull(page);
        Assert.Contains(page!.Items, user => user.Id == "list-user-id" && user.UserName == "list@example.com");
    }

    [Fact]
    public async Task GetUsers_IncludesProfileFields_AndSortsByFullName()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await CreateUserAsync(factory, "profile-user-zed", "zed@example.com");
        await CreateUserAsync(factory, "profile-user-amy", "amy@example.com");
        await CreateProfileAsync(factory, "profile-user-zed", "Zed", "Zephyr", new DateOnly(1990, 1, 1), Sex.Male);
        await CreateProfileAsync(factory, "profile-user-amy", "Amy", "Ashford", new DateOnly(1985, 6, 15), Sex.Female);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/users?offset=0&limit=100&sortBy=FullName&sortDir=asc");

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<ExistingUser>>();
        Assert.NotNull(page);

        var amy = Assert.Single(page!.Items, user => user.Id == "profile-user-amy");
        Assert.Equal("Amy", amy.FirstName);
        Assert.Equal("Ashford", amy.LastName);
        Assert.Equal(new DateOnly(1985, 6, 15), amy.BirthDate);
        Assert.Equal(Sex.Female, amy.Sex);

        var amyIndex = page.Items.ToList().FindIndex(user => user.Id == "profile-user-amy");
        var zedIndex = page.Items.ToList().FindIndex(user => user.Id == "profile-user-zed");
        Assert.True(amyIndex < zedIndex);
    }

    [Fact]
    public async Task GetUsers_NegativeOffset_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "clamp-user-id", "clamp@example.com");
        using var client = factory.CreateClient();

        // offset is [Range(0, int.MaxValue)]; a negative value is rejected at model validation.
        var response = await client.GetAsync("/api/users?offset=-5");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_OversizedLimit_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        // limit is [Range(0, MaxLimit)]; a value above the ceiling is rejected at model validation.
        var response = await client.GetAsync($"/api/users?limit={ListDefaults.MaxLimit + 1}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetUser_MissingUser_ReturnsNotFound()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/users/missing-user-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchUser_RequiresUsersUpdatePermission()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await CreateUserAsync(factory, "patch-auth-user-id", "patch-auth@example.com");
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/users/patch-auth-user-id", new { emailConfirmed = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutUserRole_RequiresUsersUpdatePermission()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await CreateUserAsync(factory, "role-auth-user-id", "role-auth@example.com");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/users/role-auth-user-id/role", new UpdatedUserRole { Role = RoleDefinitions.Owner });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_WithUsersReadPermission_ReturnsSeededRolesAndPermissions()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/users/roles");

        response.EnsureSuccessStatusCode();
        var roles = await response.Content.ReadFromJsonAsync<List<ExistingRole>>();
        Assert.NotNull(roles);
        var adminRole = Assert.Single(roles!, role => role.Name == RoleDefinitions.Admin);
        Assert.Contains(PermissionClaims.UsersRead, adminRole.Permissions);
        Assert.Contains(PermissionClaims.UsersUpdate, adminRole.Permissions);
        Assert.DoesNotContain(roles.Where(role => role.Name != RoleDefinitions.Admin).SelectMany(role => role.Permissions),
            permission => permission is PermissionClaims.UsersRead or PermissionClaims.UsersUpdate);
    }

    [Fact]
    public async Task GetPermissions_WithUsersReadPermission_ReturnsAllKnownPermissionClaims()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersRead]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/users/permissions");

        response.EnsureSuccessStatusCode();
        var permissions = await response.Content.ReadFromJsonAsync<List<ExistingPermission>>();
        Assert.NotNull(permissions);
        var values = permissions!.Select(permission => permission.Value).ToArray();
        Assert.Equal(RolePermissions.AllClaims.OrderBy(claim => claim, StringComparer.Ordinal), values);
        Assert.Contains(permissions, permission => permission.Value == PermissionClaims.TransactionTagsRead
            && permission.Category == "transactions.tags"
            && permission.Action == "read");
    }

    [Fact]
    public async Task PatchUser_EmailConfirmedTrue_ConfirmsEmail()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, "confirm-user-id", "confirm@example.com", RoleDefinitions.User, emailConfirmed: false);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/users/confirm-user-id", new UpdatedUser { EmailConfirmed = true });

        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<ExistingUser>();
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
    }

    [Fact]
    public async Task PatchUser_EmailConfirmedFalse_UnconfirmsEmail()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, "unconfirm-user-id", "unconfirm@example.com", RoleDefinitions.User, emailConfirmed: true);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/users/unconfirm-user-id", new UpdatedUser { EmailConfirmed = false });

        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<ExistingUser>();
        Assert.NotNull(user);
        Assert.False(user!.EmailConfirmed);
    }

    [Fact]
    public async Task PatchUser_DisableUser_SetsLockoutAndReturnsDisabledUser()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await CreateUserAsync(factory, "disable-user-id", "disable@example.com", RoleDefinitions.User);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/users/disable-user-id", new UpdatedUser { Enabled = false });

        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<ExistingUser>();
        Assert.NotNull(user);
        Assert.False(user!.Enabled);
        Assert.NotNull(user.LockoutEnd);
        Assert.Equal(9999, user.LockoutEnd!.Value.Year);
    }

    [Fact]
    public async Task PutUserRole_UnknownRole_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "unknown-role-user-id", "unknown-role@example.com");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/users/unknown-role-user-id/role", new UpdatedUserRole { Role = "UnknownRole" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutUserRole_ReplacesExistingRolesAndLeavesExactlyOneRole()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "replace-role-user-id", "replace-role@example.com", RoleDefinitions.User);
        await AddRoleAsync(factory, "replace-role-user-id", RoleDefinitions.Guest);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/users/replace-role-user-id/role", new UpdatedUserRole { Role = RoleDefinitions.Owner });

        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<ExistingUser>();
        Assert.NotNull(user);
        Assert.Equal(RoleDefinitions.Owner, user!.Role);

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var targetUser = await userManager.FindByIdAsync("replace-role-user-id");
        Assert.NotNull(targetUser);
        var roles = await userManager.GetRolesAsync(targetUser!);
        var role = Assert.Single(roles);
        Assert.Equal(RoleDefinitions.Owner, role);
    }

    [Fact]
    public async Task PatchUser_CannotDisableLastEnabledAdmin()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "last-admin-id", "last-admin@example.com", RoleDefinitions.Admin);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync("/api/users/last-admin-id", new UpdatedUser { Enabled = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task PutUserRole_CannotDemoteLastEnabledAdmin()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "last-demote-admin-id", "last-demote-admin@example.com", RoleDefinitions.Admin);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/users/last-demote-admin-id/role", new UpdatedUserRole { Role = RoleDefinitions.Owner });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_WithoutUsersDeletePermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersUpdate]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "no-delete-perm-id", "no-delete-perm@example.com", RoleDefinitions.User);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/users/no-delete-perm-id");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_WithUsersDeletePermission_RemovesTheUser()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersDelete]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "delete-target-id", "delete-target@example.com", RoleDefinitions.User);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/users/delete-target-id");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await userManager.FindByIdAsync("delete-target-id"));
    }

    [Fact]
    public async Task DeleteUser_UnknownId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersDelete]);
        await EnsureCreatedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/users/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_OwnAccount_ReturnsConflict()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersDelete]);
        await EnsureCreatedAsync(factory);
        // The test principal's NameIdentifier is ActorUserId; deleting that same account is refused.
        await CreateUserAsync(factory, ActorUserId, "self@example.com", RoleDefinitions.User);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/users/{ActorUserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_CannotDeleteLastEnabledAdmin()
    {
        await using var factory = new ApiFactory([PermissionClaims.UsersDelete]);
        await EnsureCreatedAsync(factory);
        await CreateUserAsync(factory, "last-admin-delete-id", "last-admin-delete@example.com", RoleDefinitions.Admin);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/users/last-admin-delete-id");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task EnsureCreatedAsync(WebApplicationFactory<Program> factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            await context.Database.EnsureCreatedAsync();
        }

        // Roles come from the model seed, but their claims are reconciled at runtime by the migrations
        // job rather than seeded by HasData — /api/users/roles reads those rows, so a fixture that only
        // creates the database would report every role as having no permissions at all.
        await new RoleClaimSeeder(factory.Services, NullLogger<RoleClaimSeeder>.Instance)
            .ExecuteAsync(CancellationToken.None);
    }

    private static async Task CreateUserAsync(
        WebApplicationFactory<Program> factory,
        string id,
        string email,
        string? role = null,
        bool emailConfirmed = true)
    {
        await EnsureCreatedAsync(factory);
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existingUser = await userManager.FindByIdAsync(id);
        if (existingUser is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            LockoutEnabled = true
        };

        var result = await userManager.CreateAsync(user, "Password123!Safe");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));

        // The require-admin-approval gate disables every newly added account, with no first-user
        // exemption since issue #290 — so a fixture user is created disabled unless the lockout is
        // cleared here. It matters beyond logins: the last-admin guards count *enabled* admins.
        user.LockoutEnd = null;
        await userManager.UpdateAsync(user);

        if (role is not null)
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var identityRole = await context.Roles.SingleAsync(identityRole => identityRole.Name == role);
            context.UserRoles.RemoveRange(context.UserRoles.Where(userRole => userRole.UserId == id));
            context.UserRoles.Add(new IdentityUserRole<string>
            {
                UserId = id,
                RoleId = identityRole.Id,
            });
            await context.SaveChangesAsync();
        }
    }

    private static async Task CreateProfileAsync(
        WebApplicationFactory<Program> factory,
        string userId,
        string firstName,
        string lastName,
        DateOnly birthDate,
        Sex sex)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.UserProfiles.Add(new UserProfile
        {
            UserId = userId,
            FirstName = firstName,
            LastName = lastName,
            BirthDate = birthDate,
            Sex = sex,
        });
        await context.SaveChangesAsync();
    }

    private static async Task AddRoleAsync(WebApplicationFactory<Program> factory, string userId, string role)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId);
        Assert.NotNull(user);
        var result = await userManager.AddToRoleAsync(user!, role);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    // Users in this suite are looked up via UserManager, whose default upper-invariant
    // normalizer cannot match the seeded roles' lower-cased NormalizedName — so swap in a
    // lower-invariant normalizer for these tests.
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
