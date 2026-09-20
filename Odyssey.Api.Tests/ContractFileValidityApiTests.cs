using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// Endpoint contract for contract-document validity metadata (issue #146): the four fields on attach
/// and on both reads, the new <c>PUT</c> and <c>GET</c>, their claim gating, clear-by-omission and the
/// model-validation rejections. AC 1–8, 10, 12, 16, 18 and 20.
/// </summary>
public sealed class ContractFileValidityApiTests
{
    private const string Path = "/api/contracts";
    private const string ActorUserId = "contract-doc-actor";
    private const string ActorEmail = "doc-actor@example.com";
    private const string DisplayName = "Ada L.";

    private static readonly string[] ReadOnly = [PermissionClaims.ContractsRead];

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.FilesRead,
    ];

    private static readonly DateTime ValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ValidTo = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime IssuedAt = new(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc);

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    // ── AC 1, 2, 4: the two reads agree, and omission means null ───────────────

    [Fact]
    public async Task Attach_WithAllFourFields_IsReturnedByBothReads()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await SeedFileAsync(factory);
        var contactId = await SeedContactAsync(factory);

        var attach = await client.PostAsJsonAsync($"{Path}/{contractId}/files", new AttachContractFileRequest
        {
            FileMetadataId = fileId,
            FileType = ContractFileType.Signed,
            ValidFrom = ValidFrom,
            ValidTo = ValidTo,
            IssuedAt = IssuedAt,
            IssuedBy = contactId,
        });
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);

        var listed = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        var inlined = Assert.Single(
            (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!.Files);

        foreach (var file in new[] { listed, inlined })
        {
            Assert.Equal(ValidFrom, file.ValidFrom);
            Assert.Equal(ValidTo, file.ValidTo);
            Assert.Equal(IssuedAt, file.IssuedAt);
            Assert.Equal(contactId, file.IssuedBy);
        }
    }

    /// <summary>AC 2 — a pre-#146 payload still attaches and stores null for all four.</summary>
    [Fact]
    public async Task Attach_WithAPreChangePayload_Succeeds_AndStoresNullForAllFour()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsync(
            $"{Path}/{contractId}/files",
            Json($$"""{"fileMetadataId":"{{fileId}}","fileType":0}"""));

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Null(file.ValidFrom);
        Assert.Null(file.ValidTo);
        Assert.Null(file.IssuedAt);
        Assert.Null(file.IssuedBy);
    }

    /// <summary>AC 3 — the PUT returns 204 and the list shows the request's values.</summary>
    [Fact]
    public async Task Put_ReturnsNoContent_AndTheListReflectsIt()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId);
        var contactId = await SeedContactAsync(factory);

        var put = await client.PutAsJsonAsync($"{Path}/{contractId}/files/{fileId}", new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            ValidFrom = ValidFrom,
            ValidTo = ValidTo,
            IssuedAt = IssuedAt,
            IssuedBy = contactId,
        });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Equal(ContractFileType.Amendment, file.FileType);
        Assert.Equal(ValidFrom, file.ValidFrom);
        Assert.Equal(ValidTo, file.ValidTo);
        Assert.Equal(IssuedAt, file.IssuedAt);
        Assert.Equal(contactId, file.IssuedBy);
    }

    /// <summary>AC 4 — the body is a full replacement, so omitting the four CLEARS them.</summary>
    [Fact]
    public async Task Put_OmittingTheFourFields_ClearsThem()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var contactId = await SeedContactAsync(factory);
        var fileId = await AttachAsync(factory, client, contractId, id => new AttachContractFileRequest
        {
            FileMetadataId = id,
            FileType = ContractFileType.Signed,
            ValidFrom = ValidFrom,
            ValidTo = ValidTo,
            IssuedAt = IssuedAt,
            IssuedBy = contactId,
        });

        var put = await client.PutAsync(
            $"{Path}/{contractId}/files/{fileId}", Json("""{"fileType":3}"""));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Equal(ContractFileType.Other, file.FileType);
        Assert.Null(file.ValidFrom);
        Assert.Null(file.ValidTo);
        Assert.Null(file.IssuedAt);
        Assert.Null(file.IssuedBy);
    }

    /// <summary>AC 5 — the link is addressed by (contract, file); a cross-contract PUT is a 404 that
    /// mutates neither contract.</summary>
    [Fact]
    public async Task Put_FileAttachedToAnotherContract_ReturnsNotFound_AndMutatesNothing()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractA = await CreateContractAsync(client);
        var contractB = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractA);

        var put = await client.PutAsJsonAsync($"{Path}/{contractB}/files/{fileId}",
            new UpdateContractFileRequest { FileType = ContractFileType.Amendment, ValidFrom = ValidFrom });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        var onA = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractA}/files"))!);
        Assert.Equal(ContractFileType.Signed, onA.FileType);
        Assert.Null(onA.ValidFrom);
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractB}/files"))!);
    }

    // ── AC 6: claim gating ─────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithoutContractsUpdate_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(ReadOnly);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{Path}/{Guid.NewGuid()}/files/{Guid.NewGuid()}",
            new UpdateContractFileRequest { FileType = ContractFileType.Other });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    /// <summary>
    /// <c>files.read</c> is deliberately NOT required on the update verb — unlike attach, it reads no
    /// file metadata and takes no file id the caller did not already have attached.
    /// </summary>
    [Fact]
    public async Task Put_WithContractsUpdateButNoFilesRead_Succeeds()
    {
        await using var seeding = new ApiFactory(ReadWriteWithFiles);
        using var seedingClient = seeding.CreateClient();
        var contractId = await CreateContractAsync(seedingClient);
        var fileId = await AttachAsync(seeding, seedingClient, contractId);

        // A second principal over the SAME store, so the link the first one attached is addressable.
        await using var narrowed = new NarrowedFactory(seeding, [PermissionClaims.ContractsUpdate]);
        using var client = narrowed.CreateClient();

        var put = await client.PutAsJsonAsync($"{Path}/{contractId}/files/{fileId}",
            new UpdateContractFileRequest { FileType = ContractFileType.Amendment });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    [Fact]
    public async Task ListFiles_WithoutContractsRead_ReturnsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.ContractsUpdate]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}/files");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListFiles_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}/{Guid.NewGuid()}/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── AC 7: empty vs. missing ────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_NoDocuments_ReturnsEmptyArray_MissingContractReturnsNotFound()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var empty = await client.GetAsync($"{Path}/{contractId}/files");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await empty.Content.ReadFromJsonAsync<List<ExistingContractFile>>())!);

        var missing = await client.GetAsync($"{Path}/{Guid.NewGuid()}/files");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ── AC 8: attribution ──────────────────────────────────────────────────────

    /// <summary>
    /// AC 8 — the list resolves <c>attachedByName</c> at the API edge under the caller's own claims,
    /// exactly as the inlined collection on <c>GET /api/contracts/{id}</c> does, and the raw id is
    /// returned alongside it. The name is never the GUID: an unresolvable id answers the resolver's
    /// neutral label instead.
    /// </summary>
    [Fact]
    public async Task ListFiles_ResolvesAttachedByName_AndNeverRendersTheIdAsAName()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        await SeedActorProfileAsync(factory);
        var contractId = await CreateContractAsync(client);
        await AttachAsync(factory, client, contractId);

        var listed = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);

        Assert.Equal(ActorUserId, listed.AttachedByUserId);
        Assert.Equal(DisplayName, listed.AttachedByName);
        Assert.NotEqual(ActorUserId, listed.AttachedByName);
    }

    [Fact]
    public async Task ListFiles_UnresolvableAttacher_ReturnsTheNeutralLabel_NotTheId()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        await AttachAsync(factory, client, contractId);

        var listed = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);

        Assert.Equal(UserDisplayNameResolver.UnknownUser, listed.AttachedByName);
        Assert.Equal(ActorUserId, listed.AttachedByUserId);
    }

    /// <summary>
    /// AC 8's other axis — the one the two tests above hold constant. The resolver withholds the
    /// email from a caller without <c>users.read</c> and falls back to it for one that holds it, and
    /// that gate is claim-conditional on the <b>caller</b>. Varying only the attacher's profile would
    /// still pass if the gate were dropped for this endpoint, so the claim is varied here directly.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListFiles_ResolvesTheAttacherUnderTheCallersOwnClaims(bool callerHoldsUsersRead)
    {
        string[] permissions = callerHoldsUsersRead
            ? [.. ReadWriteWithFiles, PermissionClaims.UsersRead]
            : ReadWriteWithFiles;

        await using var factory = new ApiFactory(permissions);
        using var client = factory.CreateClient();
        // No profile at all, so the resolver's only remaining candidate is the email.
        await SeedActorProfileAsync(factory, displayName: null);
        var contractId = await CreateContractAsync(client);
        await AttachAsync(factory, client, contractId);

        var response = await client.GetAsync($"{Path}/{contractId}/files");
        var body = await response.Content.ReadAsStringAsync();
        var listed = Assert.Single((await response.Content.ReadFromJsonAsync<List<ExistingContractFile>>())!);

        if (callerHoldsUsersRead)
        {
            Assert.Equal(ActorEmail, listed.AttachedByName);
        }
        else
        {
            Assert.Equal(UserDisplayNameResolver.UnknownUser, listed.AttachedByName);
            Assert.DoesNotContain(ActorEmail, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── AC 10: scalar ids only ─────────────────────────────────────────────────

    /// <summary>
    /// AC 10 — <c>issuedBy</c> is a scalar id and nothing else. A body sending a populated nested
    /// contact object in its place is refused by model binding, and no <c>Contact</c> is created or
    /// mutated: there is no path from a contract-document write to the contacts table.
    /// </summary>
    [Fact]
    public async Task Attach_WithANestedContactObjectAsIssuedBy_IsRejected_AndTouchesNoContact()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await SeedFileAsync(factory);
        var contactId = await SeedContactAsync(factory);
        var before = await SnapshotContactsAsync(factory);

        var response = await client.PostAsync($"{Path}/{contractId}/files", Json($$"""
            {
              "fileMetadataId": "{{fileId}}",
              "fileType": 0,
              "issuedBy": { "contactId": "{{contactId}}", "name": "Injected Ltd",
                            "normalizedName": "injected ltd", "type": 1 }
            }
            """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotContactsAsync(factory));
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
    }

    // ── The archive state gates nothing ────────────────────────────────────────

    /// <summary>
    /// <b>A document edit is accepted on an archived contract.</b> Archival hides a contract from the
    /// default list; it does not lock it, and correcting a filed document's dates is ordinary work on
    /// a closed agreement. The stamp survives the write, so the edit did not quietly restore it.
    /// </summary>
    [Fact]
    public async Task Put_AgainstAnArchivedContract_Succeeds()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, endDate: DateTime.UtcNow.AddDays(-1));
        var fileId = await AttachAsync(factory, client, contractId);
        await SetArchivedAsync(factory, contractId, DateTime.UtcNow);

        var request = new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            ValidFrom = ValidFrom,
        };

        var accepted = await client.PutAsJsonAsync($"{Path}/{contractId}/files/{fileId}", request);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Equal(ValidFrom, file.ValidFrom);
        Assert.NotNull((await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!.Archived);
    }

    // ── AC 16, 20: model validation ────────────────────────────────────────────

    /// <summary>AC 16 — an out-of-range enum is refused by model validation, before the service runs.</summary>
    [Fact]
    public async Task Put_FileTypeOutsideTheEnumRange_ReturnsBadRequest_AndMutatesNothing()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId);

        var put = await client.PutAsync(
            $"{Path}/{contractId}/files/{fileId}", Json("""{"fileType":99}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(ContractFileType.Signed, Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!).FileType);
    }

    /// <summary>
    /// AC 20 — a body omitting the <c>fileType</c> key is a <c>400</c>, not a default. The stored type
    /// is unchanged and in particular is NOT silently rewritten to <c>Signed</c>, whose ordinal is 0 —
    /// which would mark an arbitrary attachment as the signed copy of the contract.
    /// </summary>
    [Fact]
    public async Task Put_OmittingFileType_ReturnsBadRequest_AndDoesNotDefaultToSigned()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId, fileType: ContractFileType.Correspondence);

        var put = await client.PutAsync(
            $"{Path}/{contractId}/files/{fileId}", Json($$"""{"validFrom":"{{ValidFrom:O}}"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Equal(ContractFileType.Correspondence, file.FileType);
        Assert.NotEqual(ContractFileType.Signed, file.FileType);
        Assert.Null(file.ValidFrom);
    }

    // ── Per-field problem details (§9.4, §9.5, §9.9 over HTTP) ─────────────────

    [Fact]
    public async Task Put_InvertedTerm_ReturnsBadRequest_WithTheErrorOnValidTo()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId);

        var put = await client.PutAsJsonAsync($"{Path}/{contractId}/files/{fileId}", new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            ValidFrom = ValidTo,
            ValidTo = ValidFrom,
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("ValidTo", await ErrorKeysAsync(put));
    }

    [Fact]
    public async Task Put_UnknownIssuer_ReturnsBadRequest_WithTheErrorOnIssuedBy()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId);

        var put = await client.PutAsJsonAsync($"{Path}/{contractId}/files/{fileId}", new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            IssuedBy = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("IssuedBy", await ErrorKeysAsync(put));
    }

    /// <summary>§9.9 — a date the storage column cannot hold is a per-field 400, never a 500.</summary>
    [Fact]
    public async Task Put_DateOutsideTheStorableRange_ReturnsBadRequestNamingThatDate()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId);

        var put = await client.PutAsync($"{Path}/{contractId}/files/{fileId}",
            Json("""{"fileType":1,"issuedAt":"0202-01-01T00:00:00Z"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("IssuedAt", await ErrorKeysAsync(put));
    }

    // ── AC 18: the read path enforces nothing ──────────────────────────────────

    /// <summary>
    /// AC 18 — a row that already violates the ordering rule is still returned unchanged. The rule is
    /// applied on write only; making a read enforce a write rule would turn stale data into an outage.
    /// The contract half of the pair; the ACCOUNT endpoint AC 18 actually names is asserted in
    /// <c>AccountFileValidityApiTests</c>, because that is the surface with rows predating the rule.
    /// </summary>
    [Fact]
    public async Task ListFiles_APreExistingViolatingRow_IsStillReturnedUnchanged()
    {
        await using var factory = new ApiFactory(ReadWriteWithFiles);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var fileId = await AttachAsync(factory, client, contractId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var row = await db.ContractFiles.SingleAsync(f => f.FileMetadataId == fileId);
            row.ValidFrom = ValidTo;
            row.ValidTo = ValidFrom;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"{Path}/{contractId}/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = Assert.Single((await response.Content.ReadFromJsonAsync<List<ExistingContractFile>>())!);
        Assert.Equal(ValidTo, file.ValidFrom);
        Assert.Equal(ValidFrom, file.ValidTo);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class NarrowedFactory(OdysseyApiFactory sharing, IReadOnlyCollection<string> permissions)
        : OdysseyApiFactory(permissions, ActorUserId, sharingStoreWith: sharing);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<IReadOnlyCollection<string>> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }

    private static async Task<Guid> CreateContractAsync(HttpClient client, DateTime? endDate = null)
    {
        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Service agreement",
            Type = ContractType.Service,
            StartDate = DateTime.UtcNow.AddDays(-30),
            EndDate = endDate,
            Ready = DateTime.UtcNow.AddDays(-40),
            Signed = DateTime.UtcNow.AddDays(-35),
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    private static async Task<Guid> AttachAsync(
        OdysseyApiFactory factory,
        HttpClient client,
        Guid contractId,
        Func<Guid, AttachContractFileRequest>? build = null,
        ContractFileType fileType = ContractFileType.Signed)
    {
        var fileId = await SeedFileAsync(factory);
        var request = build?.Invoke(fileId)
            ?? new AttachContractFileRequest { FileMetadataId = fileId, FileType = fileType };

        var attach = await client.PostAsJsonAsync($"{Path}/{contractId}/files", request);
        attach.EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> SeedFileAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = ActorUserId,
            FileName = "agreement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
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

    private static async Task SeedActorProfileAsync(OdysseyApiFactory factory, string? displayName = DisplayName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        db.Users.Add(new ApplicationUser { Id = ActorUserId, UserName = ActorEmail, Email = ActorEmail });
        if (displayName is not null)
            db.UserProfiles.Add(new UserProfile { UserId = ActorUserId, DisplayName = displayName });
        await db.SaveChangesAsync();
    }

    private static async Task<string> SnapshotContactsAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var rows = await db.Contacts
            .AsNoTracking()
            .Include(contact => contact.OrganizationDetails)
            .OrderBy(contact => contact.ContactId)
            .Select(contact => new
            {
                contact.ContactId,
                contact.NormalizedName,
                contact.Type,
                contact.Archived,
                LegalName = contact.OrganizationDetails!.LegalName,
            })
            .ToListAsync();

        return JsonSerializer.Serialize(rows);
    }

    private static async Task SetArchivedAsync(OdysseyApiFactory factory, Guid contractId, DateTime? archived)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var contract = await db.Contracts.SingleAsync(c => c.ContractId == contractId);
        contract.Archived = archived;
        await db.SaveChangesAsync();
    }
}
