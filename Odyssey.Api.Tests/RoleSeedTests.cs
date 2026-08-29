extern alias migrations;

using Odyssey.Context;
using Odyssey.Context.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;
using Xunit;
using RoleClaimSeeder = migrations::Odyssey.MigrationService.RoleClaimSeeder;

namespace Odyssey.Api.Tests;

/// <summary>
/// Roles come from the model's <c>HasData</c>; claims come from <see cref="RoleClaimSeeder"/>.
/// </summary>
/// <remarks>
/// The split is deliberate. Role ids are fixed GUIDs, so seeding them from the model is stable. Claim
/// ids are an int identity, so a model seed had to number them positionally — and one added claim
/// renumbered every row after it, which is why each claim addition used to need a hand-written raw-SQL
/// migration. Reconciling claims at runtime on <c>(RoleId, ClaimType, ClaimValue)</c> removes that.
/// This test therefore has to run the seeder to see claims at all; <c>EnsureCreated</c> alone now
/// yields roles and no claims, which is correct rather than a regression.
/// </remarks>
public class RoleSeedTests
{
    [Fact]
    public async Task TheContextSeedsRoles_AndTheSeederReconcilesClaims()
    {
        await using var provider = BuildProvider(nameof(TheContextSeedsRoles_AndTheSeederReconcilesClaims));

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreated();
        }

        await new RoleClaimSeeder(provider, NullLogger<RoleClaimSeeder>.Instance)
            .ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var roles = context.Roles.ToList();

            Assert.Contains(roles, role => role.Name == RoleDefinitions.Admin);
            Assert.Contains(roles, role => role.Name == RoleDefinitions.Owner);
            Assert.Contains(roles, role => role.Name == RoleDefinitions.User);
            Assert.Contains(roles, role => role.Name == RoleDefinitions.Guest);

            AssertRoleClaims(context, RoleDefinitions.AdminId, RolePermissions.AdminClaims);
            AssertRoleClaims(context, RoleDefinitions.OwnerId, RolePermissions.OwnerClaims);
            AssertRoleClaims(context, RoleDefinitions.UserId, RolePermissions.UserClaims);
            AssertRoleClaims(context, RoleDefinitions.GuestId, RolePermissions.GuestClaims);
        }
    }

    /// <summary>
    /// The seeder is the only writer of these rows and runs on every migrations-job start, so a second
    /// pass adding duplicates would compound on every deploy.
    /// </summary>
    [Fact]
    public async Task TheSeederIsIdempotent()
    {
        await using var provider = BuildProvider(nameof(TheSeederIsIdempotent));

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreated();
        }

        var seeder = new RoleClaimSeeder(provider, NullLogger<RoleClaimSeeder>.Instance);
        await seeder.ExecuteAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var after = scope.ServiceProvider.GetRequiredService<OdysseyContext>().RoleClaims.Count();
            await seeder.ExecuteAsync(CancellationToken.None);

            Assert.Equal(after, scope.ServiceProvider.GetRequiredService<OdysseyContext>().RoleClaims.Count());
        }
    }

    private static ServiceProvider BuildProvider(string databaseName) =>
        new ServiceCollection()
            .AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();

    private static void AssertRoleClaims(OdysseyContext context, string roleId, IEnumerable<string> expectedClaims)
    {
        var claimValues = context.RoleClaims
            .Where(roleClaim => roleClaim.RoleId == roleId)
            .Select(roleClaim => roleClaim.ClaimValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expectedClaimArray = expectedClaims.ToArray();

        foreach (var claim in expectedClaimArray)
        {
            Assert.Contains(claim, claimValues);
        }

        Assert.Equal(expectedClaimArray.Length, claimValues.Count);
    }
}
