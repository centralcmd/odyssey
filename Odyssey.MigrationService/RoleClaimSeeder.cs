using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;

namespace Odyssey.MigrationService;

/// <summary>
/// Reconciles <c>AspNetRoleClaims</c> with <see cref="RolePermissions"/>, the server-side role-to-claim
/// mapping.
/// </summary>
/// <remarks>
/// <para>
/// These rows used to be seeded by <c>HasData</c> on <c>OdysseyContext</c>. Because
/// <see cref="IdentityRoleClaim{TKey}.Id"/> is an int identity, that seed had to assign ids
/// positionally from a counter running across all four role lists — so adding a single claim
/// renumbered every claim after it and scaffolded a migration full of <c>UpdateData</c> operations
/// against unchanged rows. The established workaround was to hand-write a raw-SQL migration per claim
/// addition at fresh out-of-band ids and strip the renumbering out of the scaffold, which is why the
/// model snapshot and every real database deliberately disagreed on claim ids.
/// </para>
/// <para>
/// Reconciling at runtime removes the problem rather than working around it: identity is
/// <c>(RoleId, ClaimType, ClaimValue)</c>, the database assigns ids, and adding or removing a claim is
/// an edit to <see cref="RolePermissions"/> with no migration at all. That matches how the claims are
/// already compared — <c>AuthorizationPolicyTests</c> and the relational guard both assert on the
/// triple and never on <c>Id</c>.
/// </para>
/// <para>
/// Removals are applied as well as additions, and only within <see cref="PermissionClaims.Type"/>: a
/// claim dropped from a role has to actually stop being granted, or revoking a permission would need a
/// hand-written migration for exactly the reason this class exists. Rows of any other claim type are
/// left untouched.
/// </para>
/// <para>
/// Runs immediately after the <c>OdysseyContext</c> migration and before every seeder, so anything
/// downstream that reasons about authorization sees the finished mapping. A role-claim change still
/// only reaches existing sessions after a sign-out/sign-in, since claims are baked into issued cookies.
/// </para>
/// </remarks>
public sealed class RoleClaimSeeder(
    IServiceProvider serviceProvider,
    ILogger<RoleClaimSeeder> logger)
    : IRoleClaimSeeder
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        // The same four pairings the model used to hold, kept together so the mapping stays readable
        // at one call site.
        (string RoleId, string[] Claims)[] mapping =
        [
            (RoleDefinitions.AdminId, RolePermissions.AdminClaims),
            (RoleDefinitions.OwnerId, RolePermissions.OwnerClaims),
            (RoleDefinitions.UserId, RolePermissions.UserClaims),
            (RoleDefinitions.GuestId, RolePermissions.GuestClaims),
        ];

        var desired = new HashSet<(string RoleId, string Claim)>();
        foreach (var (roleId, claims) in mapping)
        {
            foreach (var claim in claims)
            {
                desired.Add((roleId, claim));
            }
        }

        var existingRows = await context.RoleClaims
            .Where(row => row.ClaimType == PermissionClaims.Type)
            .ToListAsync(cancellationToken);

        var existing = new HashSet<(string RoleId, string Claim)>(
            existingRows.Select(row => (row.RoleId, row.ClaimValue!)));

        var toAdd = desired.Except(existing).ToList();
        var toRemove = existingRows
            .Where(row => !desired.Contains((row.RoleId, row.ClaimValue!)))
            .ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            logger.LogInformation(
                "Role claims already match the permission mapping ({Count} rows); nothing to do.",
                existingRows.Count);
            return;
        }

        foreach (var (roleId, claim) in toAdd)
        {
            context.RoleClaims.Add(new IdentityRoleClaim<string>
            {
                RoleId = roleId,
                ClaimType = PermissionClaims.Type,
                ClaimValue = claim
            });
        }

        context.RoleClaims.RemoveRange(toRemove);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Reconciled role claims: {Added} added, {Removed} removed, {Total} now granted.",
            toAdd.Count,
            toRemove.Count,
            desired.Count);
    }
}
