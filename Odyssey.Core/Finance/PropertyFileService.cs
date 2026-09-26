using Mapster;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using ContextPropertyFileType = Odyssey.Context.PropertyFileType;
using DtoPropertyFileType = Odyssey.Dtos.Finance.PropertyFileType;

namespace Odyssey.Core.Finance;

/// <summary>
/// Business logic for a property's documents (issue #210): attach an already-uploaded file, list,
/// update the type and validity metadata, and detach. A sibling of <see cref="PropertyEstimateService"/>
/// and <see cref="PropertySmartTagService"/> — the property aggregate splits each sub-resource into its
/// own service — and behaviourally the contract-document surface in <see cref="ContractService"/>,
/// minus the per-owner file cap (issue #210 Non-Goal 7, §7.12: deliberately uncapped).
///
/// <para>
/// Every relationship is written from a scalar id — the property from the route, the file and the
/// issuer from the body — so no write here can create or mutate a <see cref="Property"/>,
/// <see cref="FileMetadata"/> or <see cref="Contact"/>.
/// </para>
/// </summary>
public class PropertyFileService
{
    private readonly OdysseyContext context;
    private readonly IContactLookup contactLookup;
    private readonly TimeProvider timeProvider;

    public PropertyFileService(
        OdysseyContext context, IContactLookup contactLookup, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<bool> PropertyExists(Guid propertyId, CancellationToken cancellationToken = default) =>
        context.Properties.AnyAsync(p => p.PropertyId == propertyId, cancellationToken);

    /// <summary>
    /// Attaches an already-uploaded file to the property, or returns <c>null</c> when the property does
    /// not exist. The content-type allow-list is the controller's, checked before this runs.
    /// </summary>
    /// <exception cref="DomainValidationException">A date is out of range or inverted, or
    /// <c>IssuedBy</c> names no contact.</exception>
    /// <exception cref="DomainConflictException">The file is already attached to this property.</exception>
    public async Task<ExistingPropertyFile?> AttachFile(
        Guid propertyId, AttachPropertyFileRequest request, string userId, CancellationToken cancellationToken = default)
    {
        if (!await PropertyExists(propertyId, cancellationToken))
        {
            return null;
        }

        var (validFrom, validTo, issuedAt) = DocumentValidity.Normalize(
            request.ValidFrom, request.ValidTo, request.IssuedAt);
        await EnsureIssuerExists(request.IssuedBy, cancellationToken);

        // The pre-check turns the ordinary duplicate into a message naming the pair; the unique index
        // is the backstop for two concurrent attaches, which GlobalExceptionHandler maps to a 409.
        if (await IsFileAttached(propertyId, request.FileMetadataId, cancellationToken))
        {
            throw new DomainConflictException(
                $"File {request.FileMetadataId} is already attached to property {propertyId}.");
        }

        var link = new PropertyFile
        {
            PropertyId = propertyId,
            FileMetadataId = request.FileMetadataId,
            FileType = request.FileType.Adapt<ContextPropertyFileType>(),
            AttachedByUserId = userId,
            AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IssuedAt = issuedAt,
            IssuedBy = request.IssuedBy,
        };

        context.PropertyFiles.Add(link);
        await context.SaveChangesAsync(cancellationToken);

        var loaded = await context.PropertyFiles
            .AsNoTracking()
            .Include(f => f.FileMetadata)
            .FirstAsync(f => f.PropertyFileId == link.PropertyFileId, cancellationToken);
        return ToDto(loaded);
    }

    /// <summary>
    /// Replaces an attached document's type and validity metadata — a full replacement, so a
    /// <c>null</c> clears. Addressed by <c>(PropertyId, FileMetadataId)</c>, the unique index. Returns
    /// <c>false</c> when the property does not exist or the file is not attached to it; the two are one
    /// <c>404</c> at the edge.
    /// </summary>
    public async Task<bool> UpdateFile(
        Guid propertyId, Guid fileMetadataId, UpdatePropertyFileRequest request, CancellationToken cancellationToken = default)
    {
        var link = await context.PropertyFiles
            .FirstOrDefaultAsync(f => f.PropertyId == propertyId && f.FileMetadataId == fileMetadataId, cancellationToken);
        if (link is null)
        {
            return false;
        }

        var (validFrom, validTo, issuedAt) = DocumentValidity.Normalize(
            request.ValidFrom, request.ValidTo, request.IssuedAt);
        await EnsureIssuerExists(request.IssuedBy, cancellationToken);

        link.FileType = request.FileType.Adapt<ContextPropertyFileType>();
        link.ValidFrom = validFrom;
        link.ValidTo = validTo;
        link.IssuedAt = issuedAt;
        link.IssuedBy = request.IssuedBy;

        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The documents attached to one property, oldest attachment first, or <c>null</c> when the property
    /// does not exist — an empty list is the answer for a property with no documents. Unpaged and
    /// uncapped (issue #210 §7.12). Loads <see cref="FileMetadata"/> only, never the blob.
    /// </summary>
    public async Task<List<ExistingPropertyFile>?> GetFiles(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!await PropertyExists(propertyId, cancellationToken))
        {
            return null;
        }

        var files = await context.PropertyFiles
            .AsNoTracking()
            .Include(f => f.FileMetadata)
            .Where(f => f.PropertyId == propertyId && f.FileMetadata != null)
            .OrderBy(f => f.AttachedAtUtc)
            .ToListAsync(cancellationToken);

        return [.. files.Select(ToDto)];
    }

    public Task<bool> IsFileAttached(Guid propertyId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        context.PropertyFiles.AnyAsync(
            f => f.PropertyId == propertyId && f.FileMetadataId == fileMetadataId, cancellationToken);

    /// <summary>Removes the link row only; the file stays in the Files store.</summary>
    public async Task<bool> DetachFile(Guid propertyId, Guid fileMetadataId, CancellationToken cancellationToken = default)
    {
        var link = await context.PropertyFiles
            .FirstOrDefaultAsync(f => f.PropertyId == propertyId && f.FileMetadataId == fileMetadataId, cancellationToken);
        if (link is null)
        {
            return false;
        }

        context.PropertyFiles.Remove(link);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Asserts the issuing contact exists — the only thing done with the id.</summary>
    private async Task EnsureIssuerExists(Guid? issuedBy, CancellationToken cancellationToken)
    {
        if (issuedBy is not { } id)
        {
            return;
        }

        if (!(await contactLookup.ExistingIdsAsync([id], cancellationToken)).Contains(id))
        {
            throw new DomainValidationException(
                $"Contact with ID {id} was not found.",
                code: null,
                field: nameof(UpdatePropertyFileRequest.IssuedBy));
        }
    }

    private static ExistingPropertyFile ToDto(PropertyFile file) => new()
    {
        PropertyFileId = file.PropertyFileId,
        PropertyId = file.PropertyId,
        FileMetadata = file.FileMetadata!.Adapt<ExistingFileMetadata>(),
        FileType = file.FileType.Adapt<DtoPropertyFileType>(),
        AttachedByUserId = file.AttachedByUserId,
        AttachedAtUtc = file.AttachedAtUtc,
        ValidFrom = file.ValidFrom,
        ValidTo = file.ValidTo,
        IssuedAt = file.IssuedAt,
        IssuedBy = file.IssuedBy,
    };
}
