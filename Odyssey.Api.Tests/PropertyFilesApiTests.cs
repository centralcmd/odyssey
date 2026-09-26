using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using static Odyssey.Api.Tests.PropertyApiTestSupport;
using PropertyFileType = Odyssey.Dtos.Finance.PropertyFileType;

namespace Odyssey.Api.Tests;

/// <summary>
/// Endpoint contract for property documents (issue #210 §5): attach, list, update, download and detach,
/// their claim gating, validation, the shared content-type allow-list and the absence of a cap.
/// </summary>
public sealed class PropertyFilesApiTests
{
    private const string ActorUserId = "property-doc-actor";
    private const string ActorEmail = "property-doc-actor@example.com";
    private const string DisplayName = "Ada L.";

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.PropertiesRead, PermissionClaims.PropertiesUpdate, PermissionClaims.FilesRead,
    ];

    private static readonly DateTime ValidFrom = new(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ValidTo = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime IssuedAt = new(2021, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions, OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(permissions, ActorUserId, sharingStoreWith: sharingStoreWith);

    private static string FilesPath(Guid propertyId) => $"{PropertyPath(propertyId)}/files";

    private static string FilePath(Guid propertyId, Guid fileId) => $"{FilesPath(propertyId)}/{fileId}";

    // ── AC 1: attach, then list ────────────────────────────────────────────────

    [Fact]
    public async Task Attach_ReturnsCreatedWithDownloadLocation_AndTheListCarriesEveryField()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        await SeedActorProfileAsync(factory);
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await SeedFileAsync(factory);
        var contactId = await SeedContactAsync(factory);

        var attach = await client.PostAsJsonAsync(FilesPath(propertyId), new AttachPropertyFileRequest
        {
            FileMetadataId = fileId,
            FileType = PropertyFileType.Deed,
            ValidFrom = ValidFrom,
            ValidTo = ValidTo,
            IssuedAt = IssuedAt,
            IssuedBy = contactId,
        });

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        Assert.EndsWith(FilePath(propertyId, fileId), attach.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        var file = Assert.Single((await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!);
        Assert.Equal(propertyId, file.PropertyId);
        Assert.Equal(fileId, file.FileMetadata.Id);
        Assert.Equal(PropertyFileType.Deed, file.FileType);
        Assert.Equal(ValidFrom, file.ValidFrom);
        Assert.Equal(ValidTo, file.ValidTo);
        Assert.Equal(IssuedAt, file.IssuedAt);
        Assert.Equal(contactId, file.IssuedBy);
        Assert.Equal(ActorUserId, file.AttachedByUserId);
        Assert.Equal(DisplayName, file.AttachedByName);
        Assert.Equal(DisplayName, file.FileMetadata.UploadedByName);
    }

    [Fact]
    public async Task Attach_OmittingFileType_DefaultsToOther()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsync(FilesPath(propertyId), Json($$"""{"fileMetadataId":"{{fileId}}"}"""));

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var file = Assert.Single((await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!);
        Assert.Equal(PropertyFileType.Other, file.FileType);
        Assert.Null(file.ValidFrom);
        Assert.Null(file.IssuedBy);
    }

    [Fact]
    public async Task List_IsOrderedByAttachmentTime_AndScopedToTheProperty()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var otherId = await SeedPropertyAsync(factory, name: "Cabin");
        var first = await AttachAsync(factory, client, propertyId);
        var second = await AttachAsync(factory, client, propertyId);
        await AttachAsync(factory, client, otherId);

        var files = (await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!;

        Assert.Equal([first, second], files.Select(f => f.FileMetadata.Id));
    }

    // ── AC 2: download ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_ReturnsTheBytes_WithSafeDownloadHeaders()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        byte[] bytes = [0x25, 0x50, 0x44, 0x46, 0x2d];
        var fileId = await AttachAsync(factory, client, propertyId, content: bytes);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var response = await client.GetAsync(FilePath(propertyId, fileId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal($"\"{expectedHash}\"", response.Headers.ETag!.Tag);
    }

    // ── AC 3: update ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_ReplacesTheMetadata_AndOmissionClears()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var contactId = await SeedContactAsync(factory);
        var fileId = await AttachAsync(factory, client, propertyId, build: id => new AttachPropertyFileRequest
        {
            FileMetadataId = id, FileType = PropertyFileType.Deed,
            ValidFrom = ValidFrom, ValidTo = ValidTo, IssuedAt = IssuedAt, IssuedBy = contactId,
        });

        var put = await client.PutAsync(FilePath(propertyId, fileId), Json("""{"fileType":3}"""));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var file = Assert.Single((await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!);
        Assert.Equal(PropertyFileType.Valuation, file.FileType);
        Assert.Null(file.ValidFrom);
        Assert.Null(file.ValidTo);
        Assert.Null(file.IssuedAt);
        Assert.Null(file.IssuedBy);
    }

    [Fact]
    public async Task Put_OmittingFileType_IsBadRequest_AndChangesNothing()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await AttachAsync(factory, client, propertyId, fileType: PropertyFileType.Deed);

        var put = await client.PutAsync(FilePath(propertyId, fileId), Json("""{"validTo":"2027-01-01T00:00:00Z"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var file = Assert.Single((await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!);
        Assert.Equal(PropertyFileType.Deed, file.FileType);
        Assert.Null(file.ValidTo);
    }

    [Fact]
    public async Task Put_WithAnUndefinedFileType_IsBadRequest()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await AttachAsync(factory, client, propertyId);

        var put = await client.PutAsync(FilePath(propertyId, fileId), Json("""{"fileType":99}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ── AC 4: detach ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Detach_RemovesTheLinkOnly_AndTheFileSurvives()
    {
        await using var factory = new ApiFactory([.. ReadWriteWithFiles]);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await AttachAsync(factory, client, propertyId);

        var delete = await client.DeleteAsync(FilePath(propertyId, fileId));

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/files/{fileId}/content")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var metadata = await db.FileMetadata.SingleAsync(m => m.Id == fileId);
        Assert.True(await db.FileBlob.AnyAsync(b => b.Id == metadata.FileBlobId));
    }

    // ── AC 5: claims ───────────────────────────────────────────────────────────

    public static TheoryData<string[], HttpStatusCode> AttachClaimCases => new()
    {
        { [PermissionClaims.PropertiesRead, PermissionClaims.FilesRead], HttpStatusCode.Forbidden },
        { [PermissionClaims.PropertiesRead, PermissionClaims.PropertiesUpdate], HttpStatusCode.Forbidden },
        { [PermissionClaims.PropertiesUpdate, PermissionClaims.FilesRead], HttpStatusCode.Created },
    };

    [Theory]
    [MemberData(nameof(AttachClaimCases))]
    public async Task Attach_RequiresPropertiesUpdateAndFilesRead(string[] claims, HttpStatusCode expected)
    {
        await using var factory = new ApiFactory(claims);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsJsonAsync(
            FilesPath(propertyId), new AttachPropertyFileRequest { FileMetadataId = fileId });

        Assert.Equal(expected, attach.StatusCode);
        Assert.Equal(expected == HttpStatusCode.Created, await AttachedCountAsync(factory, propertyId) == 1);
    }

    [Fact]
    public async Task ListAndDownload_RequirePropertiesRead()
    {
        await using var seed = new ApiFactory(ReadWriteWithFiles);
        using var seedClient = seed.CreateClient();
        var propertyId = await SeedPropertyAsync(seed);
        var fileId = await AttachAsync(seed, seedClient, propertyId);

        await using var denied = new ApiFactory([PermissionClaims.PropertiesUpdate, PermissionClaims.FilesRead], seed);
        using var deniedClient = denied.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await deniedClient.GetAsync(FilesPath(propertyId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deniedClient.GetAsync(FilePath(propertyId, fileId))).StatusCode);

        await using var admitted = new ApiFactory([PermissionClaims.PropertiesRead], seed);
        using var admittedClient = admitted.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await admittedClient.GetAsync(FilesPath(propertyId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admittedClient.GetAsync(FilePath(propertyId, fileId))).StatusCode);
    }

    [Fact]
    public async Task UpdateAndDetach_RequirePropertiesUpdate_ButNotFilesRead()
    {
        await using var seed = new ApiFactory(ReadWriteWithFiles);
        using var seedClient = seed.CreateClient();
        var propertyId = await SeedPropertyAsync(seed);
        var fileId = await AttachAsync(seed, seedClient, propertyId);

        await using var denied = new ApiFactory([PermissionClaims.PropertiesRead, PermissionClaims.FilesRead], seed);
        using var deniedClient = denied.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await deniedClient.PutAsync(FilePath(propertyId, fileId), Json("""{"fileType":1}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deniedClient.DeleteAsync(FilePath(propertyId, fileId))).StatusCode);

        await using var admitted = new ApiFactory([PermissionClaims.PropertiesUpdate], seed);
        using var admittedClient = admitted.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent,
            (await admittedClient.PutAsync(FilePath(propertyId, fileId), Json("""{"fileType":1}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admittedClient.DeleteAsync(FilePath(propertyId, fileId))).StatusCode);
    }

    // ── AC 6: the allow-list ───────────────────────────────────────────────────

    [Fact]
    public async Task Attach_WithADisallowedStoredContentType_IsBadRequest_AndWritesNothing()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await SeedFileAsync(factory, contentType: "text/html");

        var attach = await client.PostAsJsonAsync(
            FilesPath(propertyId), new AttachPropertyFileRequest { FileMetadataId = fileId });

        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
        Assert.Contains("Content type 'text/html' is not allowed for property documents.",
            await attach.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await AttachedCountAsync(factory, propertyId));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task Attach_AcceptsEveryAllowListedType(string contentType)
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await SeedFileAsync(factory, contentType: contentType);

        var attach = await client.PostAsJsonAsync(
            FilesPath(propertyId), new AttachPropertyFileRequest { FileMetadataId = fileId });

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
    }

    [Fact]
    public async Task Attach_AnUnknownFile_IsNotFound()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);

        var attach = await client.PostAsJsonAsync(
            FilesPath(propertyId), new AttachPropertyFileRequest { FileMetadataId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);
    }

    // ── AC 7, 8: duplicates and no cap ─────────────────────────────────────────

    [Fact]
    public async Task Attach_TheSameFileTwiceToOneProperty_IsConflict_ButTwoPropertiesMayShareIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var first = await SeedPropertyAsync(factory);
        var second = await SeedPropertyAsync(factory, name: "Cabin");
        var fileId = await SeedFileAsync(factory);
        var body = new AttachPropertyFileRequest { FileMetadataId = fileId };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(FilesPath(first), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(FilesPath(first), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(FilesPath(second), body)).StatusCode);
        Assert.Equal(1, await AttachedCountAsync(factory, first));
    }

    [Fact]
    public async Task Attach_SixtyFiles_AllSucceed_BecauseThereIsNoCap()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);

        for (var i = 0; i < 60; i++)
        {
            await AttachAsync(factory, client, propertyId);
        }

        Assert.Equal(60, (await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(propertyId)))!.Count);
    }

    // ── AC 9, 10: validation ───────────────────────────────────────────────────

    [Fact]
    public async Task Attach_WithValidToBeforeValidFrom_IsBadRequestKeyedValidTo()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsJsonAsync(FilesPath(propertyId), new AttachPropertyFileRequest
        {
            FileMetadataId = fileId, ValidFrom = ValidTo, ValidTo = ValidFrom,
        });

        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
        Assert.Contains("ValidTo", await ErrorKeysAsync(attach));
        Assert.Equal(0, await AttachedCountAsync(factory, propertyId));
    }

    [Theory]
    [InlineData("validFrom", "ValidFrom")]
    [InlineData("validTo", "ValidTo")]
    [InlineData("issuedAt", "IssuedAt")]
    public async Task Put_WithADateBeforeYear1000_IsBadRequestKeyedOnThatField(string json, string key)
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await AttachAsync(factory, client, propertyId);

        var put = await client.PutAsync(
            FilePath(propertyId, fileId), Json($$"""{"fileType":1,"{{json}}":"0999-12-31T00:00:00Z"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains(key, await ErrorKeysAsync(put));
    }

    [Fact]
    public async Task AttachAndPut_WithAnUnknownIssuer_AreBadRequestKeyedIssuedBy()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var unattached = await SeedFileAsync(factory);
        var attached = await AttachAsync(factory, client, propertyId);

        var attach = await client.PostAsJsonAsync(FilesPath(propertyId), new AttachPropertyFileRequest
        {
            FileMetadataId = unattached, IssuedBy = Guid.NewGuid(),
        });
        var put = await client.PutAsJsonAsync(FilePath(propertyId, attached), new UpdatePropertyFileRequest
        {
            FileType = PropertyFileType.Deed, IssuedBy = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
        Assert.Contains("IssuedBy", await ErrorKeysAsync(attach));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("IssuedBy", await ErrorKeysAsync(put));
    }

    // ── AC 11, 15: not found ───────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownProperty_IsNotFound_OnAllFiveEndpoints()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        await EnsureCreatedAsync(factory);
        var fileId = await SeedFileAsync(factory);
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(
            FilesPath(missing), new AttachPropertyFileRequest { FileMetadataId = fileId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FilesPath(missing))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsync(FilePath(missing, fileId), Json("""{"fileType":1}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FilePath(missing, fileId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(FilePath(missing, fileId))).StatusCode);
    }

    [Fact]
    public async Task APropertyWithNoDocuments_ListsAnEmptyArray()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);

        var response = await client.GetAsync(FilesPath(propertyId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AFileAttachedToA_IsNotFoundThroughB()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var a = await SeedPropertyAsync(factory);
        var b = await SeedPropertyAsync(factory, name: "Cabin");
        var fileId = await AttachAsync(factory, client, a, fileType: PropertyFileType.Deed);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FilePath(b, fileId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsync(FilePath(b, fileId), Json("""{"fileType":10}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(FilePath(b, fileId))).StatusCode);

        var file = Assert.Single((await client.GetFromJsonAsync<List<ExistingPropertyFile>>(FilesPath(a)))!);
        Assert.Equal(PropertyFileType.Deed, file.FileType);
    }

    // ── AC 12, 13, 14: limits unchanged, no leaked personal data, no over-posting ──

    [Fact]
    public async Task PropertyLimits_AreUnchanged()
    {
        await using var factory = new ApiFactory([PermissionClaims.PropertiesRead]);
        using var client = factory.CreateClient();
        await EnsureCreatedAsync(factory);

        using var document = JsonDocument.Parse(await client.GetStringAsync(LimitsPath));

        Assert.Equal(["maxSmartTagsPerProperty"], document.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheList_CarriesNoIssuerNameAndNoPropertyPersonalData()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory, city: "Trondheim-Unique-City");
        var contactId = await SeedContactAsync(factory);
        await AttachAsync(factory, client, propertyId, build: id => new AttachPropertyFileRequest
        {
            FileMetadataId = id, IssuedBy = contactId,
        });

        var body = await client.GetStringAsync(FilesPath(propertyId));

        Assert.Contains(contactId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acme", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Trondheim-Unique-City", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Maple St house", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_IgnoresNestedObjectsAndABodyPropertyId()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var target = await SeedPropertyAsync(factory);
        var decoy = await SeedPropertyAsync(factory, name: "Decoy");
        var contactId = await SeedContactAsync(factory);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsync(FilesPath(target), Json($$"""
            {
              "fileMetadataId": "{{fileId}}",
              "propertyId": "{{decoy}}",
              "issuedBy": "{{contactId}}",
              "fileMetadata": { "id": "{{fileId}}", "fileName": "renamed.exe", "contentType": "text/html" },
              "property": { "propertyId": "{{decoy}}", "name": "Hijacked" },
              "contact": { "contactId": "{{contactId}}", "normalizedName": "hijacked" }
            }
            """));

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        Assert.Equal(1, await AttachedCountAsync(factory, target));
        Assert.Equal(0, await AttachedCountAsync(factory, decoy));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var metadata = await db.FileMetadata.AsNoTracking().SingleAsync(m => m.Id == fileId);
        Assert.Equal("deed.pdf", metadata.FileName);
        Assert.Equal("application/pdf", metadata.ContentType);
        Assert.Equal("Decoy", (await db.Properties.AsNoTracking().SingleAsync(p => p.PropertyId == decoy)).Name);
        Assert.Equal("acme corp", (await db.Contacts.AsNoTracking().SingleAsync(c => c.ContactId == contactId)).NormalizedName);
        Assert.Equal(1, await db.Contacts.CountAsync());
        Assert.Equal(2, await db.Properties.CountAsync());
    }

    // ── AC 17: the property delete removes the links, not the files ────────────

    [Fact]
    public async Task DeletingTheProperty_RemovesItsLinks_AndKeepsTheFile()
    {
        await using var factory = new ApiFactory([.. ReadWriteWithFiles, PermissionClaims.PropertiesDelete]);
        using var client = factory.CreateClient();
        var propertyId = await SeedPropertyAsync(factory);
        var fileId = await AttachAsync(factory, client, propertyId);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(PropertyPath(propertyId))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await db.PropertyFiles.AnyAsync(f => f.PropertyId == propertyId));
        Assert.True(await db.FileMetadata.AnyAsync(m => m.Id == fileId));
    }

    // ── AC 20: one allow-list ──────────────────────────────────────────────────

    /// <summary>
    /// The document content-type allow-list is declared once, in <c>DocumentContentTypes</c>, and both
    /// attach paths reach it through the one shared helper — a second copy is the drift-prone duplicate
    /// issue #210 §6 forbids. (Other surfaces' upload lists, e.g. tax statements, are separate rules.)
    /// </summary>
    [Fact]
    public void TheDocumentAllowList_IsDeclaredExactlyOnce()
    {
        var root = RepositoryRoot();
        var declaration = File.ReadAllText(Path.Combine(root, "Odyssey.Dtos", "Finance", "DocumentContentTypes.cs"));
        Assert.Contains("\"application/pdf\"", declaration, StringComparison.Ordinal);

        var helper = File.ReadAllText(Path.Combine(root, "Odyssey.Api", "DocumentAttachmentExtensions.cs"));
        Assert.Contains("DocumentContentTypes.Allowed", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("\"application/pdf\"", helper, StringComparison.Ordinal);

        foreach (var controller in new[] { "ContractController.cs", "PropertyFilesController.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, "Odyssey.Api", "Controllers", controller));
            Assert.Contains("ValidateAttachableDocumentAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("\"application/pdf\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AllowedContentTypes", source, StringComparison.Ordinal);
        }
    }

    // ── AC 24: the read-path crossover stays safe ──────────────────────────────

    /// <summary>
    /// Issue #210 §7.3 accepts that <c>properties.read</c> returns file metadata and bytes because no role
    /// holds it without <c>files.read</c>. A role-mapping change that breaks that premise must fail here,
    /// not widen file exposure through the property surface silently.
    /// </summary>
    [Fact]
    public void EveryRoleHoldingPropertiesRead_AlsoHoldsFilesRead()
    {
        var offenders = RolePermissions.RoleClaimMap
            .Where(role => role.Claims.Contains(PermissionClaims.PropertiesRead)
                && !role.Claims.Contains(PermissionClaims.FilesRead))
            .Select(role => role.RoleName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Issue #210 §7.3: these roles hold properties.read without files.read, so the property "
            + "document list and download would expose file data they cannot otherwise read. Revisit the "
            + "property-document read path before changing the role mapping: " + string.Join(", ", offenders));
        Assert.Contains(RolePermissions.RoleClaimMap, role => role.Claims.Contains(PermissionClaims.PropertiesRead));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<IReadOnlyCollection<string>> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }

    private static async Task<Guid> AttachAsync(
        OdysseyApiFactory factory,
        HttpClient client,
        Guid propertyId,
        Func<Guid, AttachPropertyFileRequest>? build = null,
        PropertyFileType fileType = PropertyFileType.Other,
        byte[]? content = null)
    {
        var fileId = await SeedFileAsync(factory, content: content);
        var request = build?.Invoke(fileId)
            ?? new AttachPropertyFileRequest { FileMetadataId = fileId, FileType = fileType };

        var attach = await client.PostAsJsonAsync(FilesPath(propertyId), request);
        attach.EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> SeedFileAsync(
        OdysseyApiFactory factory, string contentType = "application/pdf", byte[]? content = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        var bytes = content ?? [1, 2, 3];
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = bytes };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = ActorUserId,
            FileName = "deed.pdf",
            ContentType = contentType,
            SizeBytes = bytes.Length,
            Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            UploadedAtUtc = DateTime.UtcNow,
            FileBlobId = blob.Id,
            FileBlob = blob,
        };
        db.FileBlob.Add(blob);
        db.FileMetadata.Add(metadata);
        await db.SaveChangesAsync();
        return metadata.Id;
    }

    private static async Task<Guid> SeedContactAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme corp",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme Corp" },
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return contact.ContactId;
    }

    private static async Task SeedActorProfileAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser { Id = ActorUserId, UserName = ActorEmail, Email = ActorEmail });
        db.UserProfiles.Add(new UserProfile { UserId = ActorUserId, DisplayName = DisplayName });
        await db.SaveChangesAsync();
    }

    private static async Task<int> AttachedCountAsync(OdysseyApiFactory factory, Guid propertyId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await db.PropertyFiles.CountAsync(f => f.PropertyId == propertyId);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Odyssey.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Odyssey.sln not found above the test binaries.");
    }
}
