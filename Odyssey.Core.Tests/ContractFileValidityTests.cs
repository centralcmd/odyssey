using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;
using ContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Service-level coverage for contract-document validity metadata (issue #146): the attach path
/// carrying the four fields, the new update and list paths, the archive guard, the issuer check, and
/// the cap that gates row creation but not metadata edits. AC 9, 11, 15, 17 and 22 on the contract
/// surface.
/// </summary>
public class ContractFileValidityTests
{
    private const string TestUserId = "test-user";
    private static readonly DateTime FixedToday = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly OdysseyContext journal = TestContextFactory.CreateJournal();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class StubCaps(int maxFilesPerContract) : ISystemSettingsLookup
    {
        public Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FinanceRequestCaps(25, maxFilesPerContract, 500, 1000, 100, 50, 50));

        public Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InsurancePolicySettings(30, 1000));

        public Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SubscriptionSettings(45, 6, 1000));

        public Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContractSummarySettings(45, 45, 6));
    }

    private ContractService CreateService(OdysseyContext context, int maxFilesPerContract = 50) =>
        new(context, TestContextFactory.ContactLookup(journal), new FixedTimeProvider(FixedToday),
            new StubCaps(maxFilesPerContract), NullLogger<ContractService>.Instance);

    private static NewContract NewContractRequest() => new()
    {
        Name = "Service agreement",
        Type = ContractType.Service,
        StartDate = FixedToday.AddDays(-30),
        Ready = FixedToday.AddDays(-40),
        Signed = FixedToday.AddDays(-35),
    };

    private static async Task<Guid> SeedFileAsync(OdysseyContext context)
    {
        var blob = new FileBlob { Id = Guid.NewGuid(), Content = [1, 2, 3] };
        var metadata = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = TestUserId,
            FileName = "agreement.pdf",
            ContentType = "application/pdf",
            SizeBytes = 3,
            Sha256Hash = Guid.NewGuid().ToString("N"),
            UploadedAtUtc = FixedToday,
            FileBlobId = blob.Id,
            FileBlob = blob,
        };
        context.FileBlob.Add(blob);
        context.FileMetadata.Add(metadata);
        await context.SaveChangesAsync();
        return metadata.Id;
    }

    private async Task<Guid> SeedContactAsync()
    {
        var contact = new Contact
        {
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "acme corp",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Acme Corp" },
        };
        journal.Contacts.Add(contact);
        await journal.SaveChangesAsync();
        return contact.ContactId;
    }

    private static AttachContractFileRequest Attach(Guid fileId) => new()
    {
        FileMetadataId = fileId,
        FileType = ContractFileType.Signed,
    };

    // ── Attach carries the four fields ────────────────────────────────────────

    [Fact]
    public async Task AttachFile_WithValidityMetadata_StoresAndReturnsAllFour()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        var contactId = await SeedContactAsync();

        var validFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var issuedAt = new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc);

        var attached = await service.AttachFile(contract.ContractId, new AttachContractFileRequest
        {
            FileMetadataId = fileId,
            FileType = ContractFileType.Signed,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IssuedAt = issuedAt,
            IssuedBy = contactId,
        }, TestUserId);

        Assert.NotNull(attached);
        Assert.Equal(validFrom, attached!.ValidFrom);
        Assert.Equal(validTo, attached.ValidTo);
        Assert.Equal(issuedAt, attached.IssuedAt);
        Assert.Equal(contactId, attached.IssuedBy);
    }

    [Fact]
    public async Task AttachFile_WithoutValidityMetadata_StoresNullForAllFour()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);

        var attached = await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        Assert.NotNull(attached);
        Assert.Null(attached!.ValidFrom);
        Assert.Null(attached.ValidTo);
        Assert.Null(attached.IssuedAt);
        Assert.Null(attached.IssuedBy);
    }

    /// <summary>AC 9 — on the contract attach path.</summary>
    [Fact]
    public async Task AttachFile_InvertedTerm_ThrowsOnValidTo_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFile(contract.ContractId, new AttachContractFileRequest
            {
                FileMetadataId = fileId,
                ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            }, TestUserId));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
        Assert.False(await context.ContractFiles.AnyAsync());
    }

    /// <summary>AC 22 — on the contract attach path.</summary>
    [Fact]
    public async Task AttachFile_DateOutsideTheStorableRange_ThrowsOnThatDate_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFile(contract.ContractId, new AttachContractFileRequest
            {
                FileMetadataId = fileId,
                IssuedAt = new DateTime(202, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }, TestUserId));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.IssuedAtField));
        Assert.False(await context.ContractFiles.AnyAsync());
    }

    /// <summary>AC 11 — on the contract attach path.</summary>
    [Fact]
    public async Task AttachFile_UnknownIssuer_ThrowsOnIssuedBy_AndStoresNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.AttachFile(contract.ContractId, new AttachContractFileRequest
            {
                FileMetadataId = fileId,
                IssuedBy = Guid.NewGuid(),
            }, TestUserId));

        Assert.True(error.Errors!.ContainsKey(nameof(AttachContractFileRequest.IssuedBy)));
        Assert.False(await context.ContractFiles.AnyAsync());
    }

    /// <summary>AC 17 — on the contract attach path.</summary>
    [Fact]
    public async Task AttachFile_LocalKindDate_IsStoredNormalisedToUtc()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        var local = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        await service.AttachFile(contract.ContractId, new AttachContractFileRequest
        {
            FileMetadataId = fileId,
            ValidFrom = local,
        }, TestUserId);

        var stored = await context.ContractFiles.SingleAsync();
        Assert.Equal(local.ToUniversalTime(), stored.ValidFrom);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFile_ReplacesTypeAndAllFourFields()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        var contactId = await SeedContactAsync();
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        var validFrom = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var updated = await service.UpdateFile(contract.ContractId, fileId, new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            ValidFrom = validFrom,
            IssuedBy = contactId,
        });

        Assert.True(updated);
        var files = await service.GetFiles(contract.ContractId);
        var file = Assert.Single(files!);
        Assert.Equal(ContractFileType.Amendment, file.FileType);
        Assert.Equal(validFrom, file.ValidFrom);
        Assert.Equal(contactId, file.IssuedBy);
    }

    /// <summary>The verb is a full replacement — an omitted date or issuer CLEARS the stored value.</summary>
    [Fact]
    public async Task UpdateFile_OmittingTheFourFields_ClearsThem()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        var contactId = await SeedContactAsync();
        await service.AttachFile(contract.ContractId, new AttachContractFileRequest
        {
            FileMetadataId = fileId,
            ValidFrom = FixedToday,
            ValidTo = FixedToday.AddDays(30),
            IssuedAt = FixedToday.AddDays(-1),
            IssuedBy = contactId,
        }, TestUserId);

        await service.UpdateFile(contract.ContractId, fileId,
            new UpdateContractFileRequest { FileType = ContractFileType.Other });

        var file = Assert.Single((await service.GetFiles(contract.ContractId))!);
        Assert.Null(file.ValidFrom);
        Assert.Null(file.ValidTo);
        Assert.Null(file.IssuedAt);
        Assert.Null(file.IssuedBy);
    }

    /// <summary>AC 5 — the link is addressed by (ContractId, FileMetadataId); a file attached to a
    /// different contract is not reachable and nothing is mutated on either.</summary>
    [Fact]
    public async Task UpdateFile_FileAttachedToAnotherContract_ReturnsFalse_AndMutatesNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contractA = await service.Create(NewContractRequest(), userId: null);
        var contractB = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contractA.ContractId, Attach(fileId), TestUserId);

        var updated = await service.UpdateFile(contractB.ContractId, fileId,
            new UpdateContractFileRequest { FileType = ContractFileType.Amendment });

        Assert.False(updated);
        var file = Assert.Single((await service.GetFiles(contractA.ContractId))!);
        Assert.Equal(ContractFileType.Signed, file.FileType);
    }

    [Fact]
    public async Task UpdateFile_MissingContract_ThrowsNotFound()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);

        await Assert.ThrowsAsync<DomainNotFoundException>(() => service.UpdateFile(
            Guid.NewGuid(), Guid.NewGuid(), new UpdateContractFileRequest { FileType = ContractFileType.Other }));
    }

    /// <summary>AC 12 — an archived contract refuses the edit, and succeeds once unarchived.</summary>
    [Fact]
    public async Task UpdateFile_ArchivedContract_IsRefused_ThenSucceedsAfterUnarchiving()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        var entity = await context.Contracts.SingleAsync(c => c.ContractId == contract.ContractId);
        entity.EndDate = FixedToday.AddDays(-1);
        entity.Archived = FixedToday;
        await context.SaveChangesAsync();

        var request = new UpdateContractFileRequest { FileType = ContractFileType.Amendment };
        var error = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.UpdateFile(contract.ContractId, fileId, request));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(
            ContractFileType.Signed,
            Assert.Single((await service.GetFiles(contract.ContractId))!).FileType);

        entity.Archived = null;
        await context.SaveChangesAsync();

        Assert.True(await service.UpdateFile(contract.ContractId, fileId, request));
    }

    /// <summary>AC 15 — the cap gates row creation, not metadata edits, so a contract at its cap can
    /// still have a document's dates corrected.</summary>
    [Fact]
    public async Task UpdateFile_ContractAtItsFileCap_StillSucceeds()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context, maxFilesPerContract: 1);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        // The cap is reached: a second attach is refused.
        var second = await SeedFileAsync(context);
        await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.AttachFile(contract.ContractId, Attach(second), TestUserId));

        var updated = await service.UpdateFile(contract.ContractId, fileId, new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            ValidTo = FixedToday.AddYears(1),
        });

        Assert.True(updated);
    }

    /// <summary>AC 9, 11, 17 and 22 — on the contract update path.</summary>
    [Fact]
    public async Task UpdateFile_InvertedTerm_ThrowsOnValidTo_AndMutatesNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateFile(contract.ContractId, fileId, new UpdateContractFileRequest
            {
                FileType = ContractFileType.Amendment,
                ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
        Assert.Equal(
            ContractFileType.Signed,
            Assert.Single((await service.GetFiles(contract.ContractId))!).FileType);
    }

    [Fact]
    public async Task UpdateFile_UnknownIssuer_ThrowsOnIssuedBy_AndMutatesNothing()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateFile(contract.ContractId, fileId, new UpdateContractFileRequest
            {
                FileType = ContractFileType.Amendment,
                IssuedBy = Guid.NewGuid(),
            }));

        Assert.True(error.Errors!.ContainsKey(nameof(UpdateContractFileRequest.IssuedBy)));
        Assert.Equal(
            ContractFileType.Signed,
            Assert.Single((await service.GetFiles(contract.ContractId))!).FileType);
    }

    [Fact]
    public async Task UpdateFile_DateOutsideTheStorableRange_ThrowsOnThatDate()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateFile(contract.ContractId, fileId, new UpdateContractFileRequest
            {
                FileType = ContractFileType.Amendment,
                ValidTo = new DateTime(202, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
    }

    [Fact]
    public async Task UpdateFile_LocalKindDate_IsStoredNormalisedToUtc()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        await service.AttachFile(contract.ContractId, Attach(fileId), TestUserId);
        var local = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Local);

        await service.UpdateFile(contract.ContractId, fileId, new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            IssuedAt = local,
        });

        var stored = await context.ContractFiles.SingleAsync();
        Assert.Equal(local.ToUniversalTime(), stored.IssuedAt);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>AC 7 at the service seam — an empty list and a missing contract stay distinguishable.</summary>
    [Fact]
    public async Task GetFiles_ContractWithNoDocuments_ReturnsEmpty_MissingContractReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);

        Assert.Empty((await service.GetFiles(contract.ContractId))!);
        Assert.Null(await service.GetFiles(Guid.NewGuid()));
    }

    /// <summary>AC 1 at the service seam — the list and the inlined contract collection agree.</summary>
    [Fact]
    public async Task GetFiles_AgreesFieldForFieldWithTheInlinedContractCollection()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contract = await service.Create(NewContractRequest(), userId: null);
        var fileId = await SeedFileAsync(context);
        var contactId = await SeedContactAsync();
        await service.AttachFile(contract.ContractId, new AttachContractFileRequest
        {
            FileMetadataId = fileId,
            FileType = ContractFileType.Signed,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            IssuedAt = new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc),
            IssuedBy = contactId,
        }, TestUserId);

        var listed = Assert.Single((await service.GetFiles(contract.ContractId))!);
        var inlined = Assert.Single((await service.Get(contract.ContractId))!.Files);

        Assert.Equal(inlined.ValidFrom, listed.ValidFrom);
        Assert.Equal(inlined.ValidTo, listed.ValidTo);
        Assert.Equal(inlined.IssuedAt, listed.IssuedAt);
        Assert.Equal(inlined.IssuedBy, listed.IssuedBy);
        Assert.Equal(inlined.FileType, listed.FileType);
        Assert.Equal(inlined.AttachedByUserId, listed.AttachedByUserId);
    }

    /// <summary>The list is scoped to its own contract.</summary>
    [Fact]
    public async Task GetFiles_ReturnsOnlyTheNamedContractsDocuments()
    {
        await using var context = TestContextFactory.Create();
        var service = CreateService(context);
        var contractA = await service.Create(NewContractRequest(), userId: null);
        var contractB = await service.Create(NewContractRequest(), userId: null);
        await service.AttachFile(contractA.ContractId, Attach(await SeedFileAsync(context)), TestUserId);
        await service.AttachFile(contractB.ContractId, Attach(await SeedFileAsync(context)), TestUserId);

        var listed = Assert.Single((await service.GetFiles(contractA.ContractId))!);
        Assert.Equal(contractA.ContractId, listed.ContractId);
    }
}
