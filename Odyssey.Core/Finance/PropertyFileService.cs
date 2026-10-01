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
/// The rules (duplicate <c>409</c>, missing-link <c>404</c>, the issuer check) are
/// <see cref="OwnedFileLinks{TLink, TDto}"/>'s, shared with the other four file surfaces (issue #287 H3).
/// Every relationship is written from a scalar id — the property from the route, the file and the
/// issuer from the body — so no write here can create or mutate a <see cref="Property"/>,
/// <see cref="FileMetadata"/> or <see cref="Contact"/>.
/// </para>
/// </summary>
public class PropertyFileService
{
    private static readonly OwnedFileSurface<PropertyFile, ExistingPropertyFile> Surface = new()
    {
        OwnerNoun = "property",
        Links = context => context.PropertyFiles,
        OwnerExists = (context, id, ct) => context.Properties.AnyAsync(p => p.PropertyId == id, ct),
        OwnerId = f => f.PropertyId,
        Create = (propertyId, fileId) => new PropertyFile
        {
            PropertyId = propertyId,
            FileMetadataId = fileId,
            AttachedAtUtc = default,
        },
        ToDto = ToDto,
    };

    private readonly OwnedFileLinks<PropertyFile, ExistingPropertyFile> links;

    public PropertyFileService(
        OdysseyContext context, IContactLookup contactLookup, TimeProvider? timeProvider = null)
    {
        links = new(context, Surface, contactLookup, timeProvider ?? TimeProvider.System);
    }

    public Task<bool> PropertyExists(Guid propertyId, CancellationToken cancellationToken = default) =>
        links.OwnerExists(propertyId, cancellationToken);

    /// <summary>
    /// Attaches an already-uploaded file to the property, or returns <c>null</c> when the property does
    /// not exist. The content-type allow-list is the controller's, checked before this runs.
    /// </summary>
    /// <exception cref="DomainValidationException">A date is out of range or inverted, or
    /// <c>IssuedBy</c> names no contact.</exception>
    /// <exception cref="DomainConflictException">The file is already attached to this property.</exception>
    public Task<ExistingPropertyFile?> AttachFile(
        Guid propertyId, AttachPropertyFileRequest request, string userId, CancellationToken cancellationToken = default) =>
        links.Attach(propertyId, request.FileMetadataId, userId, async (link, ct) =>
        {
            link.FileType = request.FileType.Adapt<ContextPropertyFileType>();
            await links.ApplyValidity(link, request.ValidFrom, request.ValidTo, request.IssuedAt, request.IssuedBy, ct);
        }, cancellationToken);

    /// <summary>
    /// Replaces an attached document's type and validity metadata — a full replacement, so a
    /// <c>null</c> clears. Returns <c>false</c> when the property does not exist or the file is not
    /// attached to it; the two are one <c>404</c> at the edge.
    /// </summary>
    public Task<bool> UpdateFile(
        Guid propertyId, Guid fileMetadataId, UpdatePropertyFileRequest request, CancellationToken cancellationToken = default) =>
        links.Update(propertyId, fileMetadataId, async (link, ct) =>
        {
            await links.ApplyValidity(link, request.ValidFrom, request.ValidTo, request.IssuedAt, request.IssuedBy, ct);
            link.FileType = request.FileType.Adapt<ContextPropertyFileType>();
        }, cancellationToken);

    /// <summary>
    /// The documents attached to one property, oldest attachment first, or <c>null</c> when the property
    /// does not exist. Unpaged and uncapped (issue #210 §7.12).
    /// </summary>
    public Task<List<ExistingPropertyFile>?> GetFiles(Guid propertyId, CancellationToken cancellationToken = default) =>
        links.List(propertyId, cancellationToken);

    public Task<bool> IsFileAttached(Guid propertyId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        links.IsAttached(propertyId, fileMetadataId, cancellationToken);

    /// <summary>Removes the link row only; the file stays in the Files store.</summary>
    public Task<bool> DetachFile(Guid propertyId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        links.Detach(propertyId, fileMetadataId, cancellationToken);

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
