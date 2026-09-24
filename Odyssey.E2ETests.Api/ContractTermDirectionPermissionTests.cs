using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// A term's money direction across the four seeded role users, over real HTTP against the real
/// role-claim rows (issue #159 AC 9, 10, 12).
/// </summary>
/// <remarks>
/// <para>
/// <c>Odyssey.Api.Tests</c> asserts the same gates against a <c>TestAuthHandler</c> principal carrying
/// a hand-written claim list. That proves the <c>[Authorize]</c> attributes name the right policies;
/// only a real sign-in proves the roles actually HOLD those claims, as <c>RoleClaimSeeder</c>
/// reconciled them. That is the half that matters here, because issue #159 adds no claim at all — the
/// whole no-sign-out-on-deploy claim rests on the new fields riding the existing ones.
/// </para>
/// <para>
/// The income aggregate is the surface worth pinning: it is a household's earnings, and it is behind
/// <c>contracts.read</c>, which <b>Guest does not hold</b>. If that assertion ever needs changing, the
/// posture changed with it.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class ContractTermDirectionPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    /// <summary>
    /// AC 9 / AC 11 — an Owner writes an incoming term and reads it back from every projection, plus
    /// the roll-up's incoming side, on the existing claims alone.
    /// </summary>
    [SkippableFact]
    public async Task An_owner_can_record_income_and_read_it_from_every_surface()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var owner = UserIn("Owner");
        var client = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);
        var contractId = await CreateContractAsync(client, "E2E Direction Employment");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms", Fee("Base salary", 50000m, incoming: true));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("Incoming", (await created.Content.ReadFromJsonAsync<JsonDocument>())!
                .RootElement.GetProperty("direction").GetString());

            Assert.Equal(HttpStatusCode.Created, (await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/terms", Fee("Union membership", 450m))).StatusCode);

            // All three contract-side projections of the same rows agree on both directions. The
            // contract detail payload is the one that would have been missed — its currentTerms is a
            // DIFFERENT DTO from the …/terms/current one.
            using var history = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}/terms");
            AssertBothSides(history!.RootElement.EnumerateArray());

            using var current = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}/terms/current");
            AssertBothSides(current!.RootElement.EnumerateArray());

            using var contract = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}");
            AssertBothSides(contract!.RootElement.GetProperty("currentTerms").EnumerateArray());

            // And the roll-up reports the income rather than folding it into what the file costs.
            using var summary = await client.GetFromJsonAsync<JsonDocument>("/api/contracts/summary");
            var runRate = summary!.RootElement.GetProperty("runRate");
            Assert.NotEqual(JsonValueKind.Null, runRate.GetProperty("incomingMonthly").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, runRate.GetProperty("netMonthly").ValueKind);
            Assert.True(summary.RootElement.GetProperty("upcomingReceipts").GetArrayLength() > 0);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// AC 10 — Guest holds no <c>Contracts*</c> claim, so the income aggregate and both contract reads
    /// that now carry a direction are refused. A household's earnings are not newly exposed to the
    /// lowest role by this feature — they are not exposed to it at all.
    /// </summary>
    [SkippableFact]
    public async Task The_guest_role_cannot_reach_the_income_aggregate()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(adminClient, "E2E Direction Guest Gate");

        try
        {
            var guest = UserIn("Guest");
            var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

            foreach (var route in new[]
            {
                "/api/contracts/summary",
                $"/api/contracts/{contractId}",
                $"/api/contracts/{contractId}/terms",
            })
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
            }
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contracts/{contractId}");
        }
    }

    private static void AssertBothSides(JsonElement.ArrayEnumerator rows)
    {
        var byLabel = rows.ToDictionary(
            r => r.GetProperty("label").GetString()!,
            r => r.GetProperty("direction").GetString());

        Assert.Equal("Incoming", byLabel["Base salary"]);
        Assert.Equal("Outgoing", byLabel["Union membership"]);
    }

    /// <summary>
    /// A priced monthly fee. The ordinals are written as numbers because this project speaks to the
    /// API over the wire rather than through the DTO assembly: <c>valueUnit = 1</c> is <c>Amount</c> and
    /// <c>interval = 3</c> is <c>Monthly</c>.
    /// </summary>
    private static object Fee(string label, decimal value, bool incoming = false) => new
    {
        label,
        valueUnit = 1,
        value,
        currencyCode = "NOK",
        interval = 3,
        intervalCount = 1,
        direction = incoming ? 1 : 0,
        effectiveFrom = "2026-02-01T00:00:00Z",
    };

    private static async Task<Guid> CreateContractAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/contracts", new
        {
            name,
            // Employment, started a year ago and signed, so it derives as Active and the roll-up
            // actually prices it — an unsigned contract is a Draft and contributes to nothing.
            type = 0,
            startDate = "2026-01-01T00:00:00Z",
            ready = "2025-12-30T00:00:00Z",
            signed = "2025-12-31T00:00:00Z",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("contractId").GetGuid();
    }
}
