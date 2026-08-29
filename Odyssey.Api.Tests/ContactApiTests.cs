using Odyssey.Dtos;
using System.Net;
using System.Net.Http.Json;
using Odyssey.Dtos.Authorization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-tier coverage for the contact sub-resource endpoints (issue #325 §7): the claim matrix
/// on the address/email/phone routes, create → 201 + Location, unknown-parent and wrong-parent 404s
/// (finding F2), the delete/no-content path, that the child DTOs can't reparent via over-posting, and
/// that the base GET returns the contact collections inline.
/// </summary>
public class ContactApiTests
{
    private const string ActorUserId = "contact-actor-id";

    private static readonly string[] ReadOnly = [PermissionClaims.ContactsRead];
    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContactsRead, PermissionClaims.ContactsCreate,
        PermissionClaims.ContactsUpdate, PermissionClaims.ContactsDelete,
    ];

    private static NewContact NewOrg(string legalName = "Acme") => new()
    {
        Type = ContactType.Organization,
        Archived = false,
        OrganizationDetails = new() { LegalName = legalName },
    };

    private static NewAddress Address(bool primary = false) => new()
    {
        Label = AddressLabel.Home, IsPrimary = primary, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO",
    };

    // ── Claim matrix on the sub-resource routes ───────────────────────────────

    [Fact]
    public async Task ListAddresses_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{Guid.NewGuid()}/addresses");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListAddresses_WithoutReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{Guid.NewGuid()}/addresses");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChildWrites_WithReadOnlyClaim_AreForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/addresses", Address());
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var put = await client.PutAsJsonAsync($"/api/contacts/{id}/emails/{Guid.NewGuid()}", new NewEmailAddress { Label = EmailLabel.Work, Value = "x@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        var delete = await client.DeleteAsync($"/api/contacts/{id}/phones/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ── Create / read / delete happy path ─────────────────────────────────────

    [Fact]
    public async Task PostAddress_Returns201WithLocation_AndListReturnsIt()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{id}/addresses", Address());
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.NotNull(post.Headers.Location);

        var created = await post.Content.ReadFromJsonAsync<ExistingAddress>();
        Assert.NotNull(created);
        Assert.Equal(id, created!.ContactId);
        Assert.True(created.IsPrimary); // first record is auto-primary

        var listed = await client.GetFromJsonAsync<List<ExistingAddress>>($"/api/contacts/{id}/addresses");
        Assert.Equal(created.Id, Assert.Single(listed!).Id);
    }

    [Fact]
    public async Task PostAddress_ToUnknownContact_Returns404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        await EnsureDatabaseAsync(factory);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync($"/api/contacts/{Guid.NewGuid()}/addresses", Address());
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task DeleteAddress_Returns204()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync($"/api/contacts/{id}/addresses", Address())).Content.ReadFromJsonAsync<ExistingAddress>();

        var delete = await client.DeleteAsync($"/api/contacts/{id}/addresses/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var listed = await client.GetFromJsonAsync<List<ExistingAddress>>($"/api/contacts/{id}/addresses");
        Assert.Empty(listed!);
    }

    // ── Wrong-parent isolation (finding F2) ───────────────────────────────────

    [Fact]
    public async Task PutAddress_UnderWrongParent_Returns404_AndDoesNotMutate()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var owner = await SeedContactAsync(factory, "Owner");
        var other = await SeedContactAsync(factory, "Other");
        using var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync($"/api/contacts/{owner}/addresses", Address())).Content.ReadFromJsonAsync<ExistingAddress>();

        // The address id exists, but not under 'other' — must 404, not mutate across parents.
        var put = await client.PutAsJsonAsync($"/api/contacts/{other}/addresses/{created!.Id}",
            new NewAddress { Label = AddressLabel.Work, IsPrimary = true, Line1 = "HACKED", City = "Nowhere", CountryCode = "NO" });
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var unchanged = await client.GetFromJsonAsync<List<ExistingAddress>>($"/api/contacts/{owner}/addresses");
        Assert.Equal("Storgata 55", Assert.Single(unchanged!).Line1);
    }

    [Fact]
    public async Task DeleteAddress_UnderWrongParent_Returns404()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var owner = await SeedContactAsync(factory, "Owner");
        var other = await SeedContactAsync(factory, "Other");
        using var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync($"/api/contacts/{owner}/addresses", Address())).Content.ReadFromJsonAsync<ExistingAddress>();

        var delete = await client.DeleteAsync($"/api/contacts/{other}/addresses/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<ExistingAddress>>($"/api/contacts/{owner}/addresses"))!);
    }

    // ── The route parent wins over any body-supplied parent (over-post guard) ──

    [Fact]
    public async Task PostAddress_WithForeignContactIdInBody_BindsToRouteParent()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var owner = await SeedContactAsync(factory, "Owner");
        var other = await SeedContactAsync(factory, "Other");
        using var client = factory.CreateClient();

        // Smuggle a different contactId/id in the body; the DTO carries neither, so the route wins.
        var body = new
        {
            id = Guid.NewGuid(),
            contactId = other,
            label = (int)AddressLabel.Home,
            isPrimary = true,
            line1 = "Storgata 55",
            city = "Oslo",
            countryCode = "NO",
        };
        var post = await client.PostAsJsonAsync($"/api/contacts/{owner}/addresses", body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<ExistingAddress>();
        Assert.Equal(owner, created!.ContactId);
        Assert.NotEqual(other, created.ContactId);
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingAddress>>($"/api/contacts/{other}/addresses"))!);
    }

    // ── Base GET returns contact collections inline (§7) ──────────────────────

    [Fact]
    public async Task GetContact_ReturnsContactCollectionsInline()
    {
        await using var factory = new ApiFactory(ReadWrite);
        var id = await SeedContactAsync(factory);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync($"/api/contacts/{id}/addresses", Address());
        await client.PostAsJsonAsync($"/api/contacts/{id}/emails", new NewEmailAddress { Label = EmailLabel.Work, Value = "billing@example.com" });

        var fetched = await client.GetFromJsonAsync<ExistingContact>($"/api/contacts/{id}");
        Assert.Single(fetched!.Addresses);
        Assert.Single(fetched.EmailAddresses);
        Assert.Equal("billing@example.com", fetched.EmailAddresses[0].Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
