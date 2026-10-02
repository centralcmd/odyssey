using Odyssey.Core;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using ContextContractType = Odyssey.Context.ContractType;
using ContextContractFileType = Odyssey.Context.ContractFileType;
using DtoAccountType = Odyssey.Dtos.Finance.AccountType;
using DtoContractType = Odyssey.Dtos.Finance.ContractType;
using DtoContractFileType = Odyssey.Dtos.Finance.ContractFileType;
using Odyssey.Dtos.Finance;
using Odyssey.Core.Pagination;
using Odyssey.Dtos;
using Microsoft.Extensions.Logging;
using ContextContractPartyRole = Odyssey.Context.ContractPartyRole;
using ContextInterval = Odyssey.Context.Interval;
using ContextTermValueUnit = Odyssey.Context.TermValueUnit;
using ContextTermDirection = Odyssey.Context.TermDirection;
using DtoInterval = Odyssey.Dtos.Finance.Interval;
using DtoContractPartyRole = Odyssey.Dtos.Finance.ContractPartyRole;

namespace Odyssey.Core.Finance;

/// <summary>
/// A contract's attached documents (issue #146) on the shared file-link rules (issue #287 H3). Split out
/// of <see cref="ContractService"/> (issue #287 M1), along the <c>PropertyFileService</c> precedent.
/// </summary>
public class ContractFileService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;
    private readonly ISystemSettingsLookup systemSettingsLookup;

    public ContractFileService(
        OdysseyContext context,
        IContactLookup contactLookup,
        TimeProvider timeProvider,
        ISystemSettingsLookup systemSettingsLookup)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
        this.systemSettingsLookup = systemSettingsLookup;
    }

    /// <summary>
    /// The contract-document surface (issue #146) on the shared file-link rules (issue #287 H3), plus
    /// the one thing only contracts have: the per-contract file cap, evaluated after the duplicate check
    /// so a duplicate at the cap still reads as a duplicate.
    /// </summary>
    private OwnedFileLinks<ContractFile, ExistingContractFile> FileLinks => fileLinks ??= new(
        context,
        new OwnedFileSurface<ContractFile, ExistingContractFile>
        {
            OwnerNoun = "contract",
            Links = c => c.ContractFiles,
            OwnerExists = (c, id, ct) => c.Contracts.AnyAsync(contract => contract.ContractId == id, ct),
            OwnerId = f => f.ContractId,
            Create = (contractId, fileId) => new ContractFile
            {
                ContractId = contractId,
                FileMetadataId = fileId,
                AttachedAtUtc = default,
            },
            ToDto = ContractProjection.ToFileDto,
            BeforeAttach = async (c, contractId, ct) =>
            {
                var caps = await systemSettingsLookup.GetRequestCapsAsync(ct);
                var count = await c.ContractFiles.CountAsync(f => f.ContractId == contractId, ct);
                if (count >= caps.MaxFilesPerContract)
                {
                    throw new DomainUnprocessableException(
                        $"Contract {contractId} already has the maximum of {caps.MaxFilesPerContract} attached files.");
                }
            },
        },
        contactLookup,
        timeProvider);

    private OwnedFileLinks<ContractFile, ExistingContractFile>? fileLinks;

    /// <summary>
    /// Attaches an already-uploaded file to the contract, recording the optional validity metadata
    /// the request carries (issue #146). <c>null</c> when the contract does not exist.
    /// </summary>
    public Task<ExistingContractFile?> AttachFile(
        Guid contractId, AttachContractFileRequest request, string userId, CancellationToken cancellationToken = default) =>
        FileLinks.Attach(contractId, request.FileMetadataId, userId, async (link, ct) =>
        {
            link.FileType = request.FileType.Adapt<ContextContractFileType>();
            await FileLinks.ApplyValidity(link, request.ValidFrom, request.ValidTo, request.IssuedAt, request.IssuedBy, ct);
        }, cancellationToken);

    /// <summary>
    /// Replaces an attached document's type and validity metadata (issue #146 §5.2), addressed by
    /// <c>(ContractId, FileMetadataId)</c>. <c>false</c> when the contract does not exist or the file is
    /// not attached to it, which the controller turns into a <c>404</c>.
    /// </summary>
    /// <remarks>
    /// The per-contract file cap is deliberately <b>not</b> evaluated: this creates no row, so a
    /// contract already at its cap can still have a document's dates corrected.
    /// </remarks>
    public Task<bool> UpdateFile(
        Guid contractId, Guid fileMetadataId, UpdateContractFileRequest request, CancellationToken cancellationToken = default) =>
        FileLinks.Update(contractId, fileMetadataId, async (link, ct) =>
        {
            await FileLinks.ApplyValidity(link, request.ValidFrom, request.ValidTo, request.IssuedAt, request.IssuedBy, ct);
            link.FileType = request.FileType.Adapt<ContextContractFileType>();
        }, cancellationToken);

    /// <summary>
    /// The documents attached to one contract (issue #146 §5.3), or <c>null</c> when the contract does
    /// not exist. Unpaged and bounded by <c>MaxFilesPerContract</c>.
    /// </summary>
    public Task<List<ExistingContractFile>?> GetFiles(Guid contractId, CancellationToken cancellationToken = default) =>
        FileLinks.List(contractId, cancellationToken);

    public Task<bool> IsFileAttachedToContract(Guid contractId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        FileLinks.IsAttached(contractId, fileMetadataId, cancellationToken);

    public Task<bool> DetachFile(Guid contractId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        FileLinks.Detach(contractId, fileMetadataId, cancellationToken);
}
