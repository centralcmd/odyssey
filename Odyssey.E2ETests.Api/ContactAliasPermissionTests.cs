using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Dtos;
using Odyssey.TestData;
using Xunit;

namespace Odyssey.E2ETests.Api;

/// <summary>
/// The alias sub-resource's permission matrix across the four seeded role users, over real HTTP
/// against the real role-claim rows (issue #48 AC 21).
/// </summary>
/// <remarks>
/// <para>
/// <c>Odyssey.Api.Tests</c> asserts the same gates against a <c>TestAuthHandler</c> principal carrying
/// a hand-written claim list. That proves the <c>[Authorize]</c> attributes are wired to the right
/// policies; it cannot prove the roles actually HOLD those claims, because the claim list is supplied
/// by the test. Only a real sign-in reads <c>AspNetRoleClaims</c> as <c>RoleClaimSeeder</c> reconciled
/// it, which is the half that matters here: the whole design rests on the four alias routes reusing
/// the existing <c>contacts.*</c> claims, so <b>no</b> role-claim change and no forced sign-out were
/// needed.
/// </para>
/// <para>
/// The deliberate, signed-off consequence is asserted rather than left implicit: <b>Guest</b> — the
/// lowest role — can read every alias in the deployment. That is accepted for a single-household
/// deployment where every role holder is a member of that household, and it is recorded in
/// <c>docs/deployment.md</c> because an operator can deploy a published image without ever reading
/// the issue. If this assertion ever needs changing, the posture changed with it.
/// </para>
/// </remarks>
[Collection(ApiStackCollection.Name)]
public class ContactAliasPermissionTests(ApiStackFixture fixture)
{
    private static DemoUser UserIn(string role) => DemoUsers.All.First(user => user.Role == role);

    [SkippableFact]
    public async Task An_owner_can_round_trip_an_alias_through_all_four_verbs()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var owner = UserIn("Owner");
        var client = await fixture.CreateAuthenticatedClientAsync(owner.Email, owner.Password);
        var contactId = await CreateContactAsync(client, "E2E Alias Co");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contacts/{contactId}/aliases", new { value = "E2E Trading", label = "trading as" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var aliasId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
                .RootElement.GetProperty("id").GetGuid();

            var listed = await client.GetFromJsonAsync<JsonDocument>($"/api/contacts/{contactId}/aliases");
            var row = Assert.Single(listed!.RootElement.EnumerateArray().ToList());
            Assert.Equal("E2E Trading", row.GetProperty("value").GetString());
            Assert.Equal("trading as", row.GetProperty("label").GetString());

            // The full-replace PUT: an omitted label clears the stored one.
            var updated = await client.PutAsJsonAsync(
                $"/api/contacts/{contactId}/aliases/{aliasId}", new { value = "E2E Trading" });
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

            // And the alias arrives inline on the contact read, not only on the dedicated route.
            using var contact = await client.GetFromJsonAsync<JsonDocument>($"/api/contacts/{contactId}");
            var inline = Assert.Single(contact!.RootElement.GetProperty("aliases").EnumerateArray().ToList());
            Assert.Equal(JsonValueKind.Null, inline.GetProperty("label").ValueKind);

            var deleted = await fixture.DeleteWithAntiforgeryAsync(
                client, $"/api/contacts/{contactId}/aliases/{aliasId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{contactId}");
        }
    }

    /// <summary>
    /// <c>User</c> and <c>Guest</c> hold <c>contacts.read</c> only, so every alias WRITE is refused —
    /// and the read is not. The reuse of the sibling claims is exactly what makes this true without
    /// any <c>RolePermissions</c> change.
    /// </summary>
    [SkippableTheory]
    [InlineData("User")]
    [InlineData("Guest")]
    public async Task A_read_only_role_can_list_aliases_but_not_write_one(string role)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var adminClient = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var contactId = await CreateContactAsync(adminClient, $"E2E Alias Gate {role}");

        try
        {
            var reader = UserIn(role);
            var client = await fixture.CreateAuthenticatedClientAsync(reader.Email, reader.Password);

            // The accepted posture, asserted: the lowest role reads the alias list.
            var listed = await client.GetAsync($"/api/contacts/{contactId}/aliases");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await fixture.PostWithAntiforgeryAsync(
                    client, $"/api/contacts/{contactId}/aliases", new { value = "Refused" })).StatusCode);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PutAsJsonAsync(
                    $"/api/contacts/{contactId}/aliases/{Guid.NewGuid()}", new { value = "Refused" })).StatusCode);

            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await fixture.DeleteWithAntiforgeryAsync(
                    client, $"/api/contacts/{contactId}/aliases/{Guid.NewGuid()}")).StatusCode);
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(adminClient, $"/api/contacts/{contactId}");
        }
    }

    /// <summary>
    /// Containment over the wire: an alias id belonging to another contact is <c>404</c> through this
    /// contact's route, never <c>403</c> — which would confirm the row exists under a different
    /// parent.
    /// </summary>
    [SkippableFact]
    public async Task An_alias_of_another_contact_is_not_found_through_this_contacts_route()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);

        var admin = UserIn("Admin");
        var client = await fixture.CreateAuthenticatedClientAsync(admin.Email, admin.Password);
        var a = await CreateContactAsync(client, "E2E Containment A");
        var b = await CreateContactAsync(client, "E2E Containment B");

        try
        {
            var created = await fixture.PostWithAntiforgeryAsync(
                client, $"/api/contacts/{b}/aliases", new { value = "Belongs to B" });
            var aliasOfB = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
                .RootElement.GetProperty("id").GetGuid();

            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.PutAsJsonAsync($"/api/contacts/{a}/aliases/{aliasOfB}", new { value = "Hijacked" })).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{a}/aliases/{aliasOfB}")).StatusCode);

            using var stillThere = await client.GetFromJsonAsync<JsonDocument>($"/api/contacts/{b}/aliases");
            Assert.Equal(
                "Belongs to B",
                Assert.Single(stillThere!.RootElement.EnumerateArray().ToList()).GetProperty("value").GetString());
        }
        finally
        {
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{a}");
            await fixture.DeleteWithAntiforgeryAsync(client, $"/api/contacts/{b}");
        }
    }

    private async Task<Guid> CreateContactAsync(HttpClient client, string legalName)
    {
        var response = await fixture.PostWithAntiforgeryAsync(client, "/api/contacts", new
        {
            type = ContactType.Organization,
            archived = false,
            organizationDetails = new { legalName },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var location = response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("Create response had no Location header.");
        return Guid.Parse(location[(location.LastIndexOf('/') + 1)..]);
    }
}
