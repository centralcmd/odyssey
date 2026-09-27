using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;

namespace Odyssey.Api.Tests;

/// <summary>
/// The homeowner association link on a real-estate property over HTTP (issue #217): the scalar id rides
/// the existing property bodies, the slim projection rides every read, and the R1–R4 rules run only when
/// the id changes.
/// </summary>
public class PropertyHomeownerAssociationApiTests
{
    private const string ActorUserId = "property-hoa-actor-id";
    private const string AssociationKey = "RealEstateDetails.HomeownerAssociationId";

    private static readonly string[] FullAccess =
    [
        PermissionClaims.PropertiesCreate,
        PermissionClaims.PropertiesRead,
        PermissionClaims.PropertiesUpdate,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static NewProperty Flat(Guid? associationId) => RealEstateBody("Storgata 12, H0302", "NOK") with
    {
        RealEstateDetails = new RealEstateDetailsDto
        {
            Kind = RealEstateKind.Apartment,
            AddressLine = "Storgata 12",
            HomeownerAssociationId = associationId,
        },
    };

    // ── AC 1-2: create, then every read carries the same projection ─────────

    [Fact]
    public async Task Post_WithOrganization_ReturnsCreated_AndEveryReadCarriesTheSameProjection()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var association = await SeedContactAsync(factory, "Storgata Borettslag");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, Flat(association));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingProperty>())!;
        var fetched = await GetPropertyAsync(client, created.PropertyId);
        var listed = (await client.GetFromJsonAsync<PagedResult<ExistingProperty>>(PropertiesPath))!
            .Items.Single(p => p.PropertyId == created.PropertyId);

        foreach (var property in new[] { created, fetched, listed })
        {
            Assert.Equal(association, property.RealEstateDetails!.HomeownerAssociationId);
            Assert.Equal(
                new PropertyHomeownerAssociation { ContactId = association, Name = "Storgata Borettslag", Archived = null },
                property.HomeownerAssociation);
        }
    }

    // ── AC 3-4: change and clear ─────────────────────────────────────────────

    [Fact]
    public async Task Put_WithDifferentOrganization_ChangesTheLink()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var first = await SeedContactAsync(factory, "First Sameie");
        var second = await SeedContactAsync(factory, "Second Sameie");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, Flat(first));

        var response = await client.PutAsJsonAsync(PropertyPath(id), Flat(second));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await GetPropertyAsync(client, id);
        Assert.Equal(second, updated.HomeownerAssociation!.ContactId);
        Assert.Equal("Second Sameie", updated.HomeownerAssociation.Name);
        Assert.Equal(second, await StoredAssociationAsync(factory, id));
    }

    [Fact]
    public async Task Put_WithNullAssociation_ClearsTheLink()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var association = await SeedContactAsync(factory, "Storgata Borettslag");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, Flat(association));

        var response = await client.PutAsJsonAsync(PropertyPath(id), Flat(null));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await GetPropertyAsync(client, id);
        Assert.Null(updated.HomeownerAssociation);
        Assert.Null(updated.RealEstateDetails!.HomeownerAssociationId);
        Assert.Null(await StoredAssociationAsync(factory, id));
    }

    [Fact]
    public async Task Put_OmittingTheField_ClearsTheLink()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var association = await SeedContactAsync(factory, "Storgata Borettslag");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, Flat(association));

        var body = JsonSerializer.SerializeToNode(Flat(null), JsonOptions)!.AsObject();
        Assert.True(body["realEstateDetails"]!.AsObject().Remove("homeownerAssociationId"));
        var response = await client.PutAsJsonAsync(PropertyPath(id), body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await StoredAssociationAsync(factory, id));
    }

    // ── AC 5-7: R1–R4 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithPerson_Returns422_KeyedOnTheField_AndStoresNothing()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var person = await SeedContactAsync(factory, "Kari Nordmann", ContactType.Person);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, Flat(person));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AssociationKey, await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, await CountAsync<Property>(factory));
    }

    [Fact]
    public async Task Put_ChangingToPerson_Returns422_AndKeepsTheStoredLink()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var association = await SeedContactAsync(factory, "Storgata Borettslag");
        var person = await SeedContactAsync(factory, "Kari Nordmann", ContactType.Person);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, Flat(association));

        var response = await client.PutAsJsonAsync(PropertyPath(id), Flat(person));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(AssociationKey, await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(association, await StoredAssociationAsync(factory, id));
    }

    [Fact]
    public async Task Post_WithUnknownId_Returns400_KeyedOnTheField()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        var response = await client.PostAsJsonAsync(PropertiesPath, Flat(unknown));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AssociationKey, await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(unknown.ToString(), await ReadDetailAsync(response));
        Assert.Equal(0, await CountAsync<Property>(factory));
    }

    [Fact]
    public async Task Post_WithEmptyGuid_Returns400_KeyedOnTheField()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, Flat(Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AssociationKey, await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_WithArchivedOrganization_Returns400_AndNeverEchoesTheName()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var archived = await SeedContactAsync(factory, "Gamle Sameie", archived: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, Flat(archived));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AssociationKey, await ReadErrorKeysAsync(response), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gamle", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ── AC 8: change-only ───────────────────────────────────────────────────

    [Fact]
    public async Task Put_KeepingALinkWhoseContactWasArchivedAndRetyped_IsAccepted()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var association = await SeedContactAsync(factory, "Storgata Borettslag");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, Flat(association));

        await MutateContactAsync(factory, association, contact =>
        {
            contact.Archived = DateTime.UtcNow;
            contact.Type = ContactType.Person;
        });

        var response = await client.PutAsJsonAsync(PropertyPath(id), Flat(association) with { Name = "Renamed flat" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await GetPropertyAsync(client, id);
        Assert.Equal("Renamed flat", updated.Name);
        Assert.Equal(association, updated.HomeownerAssociation!.ContactId);
        Assert.NotNull(updated.HomeownerAssociation.Archived);
    }

    // ── AC 9: no mass assignment through the response shape ──────────────────

    [Fact]
    public async Task Post_CarryingANestedAssociationObject_CreatesNoContact_RenamesNone_AndSetsNoLink()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var existing = await SeedContactAsync(factory, "Storgata Borettslag");
        using var client = factory.CreateClient();
        var contactsBefore = await CountAsync<Contact>(factory);

        var body = JsonSerializer.SerializeToNode(Flat(null), JsonOptions)!.AsObject();
        body["homeownerAssociation"] = new JsonObject { ["contactId"] = existing, ["name"] = "Hijacked" };
        body["realEstateDetails"]!["homeownerAssociation"] =
            new JsonObject { ["contactId"] = existing, ["name"] = "Hijacked" };
        var response = await client.PostAsJsonAsync(PropertiesPath, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingProperty>())!;
        Assert.Null(created.HomeownerAssociation);
        Assert.Null(await StoredAssociationAsync(factory, created.PropertyId));
        Assert.Equal(contactsBefore, await CountAsync<Contact>(factory));
        var name = await ReadAsync(factory, context => context.OrganizationDetails.AsNoTracking()
            .Where(o => o.ContactId == existing).Select(o => o.LegalName).SingleAsync());
        Assert.Equal("Storgata Borettslag", name);
    }

    // ── AC 10-11: vehicle and projection shape ──────────────────────────────

    [Fact]
    public async Task Vehicle_HasNoAssociation()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(PropertiesPath, VehicleBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<ExistingProperty>())!.HomeownerAssociation);
    }

    [Fact]
    public async Task Projection_SerializesExactlyContactIdNameAndArchived()
    {
        await using var factory = await NewFactoryAsync(FullAccess);
        var association = await SeedContactAsync(factory, "Storgata Borettslag", organizationNumber: "912345678");
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, Flat(association));

        using var document = JsonDocument.Parse(await client.GetStringAsync(PropertyPath(id)));
        var members = document.RootElement.GetProperty("homeownerAssociation")
            .EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["archived", "contactId", "name"], members);
        Assert.DoesNotContain("912345678", document.RootElement.GetRawText());
    }

    // ── AC 17: the write gate is unchanged ──────────────────────────────────

    [Fact]
    public async Task Put_WithoutPropertiesUpdate_Returns403_AndLeavesTheLink()
    {
        await using var factory = await NewFactoryAsync([PermissionClaims.PropertiesRead]);
        var association = await SeedContactAsync(factory, "Storgata Borettslag");
        var id = await SeedPropertyAsync(factory, "Flat");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(PropertyPath(id), Flat(association));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredAssociationAsync(factory, id));
    }

    private static async Task<Guid> CreateAsync(HttpClient client, NewProperty body)
    {
        var response = await client.PostAsJsonAsync(PropertiesPath, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ExistingProperty>())!.PropertyId;
    }

    private static Task<Guid?> StoredAssociationAsync(ApiFactory factory, Guid propertyId) =>
        ReadAsync(factory, context => context.RealEstateDetails.AsNoTracking()
            .Where(d => d.PropertyId == propertyId).Select(d => d.HomeownerAssociationId).SingleAsync());

    private static async Task<Guid> SeedContactAsync(
        ApiFactory factory, string name, ContactType type = ContactType.Organization, bool archived = false,
        string? organizationNumber = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = name.ToUpperInvariant(),
            Type = type,
            Archived = archived ? DateTime.UtcNow : null,
        };
        if (type == ContactType.Organization)
        {
            contact.OrganizationDetails = new() { LegalName = name, OrganizationNumber = organizationNumber };
        }
        else
        {
            var parts = name.Split(' ', 2);
            contact.PersonDetails = new() { FirstName = parts[0], LastName = parts[1] };
        }

        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    private static async Task MutateContactAsync(ApiFactory factory, Guid contactId, Action<Contact> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contact = await context.Contacts.SingleAsync(c => c.ContactId == contactId);
        mutate(contact);
        await context.SaveChangesAsync();
    }

    private static async Task<ApiFactory> NewFactoryAsync(IReadOnlyCollection<string>? permissions)
    {
        var factory = new ApiFactory(permissions);
        await EnsureCreatedAsync(factory);
        return factory;
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
