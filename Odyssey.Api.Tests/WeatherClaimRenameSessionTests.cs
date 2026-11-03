extern alias migrations;

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Xunit;
using RoleClaimSeeder = migrations::Odyssey.MigrationService.RoleClaimSeeder;

namespace Odyssey.Api.Tests;

/// <summary>
/// Issue #387 AC 9a: a session signed in before the weather claims were renamed picks the new values up at its
/// next security-stamp revalidation — which re-runs the claims factory — once <see cref="RoleClaimSeeder"/> has
/// reconciled the role claims, without signing in again. Until then the point endpoints answer <c>403</c>.
/// </summary>
/// <remarks>
/// The fixture revalidates on every request instead of on the production one-minute interval, the precedent
/// <c>DisableRevokesSessionsTests</c> sets: what is under test is that revalidation refreshes the principal from
/// the reconciled role claims, which the zero interval only brings forward.
/// </remarks>
public class WeatherClaimRenameSessionTests
{
    private const string Email = "weather-rename-admin@example.com";

    [Fact]
    public async Task An_admin_session_from_before_the_rename_gains_the_new_claims_without_signing_in_again()
    {
        await using var factory = new PasswordGateFactory(securityStampValidationInterval: TimeSpan.Zero);
        await factory.CreateUserAsync(Email, RoleDefinitions.AdminId);
        await RewindToTheOldValuesAsync(factory);
        using var client = await factory.LoginAsync(Email);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/weather/points")).StatusCode);

        await new RoleClaimSeeder(factory.Services, NullLogger<RoleClaimSeeder>.Instance).ExecuteAsync(CancellationToken.None);

        // The same client, the same cookie: no sign-in in between.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/weather/points")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.RoleClaims.AnyAsync(c => AuthorizationPolicyTests.RetiredWeatherClaimValues.Contains(c.ClaimValue!)));
    }

    /// <summary>The Admin role's claims as a database from before the deploy holds them: the three old values.</summary>
    private static async Task RewindToTheOldValuesAsync(PasswordGateFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        string[] renamed = [PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate, PermissionClaims.WeatherPointsDelete];
        context.RoleClaims.RemoveRange(await context.RoleClaims
            .Where(c => c.RoleId == RoleDefinitions.AdminId && renamed.Contains(c.ClaimValue!))
            .ToListAsync());
        foreach (var value in AuthorizationPolicyTests.RetiredWeatherClaimValues)
        {
            context.RoleClaims.Add(new Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>
            {
                RoleId = RoleDefinitions.AdminId, ClaimType = PermissionClaims.Type, ClaimValue = value,
            });
        }

        await context.SaveChangesAsync();
    }
}
