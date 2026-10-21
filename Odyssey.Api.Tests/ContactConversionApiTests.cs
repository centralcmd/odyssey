using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-tier coverage for converting a contact between Person and Organization (issue #335 §5, §12):
/// the claim gates, the binding of the two new models, the blocked and stale <c>409</c>s with their
/// claim-conditional payload, the type-changing PUT, and the change-only photo person-tag rule.
/// </summary>
public class ContactConversionApiTests
{
    private const string ActorUserId = "conversion-actor-id";

    private static readonly string[] ContactsOnly =
        [PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate];

    private static readonly string[] ContactsPhotosProperties =
        [PermissionClaims.ContactsRead, PermissionClaims.ContactsUpdate, PermissionClaims.PhotosRead, PermissionClaims.PropertiesRead];

    // ── Claims ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_WithoutUpdateClaim_IsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContactsRead]);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion", Request(ContactType.Organization, contact.UpdatedAt));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(PermissionClaims.ContactsRead)]
    [InlineData(PermissionClaims.ContactsUpdate)]
    public async Task Preview_NeedsBothReadAndUpdate(string onlyClaim)
    {
        await using var factory = new ApiFactory([onlyClaim]);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{contact.Id}/conversion-preview?targetType=Organization");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Binding ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_WithoutTargetType_IsARequired400()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/contacts/{contact.Id}/conversion-preview");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        var message = Assert.Single(problem!.Errors[nameof(ContactConversionPreviewQuery.TargetType)]);
        Assert.Contains("required", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Convert_WithoutExpectedUpdatedAt_Is400()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion", new { targetType = (int)ContactType.Organization });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Convert_OverrideForTheWrongTarget_Is400()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion",
            Request(ContactType.Organization, contact.UpdatedAt) with { FirstName = "Ada" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ThenConvert_EchoingUpdatedAt_Converts()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var preview = await client.GetFromJsonAsync<ContactConversionPreview>(
            $"/api/contacts/{contact.Id}/conversion-preview?targetType=Organization");
        Assert.True(preview!.CanConvert);
        Assert.Equal("Ada Lovelace", preview.OrganizationDetails!.LegalName);

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion",
            Request(ContactType.Organization, preview.UpdatedAt));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var converted = await response.Content.ReadFromJsonAsync<ExistingContact>();
        Assert.Equal(contact.Id, converted!.ContactId);
        Assert.Equal(ContactType.Organization, converted.Type);
        Assert.Equal("Ada Lovelace", converted.OrganizationDetails!.LegalName);
    }

    [Fact]
    public async Task Convert_ExtraBodyFields_AreNotSet()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion", new
        {
            targetType = (int)ContactType.Organization,
            expectedUpdatedAt = contact.UpdatedAt,
            organizationNumber = "999 999 999",
            website = "https://evil.example",
            dateOfBirth = "1900-01-01",
        });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var converted = await response.Content.ReadFromJsonAsync<ExistingContact>();
        Assert.Null(converted!.OrganizationDetails!.OrganizationNumber);
        Assert.Null(converted.OrganizationDetails.Website);
        Assert.Null(converted.OrganizationDetails.EstablishedDate);
    }

    [Fact]
    public async Task Convert_SingleWordLegalName_Is422KeyedOnLastName()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedOrgAsync(factory, "Equinor");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion", Request(ContactType.Person, contact.UpdatedAt));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.True(problem!.Errors.ContainsKey(ContactConversionFields.LastName));
    }

    [Theory]
    [InlineData("Line\nbreak")]
    [InlineData("Carriage\rreturn")]
    public async Task Convert_OverrideWithALineBreak_Is400KeyedOnTheField(string lastName)
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedOrgAsync(factory, "Equinor");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion",
            Request(ContactType.Person, contact.UpdatedAt) with { LastName = lastName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.True(problem!.Errors.ContainsKey(ContactConversionFields.LastName));
    }

    [Fact]
    public async Task Convert_StaleExpectedUpdatedAt_Is409WithTheStaleCode()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Ada", "Lovelace");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion",
            Request(ContactType.Organization, contact.UpdatedAt.AddMinutes(-5)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(ContactConversionCodes.Stale, body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("photoPersonTags", out _));
    }

    [Fact]
    public async Task Convert_UnknownContact_Is404()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        using var client = factory.CreateClient();
        await EnsureDatabaseAsync(factory);

        var response = await client.PostAsJsonAsync($"/api/contacts/{Guid.NewGuid()}/conversion", Request(ContactType.Person, DateTime.UtcNow));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Blockers ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_BlockedByPhotoTags_WithoutPhotosRead_ShowsTheCountOnly()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedPersonAsync(factory, "Diego", "Ramos");
        await SeedPhotoTagAsync(factory, contact.Id);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion", Request(ContactType.Organization, contact.UpdatedAt));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(ContactConversionCodes.Blocked, root.GetProperty("code").GetString());
        Assert.Equal(1, root.GetProperty("photoPersonTags").GetProperty("count").GetInt32());
        Assert.DoesNotContain("Diego", root.GetProperty("detail").GetString()!);
        Assert.Equal(0, root.GetProperty("photoPersonTags").GetProperty("photoIds").GetArrayLength());
        Assert.DoesNotMatch("[0-9]", root.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Convert_BlockedByPhotoTags_WithPhotosRead_ListsThePhotoIds()
    {
        await using var factory = new ApiFactory(ContactsPhotosProperties);
        var contact = await SeedPersonAsync(factory, "Diego", "Ramos");
        var photoId = await SeedPhotoTagAsync(factory, contact.Id);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/contacts/{contact.Id}/conversion", Request(ContactType.Organization, contact.UpdatedAt));

        using var body = await ReadJsonAsync(response);
        var ids = body.RootElement.GetProperty("photoPersonTags").GetProperty("photoIds");
        Assert.Equal(photoId, ids[0].GetGuid());
    }

    [Fact]
    public async Task Preview_HomeownerAssociation_PropertyNamesFollowPropertiesRead()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedOrgAsync(factory, "Maple Heights HOA");
        await SeedAssociatedPropertyAsync(factory, contact.Id, "Maple St house");
        using var client = factory.CreateClient();

        var preview = await client.GetFromJsonAsync<ContactConversionPreview>(
            $"/api/contacts/{contact.Id}/conversion-preview?targetType=Person");

        Assert.False(preview!.CanConvert);
        Assert.Equal(1, preview.Blockers.HomeownerAssociations.Count);
        Assert.Empty(preview.Blockers.HomeownerAssociations.Properties);

        await using var privileged = new ApiFactory(ContactsPhotosProperties);
        var other = await SeedOrgAsync(privileged, "Solberg Sameie");
        await SeedAssociatedPropertyAsync(privileged, other.Id, "Storgata 14");
        using var privilegedClient = privileged.CreateClient();
        var named = await privilegedClient.GetFromJsonAsync<ContactConversionPreview>(
            $"/api/contacts/{other.Id}/conversion-preview?targetType=Person");
        Assert.Equal("Storgata 14", Assert.Single(named!.Blockers.HomeownerAssociations.Properties).Name);
    }

    [Fact]
    public async Task Put_TypeChange_WhileBlocked_ReturnsTheSameBlocked409()
    {
        await using var factory = new ApiFactory(ContactsOnly);
        var contact = await SeedOrgAsync(factory, "Maple Heights HOA");
        await SeedAssociatedPropertyAsync(factory, contact.Id, "Maple St house");
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/contacts/{contact.Id}", new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto { FirstName = "Maple", LastName = "Heights" },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(ContactConversionCodes.Blocked, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("homeownerAssociations").GetProperty("count").GetInt32());
    }

    // ── Photo person tags: change-only on PUT (§7.6) ───────────────────────────

    [Fact]
    public async Task PhotoPut_KeepingAStaleNonPersonTag_Succeeds_AddingOneIsStill422()
    {
        await using var factory = new PhotoFactory();
        var fileId = await PhotoTestSupport.SeedImageFileAsync(factory, ActorUserId);
        var person = await PhotoTestSupport.SeedPersonAsync(factory);
        var org = await PhotoTestSupport.SeedPersonAsync(factory, "Acme", ContactType.Organization);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/photos", new NewPhoto { FileId = fileId, PersonContactIds = [person] });
        var photo = (await post.Content.ReadFromJsonAsync<ExistingPhoto>())!;

        // The race the spec accepts: the tagged contact became an organization after the tag was written.
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await context.Contacts.Include(c => c.PersonDetails).FirstAsync(c => c.ContactId == person);
            context.Remove(row.PersonDetails!);
            row.Type = ContactType.Organization;
            row.OrganizationDetails = new OrganizationDetails { LegalName = "Now An Org" };
            await context.SaveChangesAsync();
        }

        var keep = await client.PutAsJsonAsync($"/api/photos/{photo.PhotoId}", new UpdatePhoto { Title = "Kept", PersonContactIds = [person] });
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);

        var add = await client.PutAsJsonAsync($"/api/photos/{photo.PhotoId}", new UpdatePhoto { PersonContactIds = [person, org] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
    }

    // ── Contract and role guards ────────────────────────────────────────────────

    [Fact]
    public async Task Swagger_PutContact_DeclaresConflictAndUnprocessable()
    {
        using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = document.RootElement.GetProperty("paths").GetProperty("/api/contacts/{id}")
            .GetProperty("put").GetProperty("responses");

        Assert.True(responses.TryGetProperty("409", out _));
        Assert.True(responses.TryGetProperty("422", out _));
    }

    /// <summary>
    /// The unconditional blocker counts are an accepted residual only while every role that can convert
    /// a contact can also read the photos and properties being counted (issue #335 §7.2). A role that
    /// breaks that fails here rather than quietly widening the disclosure.
    /// </summary>
    [Fact]
    public void EveryRoleHoldingContactsUpdate_AlsoHoldsPhotosReadAndPropertiesRead()
    {
        var converters = RolePermissions.RoleClaimMap.Where(role => role.Claims.Contains(PermissionClaims.ContactsUpdate)).ToList();

        // Non-empty, or the guard would pass vacuously.
        Assert.NotEmpty(converters);
        foreach (var role in converters)
        {
            Assert.Contains(PermissionClaims.PhotosRead, role.Claims);
            Assert.Contains(PermissionClaims.PropertiesRead, role.Claims);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static ContactConversionRequest Request(ContactType target, DateTime expectedUpdatedAt) => new()
    {
        TargetType = target,
        ExpectedUpdatedAt = expectedUpdatedAt,
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task EnsureDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
    }

    private static Task<(Guid Id, DateTime UpdatedAt)> SeedPersonAsync(WebApplicationFactory<Program> factory, string first, string last) =>
        SeedAsync(factory, new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = $"{first} {last}".ToUpperInvariant(),
            Type = ContactType.Person,
            PersonDetails = new PersonDetails { FirstName = first, LastName = last },
        });

    private static Task<(Guid Id, DateTime UpdatedAt)> SeedOrgAsync(WebApplicationFactory<Program> factory, string legalName) =>
        SeedAsync(factory, new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = legalName.ToUpperInvariant(),
            Type = ContactType.Organization,
            OrganizationDetails = new OrganizationDetails { LegalName = legalName },
        });

    private static async Task<(Guid Id, DateTime UpdatedAt)> SeedAsync(WebApplicationFactory<Program> factory, Contact contact)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        contact.ContactId = Guid.NewGuid();
        // Sub-second ticks: the precondition compares exactly, so this proves the value survives the JSON
        // round trip the client makes when it echoes it back.
        contact.UpdatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return (contact.ContactId, contact.UpdatedAt);
    }

    private static async Task<Guid> SeedPhotoTagAsync(WebApplicationFactory<Program> factory, Guid contactId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var photoId = Guid.NewGuid();
        context.PhotoPeople.Add(new PhotoPerson { PhotoId = photoId, ContactId = contactId });
        await context.SaveChangesAsync();
        return photoId;
    }

    private static async Task SeedAssociatedPropertyAsync(WebApplicationFactory<Program> factory, Guid associationId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var propertyId = Guid.NewGuid();
        context.Properties.Add(new Property
        {
            PropertyId = propertyId,
            Name = name,
            Description = "",
            Type = PropertyType.RealEstate,
            CurrencyCode = "NOK",
            RealEstateDetails = new RealEstateDetails
            {
                PropertyId = propertyId,
                Kind = RealEstateKind.House,
                HomeownerAssociationId = associationId,
            },
        });
        await context.SaveChangesAsync();
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    private sealed class PhotoFactory() : OdysseyApiFactory(
        [PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.PhotosUpdate, PermissionClaims.FilesRead],
        ActorUserId);
}
