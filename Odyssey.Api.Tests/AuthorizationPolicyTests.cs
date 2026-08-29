using System.Reflection;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

public class AuthorizationPolicyTests
{
    /// <summary>Every claim constant, discovered the same way the Blazor client discovers them.</summary>
    private static IEnumerable<string> DeclaredClaims() =>
        typeof(PermissionClaims)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly
                            && field.FieldType == typeof(string)
                            && field.Name != nameof(PermissionClaims.Type))
            .Select(field => (string)field.GetRawConstantValue()!);

    /// <summary>
    /// Guards the one way the claim vocabulary can still silently break now that it has a single
    /// definition: declaring a constant but forgetting to add it to <see cref="RolePermissions.AllClaims"/>.
    /// No role would then hold it — Admin included — so every endpoint gated on it 403s for everyone,
    /// with nothing at compile time to catch it.
    /// </summary>
    [Fact]
    public void Every_declared_permission_claim_is_granted_by_AllClaims()
    {
        var missing = DeclaredClaims().Except(RolePermissions.AllClaims).OrderBy(claim => claim, StringComparer.Ordinal);

        Assert.True(!missing.Any(),
            $"Declared in PermissionClaims but absent from RolePermissions.AllClaims: {string.Join(", ", missing)}");
    }

    /// <summary>The reverse: a role must not grant a claim that no longer exists in the vocabulary.</summary>
    [Fact]
    public void Every_role_claim_is_a_declared_permission_claim()
    {
        var declared = DeclaredClaims().ToHashSet(StringComparer.Ordinal);
        var roles = new (string Role, string[] Claims)[]
        {
            (nameof(RolePermissions.AllClaims), RolePermissions.AllClaims),
            (nameof(RolePermissions.AdminClaims), RolePermissions.AdminClaims),
            (nameof(RolePermissions.OwnerClaims), RolePermissions.OwnerClaims),
            (nameof(RolePermissions.UserClaims), RolePermissions.UserClaims),
            (nameof(RolePermissions.GuestClaims), RolePermissions.GuestClaims),
        };

        foreach (var (role, claims) in roles)
        {
            var unknown = claims.Where(claim => !declared.Contains(claim)).ToArray();
            Assert.True(unknown.Length == 0, $"{role} grants undeclared claim(s): {string.Join(", ", unknown)}");
        }
    }

    /// <summary>
    /// The claim values are the wire contract: they are persisted in <c>AspNetRoleClaims</c> by
    /// migrations and baked into issued auth cookies, so renaming one silently de-authorizes existing
    /// users and rows. Pins the count so a drive-by rename or deletion has to be deliberate.
    /// 101 = 98 + the three system-settings claims (issue #349).
    /// </summary>
    [Fact]
    public void Permission_claim_vocabulary_has_the_expected_size() =>
        Assert.Equal(101, DeclaredClaims().Count());

    /// <summary>
    /// Pins the premise the system-settings claim split rests on (issue #421 §10.10).
    ///
    /// <para>
    /// The three <c>system-settings.*</c> claims are Admin-only, and a fair amount of reasoning leans on
    /// that: the split between <c>update</c> and <c>security.update</c> is defence-in-depth against a
    /// future role rather than a live boundary, and the settings controller gates BOTH verbs on
    /// <c>system-settings.read</c> so a write-only caller cannot use 403-vs-200 as a value oracle.
    /// Nothing asserted it, so the day one of these claims is granted to Owner or User that reasoning
    /// would quietly stop holding, with no test to notice.
    /// </para>
    ///
    /// <para>
    /// Failing this test is not necessarily a bug — it is the signal to revisit that reasoning and
    /// decide whether the per-field split now needs to be a real boundary.
    /// </para>
    /// </summary>
    [Fact]
    public void System_settings_claims_are_granted_to_Admin_only()
    {
        string[] systemSettingsClaims =
        [
            PermissionClaims.SystemSettingsRead,
            PermissionClaims.SystemSettingsUpdate,
            PermissionClaims.SystemSettingsSecurityUpdate,
        ];

        (string Role, string[] Claims)[] nonAdminRoles =
        [
            ("Owner", RolePermissions.OwnerClaims),
            ("User", RolePermissions.UserClaims),
            ("Guest", RolePermissions.GuestClaims),
        ];

        var leaks = (from role in nonAdminRoles
                     from claim in systemSettingsClaims
                     where role.Claims.Contains(claim, StringComparer.Ordinal)
                     select $"{role.Role} holds '{claim}'").ToList();

        Assert.True(leaks.Count == 0,
            "system-settings.* is Admin-only by design, and issue #421 §10.10 relies on it. "
            + "Revisit that reasoning before granting these: " + string.Join(", ", leaks));

        // The other half of the premise: Admin must actually hold all three, or the settings page is
        // unreachable for everyone.
        Assert.All(systemSettingsClaims, claim => Assert.Contains(claim, RolePermissions.AdminClaims));
    }

    [Fact]
    public void PermissionClaimsConfigurePolicies()
    {
        var options = new AuthorizationOptions();

        foreach (var claimValue in RolePermissions.AllClaims)
        {
            options.AddPolicy(claimValue, policy =>
                policy.RequireClaim(PermissionClaims.Type, claimValue));
        }

        foreach (var claimValue in RolePermissions.AllClaims)
        {
            var policy = options.GetPolicy(claimValue);

            Assert.NotNull(policy);
            Assert.Contains(policy!.Requirements, requirement =>
                requirement is ClaimsAuthorizationRequirement claimsRequirement
                && claimsRequirement.ClaimType == PermissionClaims.Type
                && claimsRequirement.AllowedValues != null
                && claimsRequirement.AllowedValues.Contains(claimValue));
        }
    }
}
