using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The property-event sub-resource's permission matrix across the four seeded role users, over real
/// HTTP against the real role-claim rows (issue #209 AC 17).
/// </summary>
/// <remarks>
/// The routes reuse <c>properties.read</c>/<c>.update</c>, so no role-claim change and no sign-out were
/// needed. Only a real sign-in proves the roles hold what that decision assumes: Guest and User hold
/// <c>properties.read</c> but not <c>properties.update</c>, so they list and every write is a
/// <c>403</c>.
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class PropertyEventPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableFact]
    public async Task An_owner_can_round_trip_an_event_through_all_four_routes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var owner = UserIn("Owner");
        var client = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);
        var propertyId = await CreateVehicleAsync(client, "E2E Events Car");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(client, Events(propertyId), new
            {
                type = 118,
                title = "Winter tyres on",
                notes = "Next change mid-April",
                occurredAt = "2026-02-01T09:00:00Z",
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var body = (await created.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
            var eventId = body.GetProperty("propertyEventId").GetGuid();
            Assert.Equal(propertyId, body.GetProperty("propertyId").GetGuid());
            Assert.Equal(owner.DisplayName, body.GetProperty("createdBy").GetString());
            Assert.False(body.TryGetProperty("createdByUserId", out _));

            var page = await client.GetFromJsonAsync<JsonDocument>(Events(propertyId));
            var only = Assert.Single(page!.RootElement.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal("Next change mid-April", only.GetProperty("notes").GetString());

            var updated = await client.PutAsJsonAsync($"{Events(propertyId)}/{eventId}",
                new { title = "Winter tyres on (revised)", occurredAt = "2026-02-01T09:00:00Z" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var after = (await updated.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
            Assert.Equal(108, after.GetProperty("type").GetInt32());
            Assert.Equal(JsonValueKind.Null, after.GetProperty("notes").ValueKind);

            Assert.Equal(HttpStatusCode.NoContent,
                (await fixture.DeleteWithAntiforgeryAsync(client, $"{Events(propertyId)}/{eventId}")).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/properties/{propertyId}");
        }
    }

    [SkippableTheory]
    [InlineData("User")]
    [InlineData("Guest")]
    public async Task A_reader_role_lists_but_every_write_is_forbidden(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var propertyId = await CreateVehicleAsync(adminClient, $"E2E Events {role} Gate");

        try
        {
            var reader = UserIn(role);
            var client = await fixture.CreateAuthenticatedClientAsync(reader.Email, reader.Password);
            object body = new { type = 108, title = "Should never land", occurredAt = "2026-02-01T09:00:00Z" };

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Events(propertyId))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await fixture.PostWithAntiforgeryAsync(client, Events(propertyId), body)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.PutAsJsonAsync($"{Events(propertyId)}/{Guid.NewGuid()}", body)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await fixture.DeleteWithAntiforgeryAsync(client, $"{Events(propertyId)}/{Guid.NewGuid()}")).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/properties/{propertyId}");
        }
    }

    /// <summary>A server-only type is refused over the wire, whoever writes it (§4.2).</summary>
    [SkippableFact]
    public async Task A_system_only_type_is_refused_even_for_an_admin()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var client = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var propertyId = await CreateVehicleAsync(client, "E2E Events System Only");

        try
        {
            var refused = await fixture.PostWithAntiforgeryAsync(client, Events(propertyId),
                new { type = 109, title = "Forged archive", occurredAt = "2026-02-01T09:00:00Z" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/properties/{propertyId}");
        }
    }

    private static string Events(Guid propertyId) => $"/api/properties/{propertyId}/events";

    private async Task<Guid> CreateVehicleAsync(HttpClient client, string name)
    {
        var response = await fixture.PostWithAntiforgeryAsync(client, "/api/properties", new
        {
            name,
            description = "E2E vehicle",
            type = 1,
            currencyCode = "NOK",
            vehicleDetails = new { kind = 0 },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("propertyId").GetGuid();
    }
}
