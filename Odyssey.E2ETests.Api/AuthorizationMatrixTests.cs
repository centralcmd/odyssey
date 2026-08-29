using System.Net;
using Odyssey.Context.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// Exercises permission enforcement through the REAL login → cookie → authorization-policy path,
/// using the four seeded role users. This is what the in-process faked-auth API tests cannot prove:
/// that a real sign-in bakes the role's permission claims into the cookie and the
/// <c>[Authorize(Policy = …)]</c> gates actually allow/deny accordingly.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class AuthorizationMatrixTests(ApiStackFixture fixture)
{
    // Read-only, permission-gated endpoints and the claim each requires. Read-only keeps the suite
    // safe against the shared seeded database.
    private static readonly (string Endpoint, string RequiredClaim)[] GatedEndpoints =
    [
        ("/api/users", PermissionClaims.UsersRead),       // Admin-only — the clear discriminator.
        ("/api/accounts", PermissionClaims.AccountsRead), // every role has this.
        ("/api/budgets", PermissionClaims.BudgetsRead),   // every role has this.
        // Photos module (issue #321) — Admin/Owner/User hold these, Guest holds none (whole module 403).
        ("/api/photos", PermissionClaims.PhotosRead),
        ("/api/albums", PermissionClaims.PhotoAlbumsRead),
        ("/api/photo-tags", PermissionClaims.PhotoTagsRead),
        // Admin-only via the dedicated file-analysis.audit claim, seeded onto the Admin role by a
        // hand-written role-claim migration. Exercises that seeding over real login (the injected-claim
        // API tier can't) — Admin → 200, Owner/User/Guest → 403. (#264)
        ("/api/file-analysis/audit", PermissionClaims.FileAnalysisAudit),
        // System settings (issue #349) — Admin-only, seeded by the same hand-written-migration
        // pattern at fresh ids 264-266. GET never writes, so it's safe in the strict-200 matrix.
        ("/api/system-settings", PermissionClaims.SystemSettingsRead),
        // Encrypted secret settings (issue #444) — the STATUS endpoint, gated by the same read claim as
        // its plaintext sibling and for the same reason: a write-only caller must not be able to probe
        // the resource. It returns a flat 200 (one entry per registered key, whether or not a value is
        // stored), so it belongs in the strict-200 matrix. The two write endpoints are not here — they
        // are 204-returning mutations, and their claim boundary is covered in Odyssey.Api.Tests.
        ("/api/system-settings/secrets", PermissionClaims.SystemSettingsRead),
    ];

    // Read-gated endpoints whose authorized result is NOT a flat 200 — they 404 on a random id (or 503
    // when their feature is off), so they can't join the strict-200 GatedEndpoints matrix above. We
    // assert the claim BOUNDARY only: lacking the claim → 403; holding it → past authorization
    // (anything but 401/403). Proves the new resumable-reviews read endpoint enforces file-analysis.read
    // over real login → cookie → policy, which the injected-claim API tier can't. (#267)
    private static readonly (string Endpoint, string RequiredClaim)[] ReadBoundaryEndpoints =
    [
        ($"/api/accounts/{Guid.NewGuid()}/files/analysis/resumable", PermissionClaims.FileAnalysisRead),
    ];

    // Write-gated endpoints and the write claim each requires. Probed with DELETE against a random,
    // non-existent id so the call is non-mutating against the shared seeded database: a role lacking the
    // claim is stopped at authorization (403) before the handler runs; a role holding it gets past
    // authorization to a not-found / no-op result (anything but 401/403). This closes the real-cookie
    // write-permission gap the read-only matrix above leaves open (#240 M3).
    private static readonly (string Endpoint, string RequiredClaim)[] WriteGatedEndpoints =
    [
        ($"/api/accounts/{Guid.NewGuid()}", PermissionClaims.AccountsDelete),
        ($"/api/insurance-policies/{Guid.NewGuid()}", PermissionClaims.InsuranceDelete),
        ($"/api/contracts/{Guid.NewGuid()}", PermissionClaims.ContractsDelete),
        ($"/api/transactions/{Guid.NewGuid()}", PermissionClaims.TransactionsDelete),
        ($"/api/transaction-tags/{Guid.NewGuid()}", PermissionClaims.TransactionTagsDelete),
        ($"/api/contacts/{Guid.NewGuid()}", PermissionClaims.ContactsDelete),
        // Contact contact sub-resources (issue #325) reuse the contacts.* claims; probe each
        // DELETE so the sub-resource routes are pinned in the matrix, not just the base resource.
        ($"/api/contacts/{Guid.NewGuid()}/addresses/{Guid.NewGuid()}", PermissionClaims.ContactsDelete),
        ($"/api/contacts/{Guid.NewGuid()}/emails/{Guid.NewGuid()}", PermissionClaims.ContactsDelete),
        ($"/api/contacts/{Guid.NewGuid()}/phones/{Guid.NewGuid()}", PermissionClaims.ContactsDelete),
        ($"/api/exchange-rates/{Guid.NewGuid()}", PermissionClaims.ExchangeRatesDelete),
        ($"/api/photos/{Guid.NewGuid()}", PermissionClaims.PhotosDelete),
        ($"/api/albums/{Guid.NewGuid()}", PermissionClaims.PhotoAlbumsDelete),
        ($"/api/photo-tags/{Guid.NewGuid()}", PermissionClaims.PhotoTagsDelete),
        ($"/api/users/{Guid.NewGuid()}", PermissionClaims.UsersDelete), // Admin-only discriminator.
    ];

    [SkippableFact]
    public async Task Unauthenticated_requests_are_challenged_with_401()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        using var client = fixture.CreateAnonymousClient();

        foreach (var (endpoint, _) in GatedEndpoints)
        {
            var response = await client.GetAsync(endpoint);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous GET {endpoint}: expected 401, got {(int)response.StatusCode} {response.StatusCode}.");
        }

        foreach (var (endpoint, _) in WriteGatedEndpoints)
        {
            var response = await client.DeleteAsync(endpoint);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous DELETE {endpoint}: expected 401, got {(int)response.StatusCode} {response.StatusCode}.");
        }

        foreach (var (endpoint, _) in ReadBoundaryEndpoints)
        {
            var response = await client.GetAsync(endpoint);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous GET {endpoint}: expected 401, got {(int)response.StatusCode} {response.StatusCode}.");
        }
    }

    [SkippableFact]
    public async Task Each_role_clears_or_is_denied_read_boundary_endpoints_per_its_real_claims()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        foreach (var user in DemoUsers.All)
        {
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);
            var claims = ClaimsForRole(user.Role);

            foreach (var (endpoint, requiredClaim) in ReadBoundaryEndpoints)
            {
                var response = await client.GetAsync(endpoint);

                if (claims.Contains(requiredClaim))
                {
                    // Holds the claim: must clear authorization. The id is random and the result varies
                    // (404 not-found, or 503 if the feature is off) — only that it's neither 401 nor 403.
                    Assert.True(
                        response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                        $"{user.Role} GET {endpoint} (has '{requiredClaim}'): expected to pass authorization, " +
                        $"got {(int)response.StatusCode} {response.StatusCode}.");
                }
                else
                {
                    Assert.True(
                        response.StatusCode == HttpStatusCode.Forbidden,
                        $"{user.Role} GET {endpoint} (lacks '{requiredClaim}'): expected 403 Forbidden, " +
                        $"got {(int)response.StatusCode} {response.StatusCode}.");
                }
            }
        }
    }

    [SkippableFact]
    public async Task Each_role_is_allowed_or_forbidden_per_its_real_claims()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        foreach (var user in DemoUsers.All)
        {
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);
            var claims = ClaimsForRole(user.Role);

            foreach (var (endpoint, requiredClaim) in GatedEndpoints)
            {
                var response = await client.GetAsync(endpoint);
                var expected = claims.Contains(requiredClaim) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

                Assert.True(
                    response.StatusCode == expected,
                    $"{user.Role} GET {endpoint} (needs '{requiredClaim}'): expected {(int)expected} {expected}, " +
                    $"got {(int)response.StatusCode} {response.StatusCode}.");
            }
        }
    }

    [SkippableFact]
    public async Task Each_role_is_allowed_or_forbidden_on_writes_per_its_real_claims()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        foreach (var user in DemoUsers.All)
        {
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);
            var claims = ClaimsForRole(user.Role);

            foreach (var (endpoint, requiredClaim) in WriteGatedEndpoints)
            {
                if (claims.Contains(requiredClaim))
                {
                    // Authorized: send a valid antiforgery token so the write clears the antiforgery
                    // gate and actually reaches the handler. The target id is random, so the exact
                    // result varies (404 / 204 no-op) — only that it is neither 401 nor 403 matters.
                    // A tokenless DELETE would short-circuit at the antiforgery 400 and never exercise
                    // authorization at all, hiding a regression.
                    var response = await fixture.DeleteWithAntiforgeryAsync(client, endpoint);
                    Assert.True(
                        response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                        $"{user.Role} DELETE {endpoint} (has '{requiredClaim}'): expected to pass authorization, " +
                        $"got {(int)response.StatusCode} {response.StatusCode}.");
                }
                else
                {
                    // Lacking the claim: authorization runs before antiforgery, so the request is
                    // forbidden (403) before any token check — this branch stays intentionally
                    // tokenless to assert that pre-antiforgery authorization denial.
                    var response = await client.DeleteAsync(endpoint);
                    Assert.True(
                        response.StatusCode == HttpStatusCode.Forbidden,
                        $"{user.Role} DELETE {endpoint} (lacks '{requiredClaim}'): expected 403 Forbidden, " +
                        $"got {(int)response.StatusCode} {response.StatusCode}.");
                }
            }
        }
    }

    private static string[] ClaimsForRole(string role) => role switch
    {
        RoleDefinitions.Admin => RolePermissions.AdminClaims,
        RoleDefinitions.Owner => RolePermissions.OwnerClaims,
        RoleDefinitions.User => RolePermissions.UserClaims,
        RoleDefinitions.Guest => RolePermissions.GuestClaims,
        _ => throw new InvalidOperationException($"Unknown demo role '{role}'."),
    };
}
