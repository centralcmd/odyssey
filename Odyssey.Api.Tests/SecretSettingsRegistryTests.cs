using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Odyssey.Api.Controllers;
using Odyssey.Api.SystemSettings;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The structural guards behind <see cref="SecretSettingsRegistry"/> (issue #444 §16 ACs 30–34).
///
/// <para>
/// <strong>Everything reflective here asserts over <see cref="SecretSettingsRegistry.AllUnfiltered"/>,
/// not over a resolved registry.</strong> A resolved registry omits whatever the host's environment
/// filters out, so a guard reading it would silently stop covering the filtered descriptors — and when
/// this was written, under a Production environment it covered nothing at all. Issue #445 added five
/// unfiltered credentials, which makes the difference smaller and the reason to keep asserting over
/// the declaration exactly the same.
/// </para>
/// </summary>
public class SecretSettingsRegistryTests
{
    private const string ActorUserId = "55555555-5555-5555-5555-555555555555";

    /// <summary>
    /// AC 30 — the separation that makes the plaintext audit loop unreachable. <c>SystemSettingDescriptor</c>
    /// carries three members a secret must never meet: <c>Format</c>, whose output the audit loop writes
    /// verbatim; <c>Project</c>, which writes onto the response DTO; and <c>AuditChanges</c>, which is
    /// DERIVED from the claim — so a secret subclass carrying the security claim would log the
    /// credential at <c>Information</c> on its very first write.
    /// </summary>
    [Fact]
    public void NoSecretDescriptor_DerivesFromTheSystemSettingDescriptor()
    {
        Assert.NotEmpty(SecretSettingsRegistry.AllUnfiltered);

        var offenders = SecretSettingsRegistry.AllUnfiltered
            .Where(descriptor => typeof(SystemSettingDescriptor).IsAssignableFrom(descriptor.GetType()))
            .Select(descriptor => descriptor.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A secret descriptor derives from SystemSettingDescriptor, so it can be handed to the "
            + "plaintext audit loop: " + string.Join(", ", offenders));
    }

    /// <summary>AC 30 (the other direction) — no plaintext setting key appears in the secret registry.</summary>
    [Fact]
    public void NoPlaintextSettingKey_AppearsInTheSecretRegistry()
    {
        var plaintextKeys = SystemSettingsRegistry.All.Select(descriptor => descriptor.Key).ToHashSet(StringComparer.Ordinal);

        var offenders = SecretSettingsRegistry.AllUnfiltered
            .Where(descriptor => plaintextKeys.Contains(descriptor.Key))
            .Select(descriptor => descriptor.Key)
            .ToList();

        Assert.True(offenders.Count == 0, "Keys claimed by both registries: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC 30a. EXACTLY ONE descriptor sets <c>NonProduction</c>, and it is the test key. Without this a
    /// real credential descriptor carrying the flag would be silently filtered out of Production and
    /// present only as a <c>404</c> "key not registered" — a support call nobody would connect back to
    /// a boolean.
    /// </summary>
    [Fact]
    public void ExactlyOneDescriptor_IsNonProduction_AndItIsTheTestKey()
    {
        var nonProduction = SecretSettingsRegistry.AllUnfiltered.Where(d => d.NonProduction).ToList();

        Assert.Single(nonProduction);
        Assert.Equal(SecretSettingKeys.DiagnosticsSelfTest, nonProduction[0].Key);
    }

    /// <summary>
    /// AC 30b. Every descriptor declares a <c>Kind</c>, so no follow-up can add a secret without
    /// classifying its recoverability — the classification the Clear confirmation is load-bearing for.
    /// <c>required</c> makes it a compile error too; this catches a future default sneaking in.
    /// </summary>
    [Fact]
    public void EveryDescriptor_DeclaresARecoverabilityKind()
    {
        Assert.All(
            SecretSettingsRegistry.AllUnfiltered,
            descriptor => Assert.True(Enum.IsDefined(descriptor.Kind), $"{descriptor.Key} has no valid Kind."));
    }

    /// <summary>
    /// AC 31 (first half). Every declared <c>RequiredClaim</c> is a claim some role actually holds —
    /// otherwise the surface would be gated on a claim nobody can have, and every write would
    /// <c>403</c> for everyone, Admin included.
    /// </summary>
    [Fact]
    public void EveryRequiredClaim_IsHeldBySomeRole()
    {
        Assert.All(SecretSettingsRegistry.AllUnfiltered, descriptor =>
            Assert.Contains(descriptor.RequiredClaim, RolePermissions.AllClaims));
    }

    /// <summary>
    /// AC 31 (the half that matters) — the two-place declaration that must not drift. The claim is
    /// declared twice on purpose: on the controller action, so it is evaluated BEFORE key resolution
    /// (a per-descriptor claim is unknowable until after resolution), and on the descriptor, as
    /// defence in depth for non-HTTP callers.
    ///
    /// <para>
    /// Both drift directions fail closed, because the two are AND-combined — but a descriptor-only
    /// change yields a surface requiring BOTH claims, which is stricter than intended and awkward to
    /// diagnose. That is the outcome a future split to a dedicated <c>system-settings.secrets.update</c>
    /// claim would produce if only half the edit were made.
    /// </para>
    /// </summary>
    [Fact]
    public void TheActionPolicy_AndEveryDescriptorsRequiredClaim_Agree()
    {
        var policies = new[] { nameof(SecretSettingsController.Put), nameof(SecretSettingsController.Delete) }
            .Select(name => typeof(SecretSettingsController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance))
            .Select(method => method!.GetCustomAttribute<AuthorizeAttribute>()?.Policy)
            .ToList();

        Assert.All(policies, policy => Assert.Equal(PermissionClaims.SystemSettingsSecurityUpdate, policy));

        Assert.All(SecretSettingsRegistry.AllUnfiltered, descriptor =>
            Assert.Equal(policies[0], descriptor.RequiredClaim));
    }

    /// <summary>
    /// The class-level read claim, which is what stops a write-only caller probing the resource.
    /// </summary>
    [Fact]
    public void TheControllerClass_RequiresTheReadClaim()
    {
        var policy = typeof(SecretSettingsController).GetCustomAttribute<AuthorizeAttribute>()?.Policy;
        Assert.Equal(PermissionClaims.SystemSettingsRead, policy);
    }

    /// <summary>
    /// AC 32. No secret key appears in <c>SystemSettingsKeys.AllKeys</c>. Sharing that catalogue is
    /// exactly what the separate-table decision rejected: it would break
    /// <c>Registry_keys_match_the_key_catalogue_exactly</c> and
    /// <c>Every_descriptor_default_parses_onto_the_read_dto</c>, since a secret has no compiled default.
    /// </summary>
    [Fact]
    public void NoSecretKey_AppearsInTheSystemSettingsKeyCatalogue()
    {
        Assert.All(SecretSettingsRegistry.AllUnfiltered, descriptor =>
            Assert.DoesNotContain(descriptor.Key, SystemSettingsKeys.AllKeys));
    }

    /// <summary>
    /// AC 33 (second half). No key contains a colon, so it cannot collide with
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>'s section separator if it ever
    /// reaches the adoption table — and it matches the colon-free convention every
    /// <c>SystemSettingsKeys</c> value already follows.
    /// </summary>
    [Fact]
    public void NoSecretKey_ContainsAColon()
    {
        Assert.All(SecretSettingsRegistry.AllUnfiltered, descriptor =>
            Assert.DoesNotContain(':', descriptor.Key));
    }

    /// <summary>The client catalogue's key list and the server registry name the same keys.</summary>
    [Fact]
    public void TheDeclaredKeyList_MatchesTheRegistry()
    {
        Assert.Equal(
            SecretSettingKeys.AllKeys.OrderBy(key => key, StringComparer.Ordinal),
            SecretSettingsRegistry.AllUnfiltered.Select(d => d.Key).OrderBy(key => key, StringComparer.Ordinal));
    }

    /// <summary>
    /// AC 19. The reader's return type makes the three states distinguishable WITHOUT inspecting a
    /// string. A <c>string?</c> return would let a consumer write <c>?? configuredFallback</c> and
    /// treat an unreadable rotated credential as an unset one — silently sending with the old value
    /// the administrator believed they had replaced.
    /// </summary>
    [Fact]
    public void TheReaderReturnType_IsNotANullableString()
    {
        var method = typeof(ISecretSettingsReader).GetMethod(nameof(ISecretSettingsReader.GetAsync))!;

        Assert.Equal(typeof(Task<SecretResult>), method.ReturnType);
        Assert.NotEqual(typeof(Task<string>), method.ReturnType);
    }

    /// <summary>
    /// AC 33 (first half). Under a Production environment the test key is absent from the registry —
    /// and therefore from the status endpoint, the write path AND the reader. The reader matters most:
    /// it means a <c>DiagnosticsSelfTest</c> row carried into Production by a database restore from
    /// staging is INERT, not merely unreachable through the API.
    /// </summary>
    [Fact]
    public void UnderProduction_TheTestOnlyKey_IsFilteredOut()
    {
        var registry = new SecretSettingsRegistry(new StubEnvironment(Environments.Production));

        Assert.Null(registry.Find(SecretSettingKeys.DiagnosticsSelfTest));
        Assert.DoesNotContain(
            registry.All, descriptor => descriptor.Key == SecretSettingKeys.DiagnosticsSelfTest);

        // …and ONLY it. Since issue #445 the registry also carries five real credentials, and a filter
        // that swallowed those would be catastrophic in exactly the environment they matter in:
        // Production would show no Credentials group, every write would 404, and the reader would
        // report NotSet for a key that is stored — quietly sending mail unauthenticated and failing
        // every analysis job. Asserting the COMPLEMENT is what keeps this test about the filter rather
        // than about the registry happening to hold one entry.
        Assert.Equal(
            SecretSettingsRegistry.AllUnfiltered.Count - 1, registry.All.Count);
        Assert.All(registry.All, descriptor => Assert.False(descriptor.NonProduction));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void OutsideProduction_TheTestOnlyKey_IsRegistered(string environment)
    {
        var registry = new SecretSettingsRegistry(new StubEnvironment(environment));

        Assert.NotNull(registry.Find(SecretSettingKeys.DiagnosticsSelfTest));
    }

    /// <summary>
    /// AC 33, end to end: with the registry filtered, the endpoint does not report the test key and the
    /// write path answers <c>404</c> for it — the same answer an unregistered key gets anywhere else.
    ///
    /// <para>
    /// Since issue #445 the assertion is per-key rather than "the endpoint reports nothing": the
    /// registry now carries five real credentials that a Production deployment MUST see, so an empty
    /// listing would be the bug, not the guarantee.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnderAProductionRegistry_TheTestKeyIsAbsent_AndWritesToItAre404()
    {
        await using var factory = new ProductionRegistryFactory();
        var client = factory.CreateClient();

        var statuses = await client.GetFromJsonAsync<List<SecretSettingStatusDto>>("/api/system-settings/secrets");
        Assert.DoesNotContain(statuses!, status => status.Key == SecretSettingKeys.DiagnosticsSelfTest);

        // The real credentials are still there, which is the half a "reports no keys" assertion missed.
        Assert.Contains(statuses!, status => status.Key == SecretSettingKeys.FileAnalysisApiKey);
        Assert.Contains(statuses!, status => status.Key == SecretSettingKeys.LegalPseudonymizationSecret);

        var put = await client.PutAsJsonAsync(
            "/api/system-settings/secrets/" + SecretSettingKeys.DiagnosticsSelfTest,
            new SecretSettingUpdate { Value = "anything" });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        // And the READER is inert for it too, which is what makes a restored staging row harmless.
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();
        Assert.Equal(SecretReadState.NotSet, (await reader.GetAsync(SecretSettingKeys.DiagnosticsSelfTest)).State);
    }

    /// <summary>
    /// AC 10 — no new permission claim. The whole surface reuses
    /// <c>system-settings.security.update</c>, so <c>AspNetRoleClaims</c> is untouched, there is no
    /// claim migration, and — the part that matters operationally — <strong>no administrator has to
    /// sign out and back in</strong> to reach it. Claims are baked into the auth cookie at login, so a
    /// parallel claim would have locked every existing session out of the new surface until it
    /// re-authenticated.
    ///
    /// <para>
    /// Pinned by asserting the claim the registry actually names is one of the three that predate this
    /// feature. The claim count itself is pinned by <c>AuthorizationPolicyTests</c>, which passes
    /// unmodified.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFeature_IntroducesNoNewPermissionClaim()
    {
        string[] preExistingSettingsClaims =
        [
            PermissionClaims.SystemSettingsRead,
            PermissionClaims.SystemSettingsUpdate,
            PermissionClaims.SystemSettingsSecurityUpdate,
        ];

        Assert.All(SecretSettingsRegistry.AllUnfiltered, descriptor =>
            Assert.Contains(descriptor.RequiredClaim, preExistingSettingsClaims));

        // And nothing secret-shaped was added to the vocabulary.
        var offenders = typeof(PermissionClaims)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.Contains("secret", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A secrets-specific claim was added, which locks existing sessions out until they "
            + "re-authenticate: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC 34. The <c>Odyssey.ApiClient</c> boundary is unchanged — the new typed client added no web
    /// or UI dependency.
    /// </summary>
    [Fact]
    public void TheApiClientBoundary_StaysFreeOfWebAndUiTypes()
    {
        var apiClient = typeof(Odyssey.ApiClient.Resources.ISecretSettingsApiClient).Assembly;

        var offenders = apiClient.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("MudBlazor", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0, "Odyssey.ApiClient gained a UI dependency: " + string.Join(", ", offenders));
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Odyssey.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// Substitutes a Production-filtered registry rather than booting the host in Production, which
    /// would also change authentication, HTTPS redirection and Swagger and turn this into a test of
    /// something else.
    /// </summary>
    private sealed class ProductionRegistryFactory() : OdysseyApiFactory(
        [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsSecurityUpdate],
        ActorUserId,
        configureServices: services =>
        {
            services.RemoveAll<SecretSettingsRegistry>();
            services.AddSingleton(new SecretSettingsRegistry(new StubEnvironment(Environments.Production)));
        });
}
