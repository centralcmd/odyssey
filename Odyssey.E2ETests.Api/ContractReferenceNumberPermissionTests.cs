using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The contract reference number across the four seeded role users, over real HTTP against the real
/// role-claim rows (issue #181 AC 16). The field reuses <c>contracts.read</c>/<c>.create</c>/
/// <c>.update</c> — no claim was added — so what this proves is that the roles that hold those claims
/// reach it and the ones that do not are refused on every affected surface.
/// </summary>
[Collection(ApiStackCollection.Name)]
public class ContractReferenceNumberPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Owner")]
    public async Task A_writer_round_trips_the_reference_number_through_every_surface(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var writer = UserIn(role);
        var client = await fixture.CreateAuthenticatedClientAsync(writer.Email, writer.Password);
        var marker = $"E2E-REF-{role}-{Guid.NewGuid():N}"[..40];

        var created = await fixture.PostWithAntiforgeryAsync(client, "/api/contracts", Body($"E2E Ref {role}", marker));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var contractId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("contractId").GetGuid();

        try
        {
            var detail = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}");
            Assert.Equal(marker, detail!.RootElement.GetProperty("referenceNumber").GetString());

            var hits = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts?search={marker}");
            var hit = Assert.Single(hits!.RootElement.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(marker, hit.GetProperty("referenceNumber").GetString());

            var put = await client.PutAsJsonAsync($"/api/contracts/{contractId}", Body($"E2E Ref {role}", null));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(JsonValueKind.Null,
                (await put.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("referenceNumber").ValueKind);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>The User role holds <c>contracts.read</c> alone: it reads the field and writes nothing.</summary>
    [SkippableFact]
    public async Task A_reader_sees_the_field_and_is_refused_both_writes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var created = await fixture.PostWithAntiforgeryAsync(adminClient, "/api/contracts", Body("E2E Ref Reader", "E2E-REF-READER"));
        created.EnsureSuccessStatusCode();
        var contractId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("contractId").GetGuid();

        try
        {
            var user = UserIn("User");
            var client = await fixture.CreateAuthenticatedClientAsync(user.Email, user.Password);

            var detail = await client.GetFromJsonAsync<JsonDocument>($"/api/contracts/{contractId}");
            Assert.Equal("E2E-REF-READER", detail!.RootElement.GetProperty("referenceNumber").GetString());
            Assert.Equal(HttpStatusCode.OK,
                (await client.GetAsync("/api/contracts?sortBy=referenceNumber")).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(
                client, "/api/contracts", Body("E2E Ref Refused", "E2E-REF-NO"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
                $"/api/contracts/{contractId}", Body("E2E Ref Reader", "E2E-REF-NO"))).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contracts/{contractId}");
        }
    }

    /// <summary>Guest holds no <c>Contracts*</c> claim, so neither read nor either write reaches the field.</summary>
    [SkippableFact]
    public async Task The_guest_role_reaches_no_surface_carrying_the_field()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var guest = UserIn("Guest");
        var client = await fixture.CreateAuthenticatedClientAsync(guest.Email, guest.Password);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/contracts?sortBy=referenceNumber")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/contracts/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.PostWithAntiforgeryAsync(
            client, "/api/contracts", Body("E2E Ref Guest", "E2E-REF-NO"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/api/contracts/{Guid.NewGuid()}", Body("E2E Ref Guest", "E2E-REF-NO"))).StatusCode);
    }

    /// <summary>
    /// The seeded stack carries rows with and without a number (AC 19), so the nulls-last sort has
    /// both kinds to place — asserted over the whole list, not a page.
    /// </summary>
    [SkippableFact]
    public async Task The_seeded_list_sorts_numbered_contracts_first_in_both_directions()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var owner = UserIn("Owner");
        var client = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);

        foreach (var direction in new[] { "asc", "desc" })
        {
            var page = await client.GetFromJsonAsync<JsonDocument>(
                $"/api/contracts?sortBy=referenceNumber&sortDir={direction}&limit=500");
            var numbers = page!.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("referenceNumber"))
                .Select(value => value.ValueKind == JsonValueKind.Null ? null : value.GetString())
                .ToList();

            Assert.Contains(numbers, n => n is not null);
            Assert.Contains(numbers, n => n is null);
            var firstNull = numbers.IndexOf(null);
            Assert.All(numbers.Skip(firstNull), n => Assert.Null(n));
        }
    }

    private static object Body(string name, string? referenceNumber) => new
    {
        name,
        type = 2,
        referenceNumber,
        startDate = "2026-01-01T00:00:00Z",
    };
}
