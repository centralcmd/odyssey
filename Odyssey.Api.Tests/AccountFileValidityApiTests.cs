using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextAccountFileType = Odyssey.Context.AccountFileType;
using DtoAccountFileType = Odyssey.Dtos.Finance.AccountFileType;
using ContextAccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// AC 18 on the endpoint it names — <c>GET /api/accounts/{accountId}/files</c> (issue #146 §8.6).
/// </summary>
/// <remarks>
/// The account surface has accepted <c>ValidTo &lt; ValidFrom</c> since the fields were introduced,
/// so rows violating the new rule may already exist. This pins the important half of that
/// back-compatibility decision: the rule is applied on <b>write</b> only. A violating row is returned
/// by the read exactly as stored — nothing <c>500</c>s, nothing is hidden, and no list is filtered.
/// Making the read enforce a write rule would turn stale data into an outage.
///
/// <para>
/// This is the account twin of <c>ContractFileValidityApiTests.ListFiles_APreExistingViolatingRow_…</c>.
/// Both are needed: they are different controllers and different service methods, and only this one
/// is the endpoint AC 18 names.
/// </para>
/// </remarks>
public sealed class AccountFileValidityApiTests
{
    private const string Path = "/api/accounts";
    private const string ActorUserId = "account-doc-actor";

    private static readonly DateTime ValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ValidTo = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);

    [Fact]
    public async Task GetAccountFiles_APreExistingViolatingRow_IsStillReturnedUnchanged()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsRead]);
        using var client = factory.CreateClient();

        // Written past the service — the only way such a row can exist now that both write paths
        // reject the ordering, and exactly how one written before this change looks.
        var (accountId, fileId) = await SeedViolatingRowAsync(factory);

        var response = await client.GetAsync($"{Path}/{accountId}/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = Assert.Single((await response.Content.ReadFromJsonAsync<List<ExistingAccountFile>>())!);
        Assert.Equal(fileId, file.FileMetadata.Id);
        Assert.Equal(ValidTo, file.ValidFrom);
        Assert.Equal(ValidFrom, file.ValidTo);
    }

    /// <summary>
    /// The other half of §8.6 over HTTP: the same row's <c>PUT</c> now rejects what it once accepted,
    /// on the <c>ValidTo</c> field, which is the one route by which the new rule can bite an existing
    /// row — and the point at which it is surfaced to the person able to fix it.
    /// </summary>
    [Fact]
    public async Task UpdateAccountFile_ResendingAViolatingRowUnchanged_IsNowRejectedOnValidTo()
    {
        await using var factory = new ApiFactory(
            [PermissionClaims.AccountsRead, PermissionClaims.AccountsUpdate]);
        using var client = factory.CreateClient();
        var (accountId, fileId) = await SeedViolatingRowAsync(factory);

        var put = await client.PutAsJsonAsync($"{Path}/{accountId}/files/{fileId}", new UpdateAccountFileRequest
        {
            FileType = DtoAccountFileType.Statement,
            ValidFrom = ValidTo,
            ValidTo = ValidFrom,
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var problem = await put.Content.ReadFromJsonAsync<ValidationProblemShape>();
        Assert.NotNull(problem?.Errors);
        Assert.Contains("ValidTo", problem!.Errors!.Keys);
    }

    private sealed record ValidationProblemShape(Dictionary<string, string[]>? Errors);

    /// <summary>An account file whose stored period is inverted, written straight to the store.</summary>
    private static async Task<(Guid AccountId, Guid FileId)> SeedViolatingRowAsync(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await db.Database.EnsureCreatedAsync();

        var account = new Account
        {
            Name = "Salary account",
            Description = "Primary",
            Opened = DateTime.UtcNow,
            AccountType = ContextAccountType.CheckingAccount,
            CurrencyCode = "USD",
        };
        db.Accounts.Add(account);

        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = ActorUserId,
            FileName = "statement.pdf",
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

        db.AccountFiles.Add(new AccountFile
        {
            Id = Guid.NewGuid(),
            AccountId = account.AccountId,
            FileMetadataId = metadata.Id,
            AttachedByUserId = ActorUserId,
            AttachedAtUtc = DateTime.UtcNow,
            FileType = ContextAccountFileType.Statement,
            // Inverted, as a pre-#146 row can legitimately be.
            ValidFrom = ValidTo,
            ValidTo = ValidFrom,
        });
        await db.SaveChangesAsync();

        return (account.AccountId, metadata.Id);
    }
}
