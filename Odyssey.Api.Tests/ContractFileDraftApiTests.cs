using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using ContractStatus = Odyssey.Dtos.Finance.ContractStatus;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The <c>Draft</c> contract-file type (issue #309): the appended ordinal <c>4</c> is accepted by the
/// attach and update endpoints and read back, the next ordinal is still a <c>400</c>, and the label has
/// no effect on the contract's derived status. AC 2–5.
/// </summary>
public sealed class ContractFileDraftApiTests
{
    private const string Path = "/api/contracts";
    private const string ActorUserId = "contract-draft-actor";

    private static readonly string[] ReadWriteWithFiles =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.FilesRead,
    ];

    private sealed class ApiFactory() : OdysseyApiFactory(ReadWriteWithFiles, ActorUserId);

    [Fact]
    public void Draft_is_appended_at_four_and_every_existing_ordinal_is_unchanged()
    {
        Assert.Equal(0, (int)ContractFileType.Signed);
        Assert.Equal(1, (int)ContractFileType.Amendment);
        Assert.Equal(2, (int)ContractFileType.Correspondence);
        Assert.Equal(3, (int)ContractFileType.Other);
        Assert.Equal(4, (int)ContractFileType.Draft);
        Assert.Equal(4, (int)Odyssey.Context.ContractFileType.Draft);
    }

    /// <summary>AC 2 — a raw integer body, so the wire value itself is what is accepted.</summary>
    [Fact]
    public async Task Attach_WithFileTypeFour_Succeeds_AndBothReadsReturnDraft()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, signed: true);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsync(
            $"{Path}/{contractId}/files", Json($$"""{"fileMetadataId":"{{fileId}}","fileType":4}"""));

        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
        var listed = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        var inlined = Assert.Single(
            (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!.Files);
        Assert.Equal(ContractFileType.Draft, listed.FileType);
        Assert.Equal(ContractFileType.Draft, inlined.FileType);
    }

    /// <summary>AC 3.</summary>
    [Fact]
    public async Task Put_FromSignedToDraft_Persists()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, signed: true);
        var fileId = await AttachAsync(factory, client, contractId, ContractFileType.Signed);

        var put = await client.PutAsync($"{Path}/{contractId}/files/{fileId}", Json("""{"fileType":4}"""));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Equal(ContractFileType.Draft, file.FileType);
    }

    /// <summary>AC 4 — the range widened by exactly one value.</summary>
    [Fact]
    public async Task Attach_WithFileTypeFive_IsRejected()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, signed: true);
        var fileId = await SeedFileAsync(factory);

        var attach = await client.PostAsync(
            $"{Path}/{contractId}/files", Json($$"""{"fileMetadataId":"{{fileId}}","fileType":5}"""));

        Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
    }

    /// <summary>AC 4.</summary>
    [Fact]
    public async Task Put_WithFileTypeFive_IsRejected_AndLeavesTheTypeUnchanged()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, signed: true);
        var fileId = await AttachAsync(factory, client, contractId, ContractFileType.Draft);

        var put = await client.PutAsync($"{Path}/{contractId}/files/{fileId}", Json("""{"fileType":5}"""));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var file = Assert.Single(
            (await client.GetFromJsonAsync<List<ExistingContractFile>>($"{Path}/{contractId}/files"))!);
        Assert.Equal(ContractFileType.Draft, file.FileType);
    }

    /// <summary>
    /// AC 5 — a contract whose only file is <c>Draft</c> derives the same status as with that file typed
    /// <c>Other</c>, on both a signed contract and an unsigned one. The label shares a word with
    /// <see cref="ContractStatus.Draft"/> and must never drive it.
    /// </summary>
    [Theory]
    [InlineData(true, ContractStatus.Active)]
    [InlineData(false, ContractStatus.Ready)]
    public async Task A_draft_file_has_no_effect_on_the_contract_status(bool signed, ContractStatus expected)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client, signed);
        var fileId = await AttachAsync(factory, client, contractId, ContractFileType.Other);
        var asOther = await StatusAsync(client, contractId);

        var put = await client.PutAsync($"{Path}/{contractId}/files/{fileId}", Json("""{"fileType":4}"""));
        put.EnsureSuccessStatusCode();
        var asDraft = await StatusAsync(client, contractId);

        Assert.Equal(expected, asOther);
        Assert.Equal(asOther, asDraft);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<ContractStatus> StatusAsync(HttpClient client, Guid contractId) =>
        (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!.Status;

    private static async Task<Guid> CreateContractAsync(HttpClient client, bool signed)
    {
        var post = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Service agreement",
            Type = ContractType.Service,
            StartDate = DateTime.UtcNow.AddDays(-30),
            Ready = DateTime.UtcNow.AddDays(-40),
            Signed = signed ? DateTime.UtcNow.AddDays(-35) : null,
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    private static async Task<Guid> AttachAsync(
        OdysseyApiFactory factory, HttpClient client, Guid contractId, ContractFileType fileType)
    {
        var fileId = await SeedFileAsync(factory);
        var attach = await client.PostAsJsonAsync($"{Path}/{contractId}/files",
            new AttachContractFileRequest { FileMetadataId = fileId, FileType = fileType });
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
            FileName = "proposal-draft.pdf",
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
}
