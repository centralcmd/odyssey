using System.Reflection;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
    /// 102 before the standalone subscriptions feature was removed, taking its four
    /// <c>subscriptions.*</c> claims with it, and 98 before the standalone insurance-policy feature
    /// went the same way with its four <c>insurance.*</c> claims, and 94 before issue #190 moved
    /// account terms onto contracts and deleted the account-term read/write claim pair. 98 once issue
    /// #167 added the six <c>properties.*</c> claims.
    /// </summary>
    [Fact]
    public void Permission_claim_vocabulary_has_the_expected_size() =>
        Assert.Equal(98, DeclaredClaims().Count());

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
    /// (issue #157 §5.4).
    ///
    /// <para>
    /// The refusal payload names the blocking contracts only for a caller that also holds
    /// <c>contracts.read</c>. Withholding them from everyone would protect nobody — every shipped role
    /// holding <c>contacts.delete</c> holds <c>contracts.read</c> too — while making erasure harder for
    /// every real caller. The conditional is kept for a future role that breaks the pairing; this test
    /// is what makes that future loud instead of silent.
    /// </para>
    ///
    /// <para>
    /// The same pairing makes the detach path's composed gate — <c>contacts.delete</c> <b>and</b>
    /// <c>contracts.update</c> — satisfiable rather than a trap, so both are asserted here.
    /// </para>
    /// </summary>
    [Fact]
    public void No_role_holds_ContactsDelete_without_the_contract_claims_it_is_paired_with()
    {
        var roles = MappedRoles();

        var gaps = (from role in roles
                    where role.Claims.Contains(PermissionClaims.ContactsDelete, StringComparer.Ordinal)
                    from paired in new[] { PermissionClaims.ContractsRead, PermissionClaims.ContractsUpdate }
                    where !role.Claims.Contains(paired, StringComparer.Ordinal)
                    select $"{role.Role} holds '{PermissionClaims.ContactsDelete}' without '{paired}'").ToList();

        Assert.True(gaps.Count == 0,
            "Issue #157 §5.4 relies on every ContactsDelete holder also holding the contract claims: "
            + "the 409's contract list is withheld without contracts.read, and the detach path composes "
            + "contracts.update. Revisit that reasoning before granting these apart: "
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

    /// <summary>
    /// AC 21. Pins the premise issue #146 §7.3 rests on: a contract document's <c>issuedBy</c> is
    /// returned as a bare <c>Contact</c> id under <c>contracts.read</c>, with no name beside it and no
    /// server-side resolution — deliberately, since resolving it would move a contact attribute across
    /// the <c>contacts.read</c> boundary.
    ///
    /// <para>
    /// The residual is that a caller holding <c>contracts.read</c> but not <c>contacts.read</c> would
    /// learn some contact exists and was involved. That was accepted because no shipped role is in
    /// that position — the <c>contracts.read</c> holders are Admin, Owner and User, and all three also
    /// hold <c>contacts.read</c>; Guest holds neither <c>Contracts*</c> claim. An observation is not
    /// an invariant, so this makes it one: a future role split that reopens the exposure fails the
    /// build instead of regressing silently.
    /// </para>
    /// </summary>
    [Fact]
    public void No_role_holds_ContractsRead_without_ContactsRead()
    {
        var gaps = MappedRoles()
            .Where(role => role.Claims.Contains(PermissionClaims.ContractsRead, StringComparer.Ordinal))
            .Where(role => !role.Claims.Contains(PermissionClaims.ContactsRead, StringComparer.Ordinal))
            .Select(role => role.Role)
            .ToList();

        Assert.True(gaps.Count == 0,
            "Issue #146 §7.3 accepts returning a contract document's issuedBy as a bare contact id "
            + "because no shipped role reaches contracts.read without contacts.read. Re-make that "
            + "argument before granting them apart. Roles that do: " + string.Join(", ", gaps));
    }

    /// <summary>
    /// Issue #208 §7.2 keeps the party write gated on <c>contracts.update</c> alone, so a caller may name
    /// a property as a party exactly as it may name an account or contact — and the write echoes the
    /// property's name and type back. That is only harmless while no role reaches
    /// <c>contracts.update</c> without <c>properties.read</c>. This pins it: a role granted one without
    /// the other fails the build and forces the write-side gate to be revisited.
    /// </summary>
    [Fact]
    public void No_role_holds_ContractsUpdate_without_PropertiesRead()
    {
        var gaps = MappedRoles()
            .Where(role => role.Claims.Contains(PermissionClaims.ContractsUpdate, StringComparer.Ordinal))
            .Where(role => !role.Claims.Contains(PermissionClaims.PropertiesRead, StringComparer.Ordinal))
            .Select(role => role.Role)
            .ToList();

        Assert.True(gaps.Count == 0,
            "Issue #208 §7.2 lets contracts.update name a property as a party without properties.read "
            + "because no shipped role holds the first without the second. Re-make that argument, or "
            + "gate the property party write on properties.read, before granting them apart. Roles "
            + "that do: " + string.Join(", ", gaps));
    }

    /// <summary>
    /// Issue #217 §7.2 lets <c>properties.create</c>/<c>.update</c> set a homeowner association without
    /// also requiring <c>contacts.read</c>, and the 400/422 split answers whether a GUID names an
    /// Organization contact. That is harmless only while no role holds a property write claim without
    /// <c>contacts.read</c>.
    /// </summary>
    [Fact]
    public void No_role_holds_a_property_write_claim_without_ContactsRead()
    {
        var gaps = MappedRoles()
            .Where(role => role.Claims.Contains(PermissionClaims.PropertiesCreate, StringComparer.Ordinal)
                           || role.Claims.Contains(PermissionClaims.PropertiesUpdate, StringComparer.Ordinal))
            .Where(role => !role.Claims.Contains(PermissionClaims.ContactsRead, StringComparer.Ordinal))
            .Select(role => role.Role)
            .ToList();

        Assert.True(gaps.Count == 0,
            "Issue #217 §7.2 gates the homeowner-association write on properties.create/.update alone "
            + "because no shipped role holds either without contacts.read. Re-make that argument, or "
            + "gate the link on contacts.read, before granting them apart. Roles that do: "
            + string.Join(", ", gaps));
    }

    /// <summary>
    /// Issue #217 §7.3 — the read-side twin of the test above, raised as a non-blocking observation in
    /// the spec's security review. <c>ExistingProperty.HomeownerAssociation</c> shows a contact's name
    /// under <c>properties.read</c>; it is no disclosure while every <c>properties.read</c> holder also
    /// holds <c>contacts.read</c>.
    /// </summary>
    [Fact]
    public void No_role_holds_PropertiesRead_without_ContactsRead()
    {
        var gaps = MappedRoles()
            .Where(role => role.Claims.Contains(PermissionClaims.PropertiesRead, StringComparer.Ordinal))
            .Where(role => !role.Claims.Contains(PermissionClaims.ContactsRead, StringComparer.Ordinal))
            .Select(role => role.Role)
            .ToList();

        Assert.True(gaps.Count == 0,
            "Issue #217 §7.3 returns a homeowner association's contact name under properties.read "
            + "because no shipped role holds it without contacts.read. Re-make that argument, or "
            + "withhold the name from such callers, before granting them apart. Roles that do: "
            + string.Join(", ", gaps));
    }

    /// <summary>
    /// Issue #218 AC 15. Retiring the <c>Property</c>/<c>Vehicle</c> account types moved every such
    /// account onto a property record, so what was <c>accounts.read</c> (and <c>accounts.estimates.read</c>)
    /// data became <c>properties.read</c> (and <c>properties.estimates.read</c>) data. §7.2 accepts that
    /// on one observation: every role that could see it before still can. This pins that observation —
    /// a role granted the account claim without its property twin would silently lose sight of what
    /// the migration moved.
    /// </summary>
    [Fact]
    public void Every_role_with_an_account_read_claim_holds_its_property_twin()
    {
        (string Account, string Property)[] twins =
        [
            (PermissionClaims.AccountsRead, PermissionClaims.PropertiesRead),
            (PermissionClaims.AccountsEstimatesRead, PermissionClaims.PropertiesEstimatesRead),
        ];

        var gaps = (from role in MappedRoles()
                    from twin in twins
                    where role.Claims.Contains(twin.Account, StringComparer.Ordinal)
                    where !role.Claims.Contains(twin.Property, StringComparer.Ordinal)
                    select $"{role.Role} holds '{twin.Account}' without '{twin.Property}'").ToList();

        Assert.True(gaps.Count == 0,
            "Issue #218 §7.2 moved Property/Vehicle account data under the property claims on the "
            + "premise that no role loses sight of it. Roles that would: " + string.Join(", ", gaps));
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

    /// <summary>
    /// The attach actions every current link-by-id surface exposes, by route name. A floor for the
    /// discovery in <see cref="LinkByIdAttachActions"/>: if a refactor changed how routes are declared
    /// and the scan went quiet, the guards below would pass vacuously without it.
    /// </summary>
    private static readonly string[] KnownLinkByIdAttachRoutes =
    [
        "AttachAccountFile",
        "AttachTransactionFile",
        "AttachTaxStatementFile",
        "AttachContractFile",
        "AttachPropertyFile",
    ];

    private sealed record FilesPostAction(string Name, bool TakesBytes, string[] Policies);

    /// <summary>
    /// Every <c>POST …/files</c> action in the API, found by reflection over the controllers'
    /// attribute routes (class <c>[Route]</c> combined with the action template), with the policies
    /// its <c>[Authorize]</c> attributes demand at class and action level.
    ///
    /// <para>
    /// <c>TakesBytes</c> separates an upload — an <see cref="IFormFile"/> parameter or a
    /// <c>multipart/form-data</c> <c>[Consumes]</c> — from a link-by-id attach. Anything not
    /// recognisably an upload counts as link-by-id, so a new surface fails closed.
    /// </para>
    /// </summary>
    private static IReadOnlyList<FilesPostAction> FilesPostActions()
    {
        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(type));

        var actions = new List<FilesPostAction>();
        foreach (var controller in controllers)
        {
            var classTemplates = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(route => route.Template)
                .DefaultIfEmpty(string.Empty)
                .ToArray();
            var classPolicies = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Select(authorize => authorize.Policy)
                .OfType<string>()
                .ToArray();

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var posts = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .Where(attribute => attribute.HttpMethods.Contains("POST", StringComparer.OrdinalIgnoreCase));

                foreach (var post in posts)
                {
                    var routes = classTemplates.Select(classTemplate => post.Template switch
                    {
                        null => classTemplate,
                        var template when template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal) => template,
                        var template => $"{classTemplate}/{template}",
                    });

                    if (!routes.Any(route => string.Equals(
                            route.TrimEnd('/').Split('/')[^1], "files", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var takesBytes =
                        method.GetParameters().Any(parameter =>
                            typeof(IFormFile).IsAssignableFrom(parameter.ParameterType)
                            || typeof(IEnumerable<IFormFile>).IsAssignableFrom(parameter.ParameterType))
                        || method.GetCustomAttributes<ConsumesAttribute>(inherit: true)
                            .Any(consumes => consumes.ContentTypes.Contains("multipart/form-data", StringComparer.OrdinalIgnoreCase));

                    var policies = classPolicies
                        .Concat(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                            .Select(authorize => authorize.Policy)
                            .OfType<string>())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    actions.Add(new FilesPostAction(
                        post.Name ?? $"{controller.Name}.{method.Name}", takesBytes, policies));
                }
            }
        }

        return actions;
    }

    private static IReadOnlyList<FilesPostAction> LinkByIdAttachActions() =>
        FilesPostActions().Where(action => !action.TakesBytes).ToList();

    /// <summary>
    /// Issue #233. Attaching an existing file BY ID to a record makes that file's bytes downloadable
    /// through the record's own read claim, so the record's <c>*.update</c> claim alone would let a
    /// caller without <c>files.read</c> name any file id and read it back through the record — a
    /// confused deputy. Every link-by-id attach therefore stacks <c>files.read</c>, as the contract and
    /// property surfaces did first. Reflection rather than a list, so a new surface cannot drift.
    ///
    /// <para>
    /// A byte upload is deliberately out of scope: it cannot reach an existing file, which is why the
    /// contact avatar (bytes, never a <c>fileId</c>) is gated on <c>contacts.update</c> alone.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_link_by_id_attach_action_requires_FilesRead()
    {
        var discovered = LinkByIdAttachActions();

        var missingFromScan = KnownLinkByIdAttachRoutes
            .Except(discovered.Select(action => action.Name), StringComparer.Ordinal)
            .ToList();
        Assert.True(missingFromScan.Count == 0,
            "The POST …/files scan no longer discovers known link-by-id attach actions, so the guard would "
            + "pass vacuously. Fix the discovery: " + string.Join(", ", missingFromScan));

        var ungated = discovered
            .Where(action => !action.Policies.Contains(PermissionClaims.FilesRead, StringComparer.Ordinal))
            .Select(action => action.Name)
            .ToList();

        Assert.True(ungated.Count == 0,
            "A link-by-id attach makes the file readable through the record's read claim, so it must also "
            + "require files.read (issue #233). Add [Authorize(Policy = PermissionClaims.FilesRead)] to: "
            + string.Join(", ", ungated));
    }

    /// <summary>
    /// The other half of the classification: the byte-upload endpoint is recognised as one, so the
    /// guard above is telling the two shapes apart rather than excluding nothing.
    /// </summary>
    [Fact]
    public void Files_post_scan_classifies_the_upload_endpoint_as_taking_bytes()
    {
        var upload = Assert.Single(FilesPostActions(), action => action.Name == "PostFile");

        Assert.True(upload.TakesBytes);
    }

    /// <summary>
    /// Issue #233 AC 2. Stacking <c>files.read</c> onto the attach actions changes no shipped role's
    /// behaviour only because every role that already reached an attach action — by holding all its
    /// other policies — also holds <c>files.read</c>. Derived from the same scan, so a new surface is
    /// covered without editing this test.
    /// </summary>
    [Fact]
    public void Every_role_that_can_reach_a_link_by_id_attach_action_holds_FilesRead()
    {
        var gaps = (from action in LinkByIdAttachActions()
                    let recordPolicies = action.Policies
                        .Where(policy => !string.Equals(policy, PermissionClaims.FilesRead, StringComparison.Ordinal))
                        .ToArray()
                    where recordPolicies.Length > 0
                    from role in MappedRoles()
                    where recordPolicies.All(policy => role.Claims.Contains(policy, StringComparer.Ordinal))
                    where !role.Claims.Contains(PermissionClaims.FilesRead, StringComparer.Ordinal)
                    select $"{role.Role} can reach {action.Name} via {string.Join(" + ", recordPolicies)} "
                           + $"without '{PermissionClaims.FilesRead}'").ToList();

        Assert.True(gaps.Count == 0,
            "Issue #233 added files.read to the attach-by-id actions on the premise that no shipped role "
            + "loses access. Roles that would: " + string.Join(", ", gaps));
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
