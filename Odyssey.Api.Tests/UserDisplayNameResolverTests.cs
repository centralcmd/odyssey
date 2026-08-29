using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Unit tests for the claim-aware <see cref="UserDisplayNameResolver"/> (issue #316 §9): the
/// <c>DisplayName ?? FirstName ?? (users.read ? email : "Unknown user")</c> rule, the non-null
/// guarantee, and that it never leaks an email to a caller without <c>users.read</c>.
/// </summary>
public sealed class UserDisplayNameResolverTests
{
    private static OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ClaimsPrincipal Caller(params string[] permissions)
    {
        var claims = permissions.Select(p => new Claim(PermissionClaims.Type, p));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<OdysseyContext> SeedAsync(
        string userId,
        string? userName,
        string? email,
        Action<UserProfile>? configureProfile = null)
    {
        var context = NewContext();
        context.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userName,
            Email = email,
        });

        if (configureProfile is not null)
        {
            var profile = new UserProfile { UserId = userId };
            configureProfile(profile);
            context.UserProfiles.Add(profile);
        }

        await context.SaveChangesAsync();
        return context;
    }

    [Fact]
    public async Task Resolve_PrefersDisplayNameOverEverything()
    {
        using var context = await SeedAsync("u1", "alice", "alice@example.com", p =>
        {
            p.DisplayName = "Ali";
            p.FirstName = "Alice";
        });
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.UsersRead), "u1", CancellationToken.None);

        Assert.Equal("Ali", label);
    }

    [Fact]
    public async Task Resolve_FallsBackToFirstName_WhenNoDisplayName()
    {
        using var context = await SeedAsync("u1", "alice", "alice@example.com", p => p.FirstName = "Alice");
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.UsersRead), "u1", CancellationToken.None);

        Assert.Equal("Alice", label);
    }

    [Fact]
    public async Task Resolve_CallerWithUsersRead_SeesEmail_WhenNoName()
    {
        using var context = await SeedAsync("u1", "alice", "alice@example.com");
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.UsersRead), "u1", CancellationToken.None);

        Assert.Equal("alice@example.com", label);
    }

    [Fact]
    public async Task Resolve_CallerWithoutUsersRead_NeverSeesEmail_ReturnsUnknownUser()
    {
        // The Owner tier holds journal/photos read but NOT users.read — must get "Unknown user", never the email.
        using var context = await SeedAsync("u1", "alice", "alice@example.com");
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.JournalRead), "u1", CancellationToken.None);

        Assert.Equal(UserDisplayNameResolver.UnknownUser, label);
        Assert.DoesNotContain("@", label);
    }

    [Fact]
    public async Task Resolve_CallerWithoutUsersRead_StillSeesDisplayNameAndFirstName()
    {
        using var context = await SeedAsync("u1", "alice", "alice@example.com", p => p.FirstName = "Alice");
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.JournalRead), "u1", CancellationToken.None);

        Assert.Equal("Alice", label);
    }

    [Fact]
    public async Task Resolve_CallerWithUsersRead_FallsBackToUserName_WhenEmailNull()
    {
        using var context = await SeedAsync("u1", "aliceuser", email: null);
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.UsersRead), "u1", CancellationToken.None);

        Assert.Equal("aliceuser", label);
    }

    [Fact]
    public async Task Resolve_TrimsDisplayName()
    {
        using var context = await SeedAsync("u1", "alice", "alice@example.com", p => p.DisplayName = "  Ali  ");
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(), "u1", CancellationToken.None);

        Assert.Equal("Ali", label);
    }

    [Fact]
    public async Task Resolve_UnknownId_ReturnsUnknownUser_NotAnError()
    {
        using var context = NewContext();
        var resolver = new UserDisplayNameResolver(context);

        var label = await resolver.ResolveAsync(Caller(PermissionClaims.UsersRead), "does-not-exist", CancellationToken.None);

        Assert.Equal(UserDisplayNameResolver.UnknownUser, label);
    }

    [Fact]
    public async Task Resolve_NullOrEmptyId_ReturnsUnknownUser()
    {
        using var context = NewContext();
        var resolver = new UserDisplayNameResolver(context);

        Assert.Equal(UserDisplayNameResolver.UnknownUser,
            await resolver.ResolveAsync(Caller(), (string?)null, CancellationToken.None));
        Assert.Equal(UserDisplayNameResolver.UnknownUser,
            await resolver.ResolveAsync(Caller(), "  ", CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_Batch_ReturnsNonNullLabelForEveryWantedId()
    {
        using var context = await SeedAsync("u1", "alice", "alice@example.com", p => p.DisplayName = "Ali");
        var resolver = new UserDisplayNameResolver(context);

        var map = await resolver.ResolveAsync(
            Caller(PermissionClaims.UsersRead),
            ["u1", "missing", null, "  "],
            CancellationToken.None);

        Assert.Equal("Ali", map["u1"]);
        Assert.Equal(UserDisplayNameResolver.UnknownUser, map["missing"]);
        Assert.All(map.Values, value => Assert.False(string.IsNullOrEmpty(value)));
        // Null/whitespace ids are filtered out entirely.
        Assert.Equal(2, map.Count);
    }
}
