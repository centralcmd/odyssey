using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Odyssey.Api.Legal;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The claims factory is where issue #354's enforcement actually originates, and its registration is a
/// known silent-failure mode: this repo maps Identity twice, and handing the custom factory to the wrong
/// builder call leaves the app building, booting and logging in with the feature doing nothing at all.
/// These tests pin the registration, the claim itself, the platform claims the subclassing must not
/// drop, and the two different ways a compliance-computation failure has to behave.
///
/// <para>
/// The real-cookie tests share one host (<see cref="LegalLoginFactory"/>, a class fixture), reset before each test to
/// no published Terms of Service and a readable license (<see cref="LegalLoginFactory.ResetAsync"/>); each signs in a
/// user of its own.
/// </para>
/// </summary>
public class LegalClaimsFactoryTests(LegalLoginFactory sharedHost) : IClassFixture<LegalLoginFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => sharedHost.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string UniqueAddress(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

    /// <summary>AC 23 — the container resolves the custom factory, not Identity's default.</summary>
    [Fact]
    public void UserClaimsPrincipalFactory_ResolvesToTheLegalComplianceFactory()
    {
        using var scope = sharedHost.Services.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        Assert.IsType<LegalComplianceClaimsPrincipalFactory>(resolved);
    }

    /// <summary>AC 1 — a brand-new user is non-compliant at their very first login.</summary>
    [Fact]
    public async Task FirstLogin_OfANewUser_CarriesPendingAcceptanceForBothDocuments()
    {
        var factory = sharedHost;
        var email = UniqueAddress("first");
        await factory.CreateUserAsync(email);
        await factory.PublishTermsOfServiceAsync("Terms v1");

        using var client = await factory.LoginAsync(email);

        var pending = await PendingClaimsAsync(client);
        Assert.Equal([LegalClaims.License, LegalClaims.TermsOfService], pending);
    }

    /// <summary>AC 3 — a fully compliant user's principal carries no pending-acceptance claim at all.</summary>
    [Fact]
    public async Task Login_OfACompliantUser_CarriesNoPendingAcceptanceClaim()
    {
        var factory = sharedHost;
        var email = UniqueAddress("compliant");
        await factory.PublishTermsOfServiceAsync("Terms v1");
        await factory.CreateUserAsync(email, acceptLegalDocuments: true);

        using var client = await factory.LoginAsync(email);

        Assert.Empty(await PendingClaimsAsync(client));
    }

    /// <summary>With no ToS ever published there is nothing to accept, so only the License is outstanding (AC 17).</summary>
    [Fact]
    public async Task WithNoTermsOfServicePublished_OnlyTheLicenseIsOutstanding()
    {
        var factory = sharedHost;
        var email = UniqueAddress("nolicense");
        await factory.CreateUserAsync(email);

        using var client = await factory.LoginAsync(email);

        Assert.Equal([LegalClaims.License], await PendingClaimsAsync(client));
    }

    /// <summary>
    /// AC 22 — subclassing the default factory (rather than implementing one) must leave the rest of the
    /// principal intact: the role and permission claims every <c>[Authorize]</c> policy reads, and the
    /// security-stamp claim <c>SecurityStampValidator</c> needs to tell "recompute" from "sign out".
    /// </summary>
    [Fact]
    public async Task ThePrincipal_StillCarriesRolePermissionAndSecurityStampClaims()
    {
        var factory = sharedHost;
        var email = UniqueAddress("admin");
        await factory.CreateUserAsync(
            email, RoleDefinitions.AdminId, acceptLegalDocuments: true);

        using var client = await factory.LoginAsync(email);
        var claims = await ClaimsAsync(client);

        var securityStampClaimType = factory.Services.GetRequiredService<IOptions<IdentityOptions>>()
            .Value.ClaimsIdentity.SecurityStampClaimType;

        Assert.Contains(claims, claim => claim.Type == PermissionClaims.Type && claim.Value == PermissionClaims.UsersManage);
        Assert.Contains(claims, claim => claim.Value == RoleDefinitions.Admin);
        Assert.Contains(claims, claim => claim.Type == securityStampClaimType);
    }

    /// <summary>
    /// AC 24 (revalidation half) — a failure during background revalidation must neither sign the session
    /// out nor silently grant compliance. Here the session was gated before the failure, so it must stay
    /// gated: a 401 would mean it was signed out, a 200 would mean compliance was invented.
    /// </summary>
    [Fact]
    public async Task AFailureDuringRevalidation_KeepsAGatedSessionGatedRatherThanSigningItOut()
    {
        var factory = sharedHost;
        var email = UniqueAddress("gated");
        await factory.CreateUserAsync(email);

        using var client = await factory.LoginAsync(email);
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, (await client.PutAsJsonAsync("/api/profile", new { })).StatusCode);

        factory.License.ShouldThrow = true;

        var afterFailure = await client.PutAsJsonAsync("/api/profile", new { });
        Assert.Equal(HttpStatusCode.UnavailableForLegalReasons, afterFailure.StatusCode);
    }

    /// <summary>
    /// The other direction of the same rule: a compliant session must not be gated by a transient
    /// failure either — the existing (empty) claim value is preserved, not recomputed as "outstanding".
    /// </summary>
    [Fact]
    public async Task AFailureDuringRevalidation_DoesNotGateAnAlreadyCompliantSession()
    {
        var factory = sharedHost;
        var email = UniqueAddress("healthy");
        await factory.CreateUserAsync(email, acceptLegalDocuments: true);

        using var client = await factory.LoginAsync(email);
        Assert.True((await client.GetAsync("/api/profile")).IsSuccessStatusCode);

        factory.License.ShouldThrow = true;

        var afterFailure = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.OK, afterFailure.StatusCode);
    }

    /// <summary>AC 24 (login half) — a failure while building the principal surfaces as a failed login.</summary>
    [Fact]
    public async Task AFailureDuringLogin_FailsTheLogin()
    {
        var factory = sharedHost;
        var email = UniqueAddress("brokenlogin");
        await factory.CreateUserAsync(email, acceptLegalDocuments: true);

        factory.License.ShouldThrow = true;

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/login?useCookies=true",
            new { email, password = LegalLoginFactory.Password });

        Assert.False(response.IsSuccessStatusCode);
    }

    private static async Task<IReadOnlyList<string>> PendingClaimsAsync(HttpClient client) =>
        (await ClaimsAsync(client))
            .Where(claim => claim.Type == LegalClaims.PendingAcceptanceType)
            .Select(claim => claim.Value)
            .ToList();

    private static async Task<IReadOnlyList<ClaimRow>> ClaimsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/auth/claims");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ClaimRow>>() ?? [];
    }

    private sealed record ClaimRow(string Type, string Value);
}
