using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Controllers;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The shared-host machinery itself: a caller set per test or per client must be authenticated exactly as a factory
/// built with those permissions would be, and the reset must hand every test the store a fresh host starts with.
/// </summary>
public class SharedApiHostInfrastructureTests(SharedApiHostInfrastructureTests.Host host)
    : SharedApiHostTests<SharedApiHostInfrastructureTests.Host>(host)
{
    private const string ActorUserId = "shared-host-actor";

    private static readonly string[] SomeClaims = [PermissionClaims.AccountsRead, PermissionClaims.ContactsRead];

    public static TheoryData<string[]> ClaimSets => new() { SomeClaims, Array.Empty<string>(), new[] { PermissionClaims.AccountsRead } };

    [Theory]
    [MemberData(nameof(ClaimSets))]
    public async Task A_header_caller_and_a_default_caller_get_the_principal_a_dedicated_factory_issues(string[] permissions)
    {
        await using var dedicated = new OdysseyApiFactory(permissions, ActorUserId);
        var expected = await ClaimsOf(dedicated.CreateClient());

        Assert.Equal(expected, await ClaimsOf(Factory.ClientFor(permissions)));
        Assert.Equal(expected, await ClaimsOf(Factory.ForCaller(permissions).CreateClient()));
    }

    [Fact]
    public async Task A_null_caller_is_anonymous_and_the_actor_header_still_overrides_the_user_id()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.AnonymousClient().GetAsync("/auth/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.ForCaller(null).CreateClient().GetAsync("/auth/claims")).StatusCode);

        var claims = await ClaimsOf(Factory.ClientFor(SomeClaims, actorUserId: "someone-else"));
        Assert.Contains(new AuthController.ClaimDto(System.Security.Claims.ClaimTypes.NameIdentifier, "someone-else"), claims);
    }

    [Fact]
    public async Task The_reset_restores_the_default_caller_and_empties_the_store()
    {
        // Left behind deliberately; the reset before the next test must remove both. Run twice, either order.
        Factory.ForCaller(SomeClaims);
        await LeaveARowBehindAsync();

        await Factory.ResetStoreAsync();

        Assert.Empty(await ClaimsOf(Factory.CreateClient(), PermissionClaims.Type));
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.Locations.AnyAsync());
        Assert.True(await context.Currencies.AnyAsync(), "A host with a file volume starts seeded, so the reset re-seeds.");
    }

    private async Task LeaveARowBehindAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.Locations.Add(new Location { LocationId = Guid.NewGuid(), Name = "Left behind", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }

    private static async Task<List<AuthController.ClaimDto>> ClaimsOf(HttpClient client, string? type = null)
    {
        using (client)
        {
            var claims = (await client.GetFromJsonAsync<List<AuthController.ClaimDto>>("/auth/claims"))!;
            return type is null ? claims : claims.Where(c => c.Type == type).ToList();
        }
    }

    /// <summary>The class's shared host: every caller's claims travel per request (<see cref="SharedApiHostClients"/>).</summary>
    public sealed class Host() : OdysseyApiFactory([], ActorUserId);
}
