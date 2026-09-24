using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The contract-event sub-resource's permission matrix across the four seeded role users, over real
/// HTTP against the real role-claim rows (issue #138 AC 7).
/// </summary>
/// <remarks>
/// <para>
/// <c>Odyssey.Api.Tests</c> asserts the same gates against a <c>TestAuthHandler</c> principal carrying
/// a hand-written claim list. That proves the <c>[Authorize]</c> attributes name the right policies;
/// it cannot prove the roles actually HOLD those claims, because the claim list is supplied by the
/// test. Only a real sign-in reads <c>AspNetRoleClaims</c> as <c>RoleClaimSeeder</c> reconciled it —
/// and that is the half that matters here, because §7.2's whole decision rests on the four routes
/// reusing the existing <c>contracts.read</c>/<c>.update</c>, so that <b>no</b> role-claim change and
/// no forced sign-out were needed on deploy.
/// </para>
/// <para>
/// The <c>Notes</c> field is asserted on the wire rather than left to the unit tier, because §4.1's
/// rule is exactly the one a later change is most likely to get wrong in the safe-looking direction:
/// the field is hidden on the timeline, and hiding it from the projection too would look like
/// tightening. It is not — it is a presentation rule, and every caller holding <c>contracts.read</c>
/// receives the field.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class ContractEventPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableFact]
    public async Task An_owner_can_round_trip_an_event_through_all_four_routes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var owner = UserIn("Owner");
        var client = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);
        var contractId = await CreateContractAsync(client, "E2E Events Lease");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/events", Recorded("Emailed the landlord"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var body = (await created.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
            var eventId = body.GetProperty("contractEventId").GetGuid();
            Assert.Equal(contractId, body.GetProperty("contractId").GetGuid());

            // The attribution is a resolved LABEL, and the raw id is not on the wire at all (§7.3).
            Assert.Equal(owner.DisplayName, body.GetProperty("createdBy").GetString());
            Assert.False(body.TryGetProperty("createdByUserId", out _));

            var page = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}/events");
            var items = page!.RootElement.GetProperty("items").EnumerateArray().ToList();
            var only = Assert.Single(items);
            // All three free-text fields reach the caller — notes included (§4.1, AC 4).
            Assert.Equal("Emailed the landlord", only.GetProperty("title").GetString());
            Assert.Equal("What was said, at more length.", only.GetProperty("description").GetString());
            Assert.Equal("Chase this on the 21st.", only.GetProperty("notes").GetString());

            // The PUT is a FULL replacement: the omitted description and notes are cleared, and the
            // omitted type resets to Other (§5.3, AC 3).
            var updated = await client.PutAsJsonAsync(
                $"/api/contracts/{contractId}/events/{eventId}",
                new { title = "Emailed the landlord (revised)", occurredAt = "2026-02-01T09:00:00Z" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

            var after = (await updated.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
            Assert.Equal(JsonValueKind.Null, after.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, after.GetProperty("notes").ValueKind);
            Assert.Equal(8, after.GetProperty("type").GetInt32());
            // ...and the provenance is untouched by the edit (AC 5).
            Assert.Equal(owner.DisplayName, after.GetProperty("createdBy").GetString());
            // At the column's precision: the POST echoes the in-memory stamp (100 ns ticks) while the
            // PUT re-reads the stored datetime(6), which MariaDB truncates to whole microseconds.
            Assert.Equal(
                ToStoredPrecision(body.GetProperty("createdAtUtc").GetDateTime()),
                ToStoredPrecision(after.GetProperty("createdAtUtc").GetDateTime()));

            var deleted = await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/events/{eventId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// <c>User</c> holds <c>contracts.read</c> but not <c>contracts.update</c>
    /// (<c>RolePermissions.UserClaims</c>), so the reuse decision gives the middle role the list and
    /// nothing else — the same reach it has over the contract itself.
    /// </summary>
    [SkippableFact]
    public async Task The_user_role_can_read_but_not_write_events()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(adminClient, "E2E Events User Gate");

        try
        {
            var user = UserIn("User");
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/contracts/{contractId}/events")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/events", Recorded("Rang about the renewal"))).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// §7.2's load-bearing fact: <b>Guest holds no <c>Contracts*</c> claim</b>, so the read is refused
    /// as surely as the writes. A contract's log — the notes field included — is not newly exposed to
    /// the lowest role by this feature; it is not exposed to it at all.
    /// </summary>
    [SkippableFact]
    public async Task The_guest_role_reaches_neither_the_read_nor_the_writes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(adminClient, "E2E Events Guest Gate");

        try
        {
            var guest = UserIn("Guest");
            var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/contracts/{contractId}/events")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/events", Recorded("Should never land"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
                $"/api/contracts/{contractId}/events/{Guid.NewGuid()}", Recorded("Should never land"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/events/{Guid.NewGuid()}")).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// Containment over the wire: an event id is <c>404</c> through another contract's route — never
    /// <c>403</c>, which would confirm the row exists under a different parent, and never a silent
    /// success, which would be the cross-record write the route-only owner exists to prevent.
    /// </summary>
    [SkippableFact]
    public async Task An_event_is_not_found_through_another_contracts_route()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var client = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(client, "E2E Events Containment");
        var otherContractId = await CreateContractAsync(client, "E2E Events Containment Other");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contracts/{contractId}/events", Recorded("Belongs to the first contract"));
            var eventId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
                .RootElement.GetProperty("contractEventId").GetGuid();

            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(
                $"/api/contracts/{otherContractId}/events/{eventId}", Recorded("Hijacked"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contracts/{otherContractId}/events/{eventId}")).StatusCode);

            // And the entry is untouched under its real owner.
            var page = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}/events");
            var only = Assert.Single(page!.RootElement.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal("Belongs to the first contract", only.GetProperty("title").GetString());
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{otherContractId}");
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>
    /// §8.3 over the wire — the bound is the SERVER's clock, so this is the only tier that can observe
    /// it. A client-side check can be bypassed by construction; this is the one that actually refuses.
    /// </summary>
    [SkippableFact]
    public async Task A_future_occurrence_is_refused_by_the_server()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var client = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contractId = await CreateContractAsync(client, "E2E Events Future Bound");

        try
        {
            var refused = await fixture.PostWithAntiforgeryAsync(client, $"/api/contracts/{contractId}/events", new
            {
                type = 8,
                title = "Something that has not happened",
                occurredAt = DateTime.UtcNow.AddDays(7),
            });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

            // ...and "now" is accepted, so the 60-second tolerance is not merely documented.
            var accepted = await fixture.PostWithAntiforgeryAsync(client, $"/api/contracts/{contractId}/events", new
            {
                type = 8,
                title = "Happened just now",
                occurredAt = DateTime.UtcNow,
            });
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    private static DateTime ToStoredPrecision(DateTime value) =>
        new(value.Ticks - (value.Ticks % 10), value.Kind);

    private static object Recorded(string title) => new
    {
        type = 7,
        title,
        description = "What was said, at more length.",
        notes = "Chase this on the 21st.",
        occurredAt = "2026-02-01T09:00:00Z",
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
