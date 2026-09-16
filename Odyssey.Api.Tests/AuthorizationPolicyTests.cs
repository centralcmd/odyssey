using System.Reflection;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

public class AuthorizationPolicyTests
{
    /// <summary>
    /// Every role and the claims it holds, read from <see cref="RolePermissions.RoleClaimMap"/> — the
    /// single enumeration (issue #90 G9). Never a literal list of the four shipped roles: a guard that
    /// names its own roles cannot see a fifth one, so it keeps passing while the conclusion it exists
    /// to protect quietly stops holding.
    /// </summary>
    private static (string Role, string[] Claims)[] MappedRoles() =>
        RolePermissions.RoleClaimMap.Select(role => (role.RoleName, role.Claims)).ToArray();

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
        var roles = new (string Role, string[] Claims)[] { (nameof(RolePermissions.AllClaims), RolePermissions.AllClaims) }
            .Concat(MappedRoles())
            .ToArray();

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

        var nonAdminRoles = MappedRoles()
            .Where(role => !string.Equals(role.Role, RoleDefinitions.Admin, StringComparison.Ordinal))
            .ToArray();

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

    /// <summary>
    /// Pins the premise the claim-conditional <c>409</c> on a blocked contact delete rests on
    /// (issue #27 §10 #6).
    ///
    /// <para>
    /// The refusal payload names the blocking policies only for a caller that also holds
    /// <c>insurance.read</c>. Draft v1 withheld them from everyone, on the premise that a
    /// <c>contacts.delete</c> holder might hold no <c>insurance.read</c> — which is false for every
    /// shipped role, so the restriction protected nobody while making erasure harder for every real
    /// caller. The conditional is kept for a future role that breaks the pairing; this test is what
    /// makes that future loud instead of silent.
    /// </para>
    ///
    /// <para>
    /// The same pairing makes the detach path's composed gate — <c>contacts.delete</c> <b>and</b>
    /// <c>insurance.update</c> — satisfiable rather than a trap, so both are asserted here.
    /// </para>
    /// </summary>
    [Fact]
    public void No_role_holds_ContactsDelete_without_the_insurance_claims_it_is_paired_with()
    {
        var roles = MappedRoles();

        var gaps = (from role in roles
                    where role.Claims.Contains(PermissionClaims.ContactsDelete, StringComparer.Ordinal)
                    from paired in new[] { PermissionClaims.InsuranceRead, PermissionClaims.InsuranceUpdate }
                    where !role.Claims.Contains(paired, StringComparer.Ordinal)
                    select $"{role.Role} holds '{PermissionClaims.ContactsDelete}' without '{paired}'").ToList();

        Assert.True(gaps.Count == 0,
            "Issue #27 §10 #6 relies on every ContactsDelete holder also holding the insurance claims: "
            + "the 409's policy list is withheld without insurance.read, and the detach path composes "
            + "insurance.update. Revisit that reasoning before granting these apart: "
            + string.Join(", ", gaps));
    }

    /// <summary>
    /// AC 26. Pins the premise issue #75 §10.2 rests on: a budget item read now carries the transaction
    /// tag's name, description and archival date under <c>budgets.read</c>, which is not
    /// <c>transactions.tags.read</c>.
    ///
    /// <para>
    /// That crossover was accepted on the strength of an observation — every shipped role holds both
    /// claims together, so no role actually gains a projection it could not already reach. An
    /// observation is not an invariant, so this makes it one: the day a role is granted
    /// <c>budgets.read</c> without the tag claim, the exposure argument has to be re-made rather than
    /// quietly falling over.
    /// </para>
    /// </summary>
    [Fact]
    public void No_role_holds_BudgetsRead_without_TransactionTagsRead()
    {
        var roles = MappedRoles();

        var gaps = roles
            .Where(role => role.Claims.Contains(PermissionClaims.BudgetsRead, StringComparer.Ordinal))
            .Where(role => !role.Claims.Contains(PermissionClaims.TransactionTagsRead, StringComparer.Ordinal))
            .Select(role => role.Role)
            .ToList();

        Assert.True(gaps.Count == 0,
            "Issue #75 §10.2 accepts embedding the transaction tag on every budget item because no "
            + "shipped role reaches budgets.read without transactions.tags.read. Re-make that argument "
            + "before granting them apart. Roles that do: " + string.Join(", ", gaps));
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

    /// <summary>
    /// AC13. <c>GET /api/accounts/net-worth-history</c> folds three claim-gated sources — transactions,
    /// account estimates and exchange rates — behind the single <c>accounts.read</c> gate, and issue
    /// #90 §10.3 accepts that on the strength of one observation: every role that can reach the
    /// endpoint already holds all three and can read the rows directly, so the endpoint discloses
    /// nothing new to anyone who can call it.
    ///
    /// <para>
    /// The roles come from <see cref="RolePermissions.RoleClaimMap"/>, not a literal — that is the
    /// whole point of the guard. Field values, not source text: <c>AdminClaims</c> is a
    /// <c>[..AllClaims, …]</c> spread materialised at field initialisation, which a source-lint would
    /// miss entirely.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_role_with_AccountsRead_also_holds_the_three_claims_the_net_worth_history_folds()
    {
        string[] folded =
        [
            PermissionClaims.TransactionsRead,
            PermissionClaims.AccountsEstimatesRead,
            PermissionClaims.ExchangeRatesRead,
        ];

        var gaps = (from role in MappedRoles()
                    where role.Claims.Contains(PermissionClaims.AccountsRead, StringComparer.Ordinal)
                    from claim in folded
                    where !role.Claims.Contains(claim, StringComparer.Ordinal)
                    select $"{role.Role} holds '{PermissionClaims.AccountsRead}' without '{claim}'").ToList();

        Assert.True(gaps.Count == 0,
            "Issue #90 §10.3 accepts the net-worth-history disclosure ONLY because every accounts.read "
            + "holder can already read the transactions, estimates and rates the series is folded from. "
            + "If this fires, that acceptance is VOID: re-make the argument (or gate the finer intervals) "
            + "before granting these apart. Roles that do: " + string.Join(", ", gaps));
    }

    /// <summary>
    /// AC14. The other half of AC13's premise: a role must not be able to receive claims through a
    /// path the map does not describe. <c>RoleClaimSeeder</c> is the only writer of
    /// <c>AspNetRoleClaims</c>, so it has to reconcile against exactly
    /// <see cref="RolePermissions.RoleClaimMap"/> — a second hand-written pairing there would grant a
    /// role claims that AC13 never enumerates.
    /// </summary>
    [Fact]
    public void RoleClaimSeeder_reconciles_against_RoleClaimMap_and_nothing_else()
    {
        var source = File.ReadAllText(SeederSourcePath());

        Assert.Contains("RolePermissions.RoleClaimMap", source, StringComparison.Ordinal);

        string[] perRoleArrays =
        [
            nameof(RolePermissions.AdminClaims),
            nameof(RolePermissions.OwnerClaims),
            nameof(RolePermissions.UserClaims),
            nameof(RolePermissions.GuestClaims),
        ];

        var rebuilt = perRoleArrays
            .Where(name => source.Contains("RolePermissions." + name, StringComparison.Ordinal))
            .ToList();

        Assert.True(rebuilt.Count == 0,
            "RoleClaimSeeder names a per-role claim array directly instead of walking "
            + "RolePermissions.RoleClaimMap, which re-opens the second-copy problem issue #90 G9 closed: "
            + string.Join(", ", rebuilt));
    }

    private static string SeederSourcePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "Odyssey.MigrationService", "RoleClaimSeeder.cs");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "Odyssey.sln")))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("Could not locate RoleClaimSeeder.cs from " + AppContext.BaseDirectory);
    }
}
