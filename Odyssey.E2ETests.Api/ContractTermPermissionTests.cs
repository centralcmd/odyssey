using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The contract-term sub-resource's permission matrix across the four seeded role users, over real
/// HTTP against the real role-claim rows (issue #135 AC 5).
/// </summary>
/// <remarks>
/// <para>
/// <c>Odyssey.Api.Tests</c> asserts the same gates against a <c>TestAuthHandler</c> principal carrying
/// a hand-written claim list. That proves the <c>[Authorize]</c> attributes name the right policies;
/// it cannot prove the roles actually HOLD those claims, because the claim list is supplied by the
/// test. Only a real sign-in reads <c>AspNetRoleClaims</c> as <c>RoleClaimSeeder</c> reconciled it —
/// and that is the half that matters here, because the whole §7.2 decision rests on the five routes
/// reusing the existing <c>contracts.read</c>/<c>.update</c>, so that <b>no</b> role-claim change and
/// no forced sign-out were needed on deploy.
/// </para>
/// <para>
/// The other half of §7.2 is asserted rather than left implicit: <b>Guest holds no <c>Contracts*</c>
/// claim at all</b>, so it cannot even read a contract's term history. That is what makes the claim
/// reuse safer than a new claim would have been, not riskier — if this assertion ever needs changing,
/// the posture changed with it and §7.2 is re-opened.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class ContractTermPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableFact]
    public async Task An_owner_can_round_trip_a_term_through_all_five_routes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var owner = UserIn("Owner");
        var client = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);
        var contractId = await CreateContractAsync(client, "E2E Terms Lease");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms", Rent(14500m, EffectiveYesterday()));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var body = (await created.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
            var termId = body.GetProperty("termId").GetGuid();
            // The owner is the route's contract — the only owner a term has had since issue #190, so
            // the old account half is gone from the wire rather than null.
            Assert.Equal(contractId, body.GetProperty("contractId").GetGuid());
            Assert.False(body.TryGetProperty("accountId", out _));

            var history = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}/terms");
            Assert.Single(history!.RootElement.EnumerateArray().ToList());

            var current = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}/terms/current");
            Assert.Equal(14500m, Assert.Single(current!.RootElement.EnumerateArray().ToList())
                .GetProperty("value").GetDecimal());

            var updated = await client.PutAsJsonAsync(
                $"/api/contracts/{contractId}/terms/{termId}", Rent(15000m, EffectiveYesterday()));
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

            // The two contract reads carry the term projections.
            using var contract = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}");
            Assert.Equal(15000m, Assert.Single(contract!.RootElement.GetProperty("currentTerms").EnumerateArray().ToList())
                .GetProperty("value").GetDecimal());

            var deleted = await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms/{termId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// <c>User</c> holds <c>contracts.read</c> but not <c>contracts.update</c>
    /// (<c>RolePermissions.UserClaims</c>), so the reuse decision gives the middle role the reads and
    /// none of the writes — the same reach it has over the contract itself.
    /// </summary>
    [SkippableFact]
    public async Task The_user_role_can_read_but_not_write_terms()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(adminClient, "E2E Terms User Gate");

        try
        {
            var user = UserIn("User");
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/contracts/{contractId}/terms")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms", Rent(950m, "2026-02-01"))).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// §7.2's load-bearing fact: <b>Guest holds no <c>Contracts*</c> claim</b>, so the reads are
    /// refused as surely as the writes. A contract's price is not newly exposed to the lowest role by
    /// this feature — it is not exposed to it at all.
    /// </summary>
    [SkippableFact]
    public async Task The_guest_role_reaches_neither_the_reads_nor_the_writes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(adminClient, "E2E Terms Guest Gate");

        try
        {
            var guest = UserIn("Guest");
            var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/contracts/{contractId}/terms")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/contracts/{contractId}/terms/current")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms", Rent(1m, "2026-02-01"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
                $"/api/contracts/{contractId}/terms/{Guid.NewGuid()}", Rent(1m, "2026-02-01"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms/{Guid.NewGuid()}")).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// Containment over the wire, across CONTRACTS: another contract's term id is <c>404</c> through
    /// this contract's route — never <c>403</c>, which would confirm the row exists under a different
    /// parent, and never a silent success. And since issue #190 the account-owned term routes are
    /// gone: each is a plain <c>404</c> even for Admin, naming a seeded account that exists.
    /// </summary>
    [SkippableFact]
    public async Task A_foreign_term_is_not_found_and_the_account_term_routes_are_gone()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var client = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(client, "E2E Terms Containment");
        var otherContractId = await CreateContractAsync(client, "E2E Terms Containment Other");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{otherContractId}/terms", Rent(7m, "2026-02-01"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var foreignTermId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
                .RootElement.GetProperty("termId").GetGuid();

            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(
                $"/api/contracts/{contractId}/terms/{foreignTermId}", Rent(1m, "2026-02-01"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms/{foreignTermId}")).StatusCode);

            // Untouched, and still the other contract's.
            using var stillThere = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{otherContractId}/terms");
            var row = stillThere!.RootElement.EnumerateArray()
                .Single(t => t.GetProperty("termId").GetGuid() == foreignTermId);
            Assert.Equal(7m, row.GetProperty("value").GetDecimal());
            Assert.Equal(otherContractId, row.GetProperty("contractId").GetGuid());

            // Issue #190 AC 16 — no account-owned term route survives.
            using var accounts = await client.GetFromJsonAsync<JsonDocument>("/api/accounts?limit=1");
            var accountId = accounts!.RootElement.GetProperty("items").EnumerateArray().First()
                .GetProperty("accountId").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{accountId}/terms")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{accountId}/terms/current")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.PostWithAntiforgeryAsync(
                client, $"/api/accounts/{accountId}/terms", Rent(1m, "2026-02-01"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(
                $"/api/accounts/{accountId}/terms/{foreignTermId}", Rent(1m, "2026-02-01"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/accounts/{accountId}/terms/{foreignTermId}")).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{otherContractId}");
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    // Relative, not a literal: the round trip asserts the term is CURRENT, which a fixed date can only
    // satisfy from that date on (issue #178). A later literal is the same bomb with a longer fuse.
    private static string EffectiveYesterday() =>
        DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object Rent(decimal value, string effectiveFrom) => new
    {
        label = "Monthly rent",
        valueUnit = 1,
        value,
        currencyCode = "NOK",
        interval = 3,
        effectiveFrom = $"{effectiveFrom}T00:00:00Z",
    };

    private static async Task<Guid> CreateContractAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/contracts", new
        {
            name,
            type = 2,
            startDate = "2026-01-01T00:00:00Z",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("contractId").GetGuid();
    }
}
