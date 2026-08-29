using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.MigrationService.Tests;

/// <summary>
/// The reconciliation that replaced the positional <c>HasData</c> claim seed. Claim identity is
/// <c>(RoleId, ClaimType, ClaimValue)</c> and the database assigns ids, so every assertion here is on
/// the triple and none on <c>Id</c>.
/// </summary>
/// <remarks>
/// <para>
/// The removal half is the reason this file exists. It is what makes dropping a claim from a role
/// actually revoke it, and it is the only part of the seeder that can *take away* authorization — so
/// it needs coverage in both directions: it must delete what <see cref="RolePermissions"/> no longer
/// grants, and it must not touch anything outside <see cref="PermissionClaims.Type"/>.
/// </para>
/// <para>
/// <see cref="MigrationServiceTestHost"/>'s <c>EnsureCreated</c> seeds the four roles from the model
/// but no claims, which is exactly the state a freshly migrated database is in before the seeder runs.
/// </para>
/// </remarks>
public class RoleClaimSeederTests
{
    private static readonly (string RoleId, string[] Claims)[] Mapping =
    [
        (RoleDefinitions.AdminId, RolePermissions.AdminClaims),
        (RoleDefinitions.OwnerId, RolePermissions.OwnerClaims),
        (RoleDefinitions.UserId, RolePermissions.UserClaims),
        (RoleDefinitions.GuestId, RolePermissions.GuestClaims),
    ];

    [Fact]
    public async Task A_freshly_migrated_database_gets_exactly_the_mapped_claims()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);

        foreach (var (roleId, claims) in Mapping)
        {
            Assert.Equal(claims.OrderBy(claim => claim, StringComparer.Ordinal), await ClaimsForAsync(provider, roleId));
        }

        // No role outside the mapping picked anything up.
        var roleIds = await RowsAsync(provider, rows => rows.Select(row => row.RoleId).Distinct().ToListAsync());
        Assert.Equal(
            Mapping.Select(entry => entry.RoleId).OrderBy(id => id, StringComparer.Ordinal),
            roleIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>
    /// The removal half: a permission claim the mapping no longer grants has to actually stop being
    /// granted, or revoking one would need the hand-written migration this class exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_claim_the_mapping_no_longer_grants_is_revoked()
    {
        await using var provider = BuildProvider(out var seeder);
        await seeder.ExecuteAsync(CancellationToken.None);

        // Grant Guest a claim it must not hold — the shape a removed-from-RolePermissions row leaves
        // behind on an already-seeded database.
        await AddRowAsync(provider, RoleDefinitions.GuestId, PermissionClaims.Type, PermissionClaims.UsersDelete);
        Assert.Contains(PermissionClaims.UsersDelete, await ClaimsForAsync(provider, RoleDefinitions.GuestId));

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.DoesNotContain(PermissionClaims.UsersDelete, await ClaimsForAsync(provider, RoleDefinitions.GuestId));
        Assert.Equal(
            RolePermissions.GuestClaims.OrderBy(claim => claim, StringComparer.Ordinal),
            await ClaimsForAsync(provider, RoleDefinitions.GuestId));
    }

    /// <summary>
    /// A permission claim on a role the mapping does not cover is removed too. The four roles are the
    /// whole vocabulary, so a fifth role holding permissions is unreachable state — and revoking is the
    /// fail-closed answer to reaching it anyway.
    /// </summary>
    [Fact]
    public async Task A_permission_claim_on_an_unmapped_role_is_revoked()
    {
        await using var provider = BuildProvider(out var seeder);
        await seeder.ExecuteAsync(CancellationToken.None);

        const string strayRoleId = "11111111-2222-3333-4444-555555555555";
        await AddRowAsync(provider, strayRoleId, PermissionClaims.Type, PermissionClaims.UsersRead);

        await seeder.ExecuteAsync(CancellationToken.None);

        Assert.Empty(await ClaimsForAsync(provider, strayRoleId));
    }

    /// <summary>
    /// The deletion is filtered on <see cref="PermissionClaims.Type"/>. Nothing else writes
    /// <c>AspNetRoleClaims</c> today, so this is the guard for the day something does.
    /// </summary>
    [Fact]
    public async Task Claims_of_another_type_are_left_untouched()
    {
        await using var provider = BuildProvider(out var seeder);
        await AddRowAsync(provider, RoleDefinitions.AdminId, "feature-flag", "beta-program");

        await seeder.ExecuteAsync(CancellationToken.None);

        var survivor = Assert.Single(
            await RowsAsync(provider, rows => rows.Where(row => row.ClaimType == "feature-flag").ToListAsync()));
        Assert.Equal(RoleDefinitions.AdminId, survivor.RoleId);
        Assert.Equal("beta-program", survivor.ClaimValue);

        // …and it is not counted as a permission the Admin role holds.
        Assert.Equal(
            RolePermissions.AdminClaims.OrderBy(claim => claim, StringComparer.Ordinal),
            await ClaimsForAsync(provider, RoleDefinitions.AdminId));
    }

    /// <summary>
    /// The seeder runs on every migrations-job start, so a second pass that added or renumbered rows
    /// would compound on every deploy.
    /// </summary>
    [Fact]
    public async Task Running_twice_changes_nothing()
    {
        await using var provider = BuildProvider(out var seeder);

        await seeder.ExecuteAsync(CancellationToken.None);
        var afterFirstRun = await RowsAsync(provider, rows =>
            rows.Select(row => new { row.Id, row.RoleId, row.ClaimType, row.ClaimValue }).ToListAsync());

        await seeder.ExecuteAsync(CancellationToken.None);
        var afterSecondRun = await RowsAsync(provider, rows =>
            rows.Select(row => new { row.Id, row.RoleId, row.ClaimType, row.ClaimValue }).ToListAsync());

        Assert.Equal(afterFirstRun.OrderBy(row => row.Id), afterSecondRun.OrderBy(row => row.Id));
    }

    /// <summary>
    /// Additions and removals in one pass, which is what an upgrade that both grants and revokes looks
    /// like — and the case where handling only one direction would still leave the database wrong.
    /// </summary>
    [Fact]
    public async Task A_drifted_database_converges_in_a_single_run()
    {
        await using var provider = BuildProvider(out var seeder);

        // Missing everything for Owner, plus one claim nobody should hold.
        await AddRowAsync(provider, RoleDefinitions.UserId, PermissionClaims.Type, PermissionClaims.UsersDelete);

        await seeder.ExecuteAsync(CancellationToken.None);

        foreach (var (roleId, claims) in Mapping)
        {
            Assert.Equal(claims.OrderBy(claim => claim, StringComparer.Ordinal), await ClaimsForAsync(provider, roleId));
        }
    }

    // Materialize before ordering: an OrderBy carrying a StringComparer is not translatable by the
    // provider, and the sort is only here to make the assertion order-insensitive.
    private static async Task<List<string>> ClaimsForAsync(ServiceProvider provider, string roleId)
    {
        var claims = await RowsAsync(provider, rows => rows
            .Where(row => row.RoleId == roleId && row.ClaimType == PermissionClaims.Type)
            .Select(row => row.ClaimValue!)
            .ToListAsync());

        return [.. claims.OrderBy(claim => claim, StringComparer.Ordinal)];
    }

    private static async Task<T> RowsAsync<T>(
        ServiceProvider provider, Func<IQueryable<IdentityRoleClaim<string>>, Task<T>> query)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await query(context.RoleClaims.AsNoTracking());
    }

    private static async Task AddRowAsync(
        ServiceProvider provider, string roleId, string claimType, string claimValue)
    {
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        context.RoleClaims.Add(new IdentityRoleClaim<string>
        {
            RoleId = roleId,
            ClaimType = claimType,
            ClaimValue = claimValue
        });
        await context.SaveChangesAsync();
    }

    private static ServiceProvider BuildProvider(out RoleClaimSeeder seeder)
    {
        var provider = MigrationServiceTestHost.Build();
        seeder = new RoleClaimSeeder(provider, provider.GetRequiredService<ILogger<RoleClaimSeeder>>());
        return provider;
    }
}
