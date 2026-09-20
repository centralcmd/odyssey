using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using DtoAccountFileType = Odyssey.Dtos.Finance.AccountFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;

namespace Odyssey.Core.Tests;

/// <summary>
/// The account-file write paths route through the same <see cref="DocumentValidity"/> rule the
/// contract paths do (issue #146 §8.2/§8.3), so the two document surfaces cannot drift. This is a
/// deliberate behaviour change on a shipped endpoint: the ordering and range checks are new, and the
/// three dates are now normalised to UTC before storage. AC 9, 17 and 22 on the account surface.
/// </summary>
public class AccountFileValidityTests
{
    private static async Task<(AccountService Service, Guid AccountId, Guid FileId)> SetupAsync(
        OdysseyContext context)
    {
        var service = new AccountService(context, TestContextFactory.EmptyContactLookup());
        var account = await service.Create(new NewAccount
        {
            Name = "Filed",
            Description = string.Empty,
            AccountType = DtoAccountType.CheckingAccount,
            Archived = false,
        });

        var fileId = Guid.NewGuid();
        context.FileMetadata.Add(new FileMetadata
        {
            Id = fileId,
            UploadedByUserId = "user-1",
            FileName = "statement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            FileBlobId = Guid.NewGuid(),
            UploadedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        return (service, account.AccountId, fileId);
    }

    /// <summary>AC 9 — on the account attach path.</summary>
    [Fact]
    public async Task AttachFileToAccount_InvertedTerm_ThrowsOnValidTo_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId, fileId) = await SetupAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFileToAccount(accountId, fileId, "user-1", DtoAccountFileType.Other,
                new AttachAccountFileRequest(
                    fileId,
                    ValidFrom: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    ValidTo: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc))));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
        Assert.False(await context.AccountFiles.AnyAsync());
    }

    /// <summary>AC 9 — on the account update path.</summary>
    [Fact]
    public async Task UpdateAccountFileType_InvertedTerm_ThrowsOnValidTo_AndMutatesNothing()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId, fileId) = await SetupAsync(context);
        await service.AttachFileToAccount(accountId, fileId, "user-1");

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateAccountFileType(accountId, fileId, new UpdateAccountFileRequest
            {
                FileType = DtoAccountFileType.Statement,
                ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
        Assert.Null((await context.AccountFiles.SingleAsync()).ValidFrom);
    }

    /// <summary>AC 22 — on the account attach path: a 500 at SaveChanges becomes a per-field 400.</summary>
    [Fact]
    public async Task AttachFileToAccount_DateOutsideTheStorableRange_ThrowsOnThatDate()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId, fileId) = await SetupAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFileToAccount(accountId, fileId, "user-1", DtoAccountFileType.Other,
                new AttachAccountFileRequest(fileId, IssuedAt: DateTime.MinValue)));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.IssuedAtField));
    }

    /// <summary>AC 22 — on the account update path.</summary>
    [Fact]
    public async Task UpdateAccountFileType_DateOutsideTheStorableRange_ThrowsOnThatDate()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId, fileId) = await SetupAsync(context);
        await service.AttachFileToAccount(accountId, fileId, "user-1");

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateAccountFileType(accountId, fileId, new UpdateAccountFileRequest
            {
                FileType = DtoAccountFileType.Statement,
                ValidFrom = new DateTime(202, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidFromField));
    }

    /// <summary>AC 17 — on both account write paths.</summary>
    [Fact]
    public async Task AccountFileWrites_NormaliseLocalKindDatesToUtc()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId, fileId) = await SetupAsync(context);
        var local = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        await service.AttachFileToAccount(accountId, fileId, "user-1", DtoAccountFileType.Other,
            new AttachAccountFileRequest(fileId, ValidFrom: local));

        var attached = await context.AccountFiles.SingleAsync();
        Assert.Equal(local.ToUniversalTime(), attached.ValidFrom);
        Assert.Equal(DateTimeKind.Utc, attached.ValidFrom!.Value.Kind);

        await service.UpdateAccountFileType(accountId, fileId, new UpdateAccountFileRequest
        {
            FileType = DtoAccountFileType.Statement,
            IssuedAt = local,
        });

        var updated = await context.AccountFiles.SingleAsync();
        Assert.Equal(local.ToUniversalTime(), updated.IssuedAt);
        Assert.Equal(DateTimeKind.Utc, updated.IssuedAt!.Value.Kind);
    }

    /// <summary>
    /// AC 18's read half at the service seam — the rule is applied on WRITE only. A row that already
    /// violates the ordering (written before this change, or by a hand edit) is still returned exactly
    /// as stored; making a read enforce a write rule would turn stale data into an outage.
    /// </summary>
    [Fact]
    public async Task GetAccountFiles_ReturnsAPreExistingViolatingRowUnchanged()
    {
        await using var context = TestContextFactory.Create();
        var (service, accountId, fileId) = await SetupAsync(context);
        await service.AttachFileToAccount(accountId, fileId, "user-1");

        // Written past the service, exactly as a pre-#146 row or a hand edit would be.
        var row = await context.AccountFiles.SingleAsync();
        row.ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        row.ValidTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        var file = Assert.Single((await service.GetAccountFiles(accountId))!);

        Assert.Equal(row.ValidFrom, file.ValidFrom);
        Assert.Equal(row.ValidTo, file.ValidTo);
    }
}
