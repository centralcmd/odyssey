using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-tier coverage for the alias sub-resource (issue #48 §7, §16): the claim matrix, the status
/// codes, containment on all four verbs, model validation reaching the wire, and the mass-assignment
/// surface.
/// </summary>
public class ContactAliasApiTests
{
    private const string ActorUserId = "alias-actor-id";

    private static readonly string[] ReadOnly = [PermissionClaims.ContactsRead];
    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.ContactsUpdate, PermissionClaims.ContactsDelete,
    ];

    private static NewContactAlias Alias(string value, string? label = null) =>
        new() { Value = value, Label = label };

    // ── Claim matrix (AC 21) ──────────────────────────────────────────────────

    [Fact]
    public async Task ListAliases_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{Guid.NewGuid()}/aliases");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListAliases_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{Guid.NewGuid()}/aliases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The three write verbs each carry their OWN claim, not a shared "can edit" one — a
    // contacts.update-without-.delete principal must not be able to delete.
    [Fact]
    public async Task AliasWrites_WithReadOnlyClaim_AreForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"/api/contacts/{id}/aliases", Alias("Kari"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/api/contacts/{id}/aliases/{Guid.NewGuid()}", Alias("Kari"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"/api/contacts/{id}/aliases/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task DeleteAlias_WithUpdateButNotDeleteClaim_IsForbidden()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate, PermissionClaims.ContactsUpdate]);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        var alias = await CreateAliasAsync(client, id, Alias("Kari"));

        var response = await client.DeleteAsync($"/api/contacts/{id}/aliases/{alias.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Status codes (AC 1, 5, 6, 8) ──────────────────────────────────────────

    [Fact]
    public async Task PostAlias_ReturnsCreatedWithTheRowAndALocationHeader()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{id}/aliases", Alias("Kari", "nickname"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The collection, not a per-item GET — the siblings expose none either.
        Assert.Contains($"/api/contacts/{id}/aliases", response.Headers.Location!.ToString());

        var created = (await response.Content.ReadFromJsonAsync<ExistingContactAlias>())!;
        Assert.Equal("Kari", created.Value);
        Assert.Equal("nickname", created.Label);
        Assert.Equal(id, created.ContactId);
    }

    [Fact]
    public async Task PostAlias_UnknownContact_ReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{Guid.NewGuid()}/aliases", Alias("Kari"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // AC 5: 204 with no body, matching every sibling sub-resource PUT.
    [Fact]
    public async Task PutAlias_ReturnsNoContentAndClearsAnOmittedLabel()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        var alias = await CreateAliasAsync(client, id, Alias("Kari", "nickname"));

        var response = await client.PutAsJsonAsync($"/api/contacts/{id}/aliases/{alias.Id}", Alias("Kari"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);

        var stored = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContactAlias>>($"/api/contacts/{id}/aliases"))!);
        Assert.Null(stored.Label);
    }

    // AC 2 + AC 11: the 409 names `value` in the problem-details errors dictionary, and echoes
    // neither the submitted value nor the label.
    [Fact]
    public async Task PostAlias_Duplicate_ReturnsConflictNamingTheFieldWithoutEchoingTheValue()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        await CreateAliasAsync(client, id, Alias("Hansen", "maiden name"));

        var response = await client.PostAsJsonAsync($"/api/contacts/{id}/aliases", Alias("hansen", "nickname"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"value\"", body);
        Assert.DoesNotContain("hansen", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nickname", body, StringComparison.OrdinalIgnoreCase);
    }

    // AC 6.
    [Fact]
    public async Task PostAlias_BeyondTheCap_ReturnsUnprocessableNamingTheField()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        for (var i = 0; i < ContactAliasRules.MaxPerContact; i++)
        {
            await CreateAliasAsync(client, id, Alias($"Alias {i:00}"));
        }

        var response = await client.PostAsJsonAsync($"/api/contacts/{id}/aliases", Alias("One too many"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("value", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ContactAliasRules.MaxPerContact,
            (await client.GetFromJsonAsync<List<ExistingContactAlias>>($"/api/contacts/{id}/aliases"))!.Count);
    }

    [Fact]
    public async Task DeleteAlias_ReturnsNoContentAndBumpsTheParent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        var alias = await CreateAliasAsync(client, id, Alias("Kari"));
        var before = (await client.GetFromJsonAsync<ExistingContact>($"/api/contacts/{id}"))!.UpdatedAt;

        var response = await client.DeleteAsync($"/api/contacts/{id}/aliases/{alias.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = (await client.GetFromJsonAsync<ExistingContact>($"/api/contacts/{id}"))!;
        Assert.Empty(after.Aliases);
        Assert.True(after.UpdatedAt >= before);
    }

    // ── Containment on all four verbs (AC 7) ──────────────────────────────────
    // A mismatch is 404, never 403: a 403 would confirm the row exists under another parent.

    [Fact]
    public async Task Containment_AnAliasOfAnotherContactIs404OnEveryVerb()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var a = await SeedContactAsync(factory, "Alpha");
        var b = await SeedContactAsync(factory, "Beta");
        using var client = factory.CreateClient();
        var aliasOfB = await CreateAliasAsync(client, b, Alias("Sammy", "nickname"));

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/contacts/{a}/aliases/{aliasOfB.Id}", Alias("Hijacked"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/contacts/{a}/aliases/{aliasOfB.Id}")).StatusCode);

        // The GET is a collection route, so containment there means A's list never carries B's row.
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingContactAlias>>($"/api/contacts/{a}/aliases"))!);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/contacts/{Guid.NewGuid()}/aliases")).StatusCode);

        var untouched = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContactAlias>>($"/api/contacts/{b}/aliases"))!);
        Assert.Equal("Sammy", untouched.Value);
        Assert.Equal("nickname", untouched.Label);
    }

    // ── Model validation reaches the wire (AC 9) ──────────────────────────────
    // A control character is rejected by MODEL VALIDATION, before the service ever runs — which is
    // what makes the same rule evaluable in the WASM client against the form model.

    [Fact]
    public async Task PostAlias_ControlCharacterValue_IsRejectedByModelValidation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{id}/aliases", Alias("Kari\u0001Bob"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("Value", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostAlias_OverlongValue_IsRejectedByModelValidation()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/contacts/{id}/aliases", Alias(new string('x', ContactAliasRules.MaxValueLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Mass assignment (AC 19, AC 20) ────────────────────────────────────────

    // NewContact gains no alias member, so a nested array is simply ignored by the binder.
    [Fact]
    public async Task PostContact_WithANestedAliasesArray_CreatesZeroAliasRows()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/contacts", new
        {
            type = ContactType.Organization,
            archived = false,
            organizationDetails = new { legalName = "Acme" },
            aliases = new[] { new { value = "Sneaky", label = "injected" } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = Assert.Single(
            (await client.GetFromJsonAsync<PagedResult<ExistingContact>>("/api/contacts"))!.Items);
        Assert.Empty(created.Aliases);
    }

    // The write DTO carries two scalar strings — no ContactId, no id — so there is no over-posting
    // path to re-parent an alias. Anything extra in the body is ignored.
    [Fact]
    public async Task PutAlias_WithIdAndContactIdInTheBody_ReparentsNothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var a = await SeedContactAsync(factory, "Alpha");
        var b = await SeedContactAsync(factory, "Beta");
        using var client = factory.CreateClient();
        var alias = await CreateAliasAsync(client, a, Alias("Kari"));

        var response = await client.PutAsJsonAsync($"/api/contacts/{a}/aliases/{alias.Id}", new
        {
            value = "Renamed",
            id = Guid.NewGuid(),
            contactId = b,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContactAlias>>($"/api/contacts/{a}/aliases"))!);
        Assert.Equal(alias.Id, stored.Id);
        Assert.Equal(a, stored.ContactId);
        Assert.Equal("Renamed", stored.Value);
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingContactAlias>>($"/api/contacts/{b}/aliases"))!);
    }

    // ── Inline read shape ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetContact_ReturnsAliasesInline()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();
        await CreateAliasAsync(client, id, Alias("Kari", "nickname"));

        var fetched = await client.GetFromJsonAsync<ExistingContact>($"/api/contacts/{id}");

        var inline = Assert.Single(fetched!.Aliases);
        Assert.Equal("Kari", inline.Value);
        Assert.Equal("nickname", inline.Label);
    }

    // The detail DTOs are additive: the four new members round-trip through the contact write path.
    [Fact]
    public async Task PutContact_RoundTripsTheFourLifecycleScalars()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/contacts", new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto
            {
                FirstName = "Karoline",
                LastName = "Hansen",
                MiddleName = "Marie",
                DateOfBirth = new DateTime(1951, 9, 2),
                DateOfDeath = new DateTime(2024, 3, 11),
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var person = Assert.Single(
            (await client.GetFromJsonAsync<PagedResult<ExistingContact>>("/api/contacts"))!.Items);
        Assert.Equal("Marie", person.PersonDetails!.MiddleName);
        Assert.Equal(new DateTime(2024, 3, 11), person.PersonDetails.DateOfDeath);
        // Non-Goal 3: recording a death archives nothing.
        Assert.Null(person.Archived);
    }

    // AC 23: the pair rejection reaches the wire as a 400 naming the field the form has to mark.
    [Fact]
    public async Task PostContact_DeathBeforeBirth_ReturnsBadRequestNamingDateOfDeath()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/contacts", new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto
            {
                FirstName = "Karoline",
                LastName = "Hansen",
                DateOfBirth = new DateTime(1968, 3, 14),
                DateOfDeath = new DateTime(1960, 1, 1),
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("dateOfDeath", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // AC 24.
    [Fact]
    public async Task PostContact_DissolvedBeforeEstablished_ReturnsBadRequestNamingDissolvedDate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/contacts", new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto
            {
                LegalName = "Pacific Home Insurance Co.",
                EstablishedDate = new DateTime(1974, 6, 1),
                DissolvedDate = new DateTime(1970, 1, 1),
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("dissolvedDate", problem!.Errors.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<ExistingContactAlias> CreateAliasAsync(
        HttpClient client, Guid contactId, NewContactAlias alias)
    {
        var response = await client.PostAsJsonAsync($"/api/contacts/{contactId}/aliases", alias);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContactAlias>())!;
    }

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> SeedContactAsync(WebApplicationFactory<Program> factory, string legalName = "Acme")
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = legalName.ToUpperInvariant(),
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = legalName },
        });
        await context.SaveChangesAsync();
        return id;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
