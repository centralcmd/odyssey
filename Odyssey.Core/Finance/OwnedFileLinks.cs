using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Finance;

/// <summary>
/// What distinguishes one file-attachment surface from another: which link table, which owner, how a
/// row is created and projected. Everything else — the rules — lives once in
/// <see cref="OwnedFileLinks{TLink, TDto}"/>.
/// </summary>
public sealed class OwnedFileSurface<TLink, TDto>
    where TLink : class, IOwnedFileLink
{
    /// <summary>The owner as named in a message: <c>"account"</c>, <c>"tax statement"</c>, …</summary>
    public required string OwnerNoun { get; init; }

    public required Func<OdysseyContext, DbSet<TLink>> Links { get; init; }

    public required Func<OdysseyContext, Guid, CancellationToken, Task<bool>> OwnerExists { get; init; }

    /// <summary>The link's owner-id column, e.g. <c>f =&gt; f.AccountId</c>. Must be a plain member access.</summary>
    public required Expression<Func<TLink, Guid>> OwnerId { get; init; }

    /// <summary>Builds a new link row for <c>(ownerId, fileMetadataId)</c>; type and validity are applied after.</summary>
    public required Func<Guid, Guid, TLink> Create { get; init; }

    public required Func<TLink, TDto> ToDto { get; init; }

    /// <summary>
    /// A surface-specific gate evaluated after the duplicate check and before the insert — the
    /// per-contract file cap is the one user. Throws to refuse.
    /// </summary>
    public Func<OdysseyContext, Guid, CancellationToken, Task>? BeforeAttach { get; init; }
}

/// <summary>
/// The one implementation of a file-attachment surface's rules (issue #287 H3). Before it the account,
/// transaction, tax-statement, contract and property surfaces each carried their own copy, and the
/// copies disagreed: a duplicate attach was a silent no-op on two, a type upsert on one and a
/// <c>409</c> on two; detaching an unattached file was a <c>204</c> on two and a <c>404</c> on three;
/// two returned entities to the API layer; the issuer check was copied three times.
///
/// <para>The rules, now one set:</para>
/// <list type="bullet">
/// <item><b>Attach</b> returns <c>null</c> when the owner does not exist and the created link's DTO
/// otherwise. A file already attached to that owner is a <see cref="DomainConflictException"/>
/// (<c>409</c>); the unique <c>(owner, FileMetadataId)</c> index is the backstop for a concurrent
/// pair.</item>
/// <item><b>Update</b> and <b>detach</b> return <c>false</c> when the owner does not exist or the file is
/// not attached to it — one <c>404</c> at the edge, never a silent success.</item>
/// <item><b>List</b> returns <c>null</c> for a missing owner and an empty list for one with no files,
/// oldest attachment first, loading <see cref="FileMetadata"/> but never the blob.</item>
/// <item>A link is addressed by <c>(owner id, FileMetadataId)</c> — no link-row id travels.</item>
/// </list>
///
/// <para>
/// Which content types a surface accepts, and which claims it needs, stay at the controller: they
/// differ by surface on purpose.
/// </para>
/// </summary>
public sealed class OwnedFileLinks<TLink, TDto>
    where TLink : class, IOwnedFileLink
{
    private readonly OdysseyContext context;
    private readonly OwnedFileSurface<TLink, TDto> surface;
    private readonly IContactLookup? contactLookup;
    private readonly TimeProvider timeProvider;

    public OwnedFileLinks(
        OdysseyContext context,
        OwnedFileSurface<TLink, TDto> surface,
        IContactLookup? contactLookup,
        TimeProvider timeProvider)
    {
        this.context = context;
        this.surface = surface;
        this.contactLookup = contactLookup;
        this.timeProvider = timeProvider;
    }

    private DbSet<TLink> Links => surface.Links(context);

    public Task<bool> OwnerExists(Guid ownerId, CancellationToken cancellationToken = default) =>
        surface.OwnerExists(context, ownerId, cancellationToken);

    public Task<bool> IsAttached(Guid ownerId, Guid fileMetadataId, CancellationToken cancellationToken = default) =>
        Links.AnyAsync(Pair(ownerId, fileMetadataId), cancellationToken);

    /// <summary>
    /// Attaches an already-uploaded file. <paramref name="apply"/> sets the surface's own fields (the
    /// file type, the validity dates) on the new row and may throw to refuse; it runs before the
    /// duplicate check so an invalid request is a <c>400</c> whether or not the file is attached.
    /// </summary>
    public async Task<TDto?> Attach(
        Guid ownerId,
        Guid fileMetadataId,
        string userId,
        Func<TLink, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnerExists(ownerId, cancellationToken))
        {
            return default;
        }

        var link = surface.Create(ownerId, fileMetadataId);
        link.AttachedByUserId = userId;
        link.AttachedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await apply(link, cancellationToken);

        // The pre-check turns the ordinary duplicate into a message naming the pair; the unique index
        // is the backstop for two concurrent attaches, which GlobalExceptionHandler maps to a 409.
        if (await IsAttached(ownerId, fileMetadataId, cancellationToken))
        {
            throw new DomainConflictException(
                $"File {fileMetadataId} is already attached to {surface.OwnerNoun} {ownerId}.");
        }

        if (surface.BeforeAttach is { } gate)
        {
            await gate(context, ownerId, cancellationToken);
        }

        Links.Add(link);
        await context.SaveChangesAsync(cancellationToken);

        // Loaded onto the tracked row rather than re-queried with an Include, which would be an inner
        // join and lose the row on a provider that enforces no foreign key.
        await context.Entry(link).Reference(nameof(IOwnedFileLink.FileMetadata)).LoadAsync(cancellationToken);
        return surface.ToDto(link);
    }

    /// <summary>
    /// Applies <paramref name="apply"/> to an attached link and saves. <c>false</c> when the owner does
    /// not exist or the file is not attached to it.
    /// </summary>
    public async Task<bool> Update(
        Guid ownerId,
        Guid fileMetadataId,
        Func<TLink, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default)
    {
        var link = await Links.FirstOrDefaultAsync(Pair(ownerId, fileMetadataId), cancellationToken);
        if (link is null)
        {
            return false;
        }

        await apply(link, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Removes the link row only; the file stays in the Files store.</summary>
    public async Task<bool> Detach(Guid ownerId, Guid fileMetadataId, CancellationToken cancellationToken = default)
    {
        var link = await Links.FirstOrDefaultAsync(Pair(ownerId, fileMetadataId), cancellationToken);
        if (link is null)
        {
            return false;
        }

        Links.Remove(link);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<List<TDto>?> List(Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (!await OwnerExists(ownerId, cancellationToken))
        {
            return null;
        }

        var links = await Links
            .AsNoTracking()
            .Include(nameof(IOwnedFileLink.FileMetadata))
            .Where(OwnedBy(ownerId))
            .Where(HasMetadata)
            .OrderBy(AttachedAt)
            .ThenBy(FileId)
            .ToListAsync(cancellationToken);

        return [.. links.Select(surface.ToDto)];
    }

    /// <summary>
    /// Normalises and stores a document's validity dates and issuer — the one rule
    /// <see cref="DocumentValidity"/> defines, plus the issuer existence check that used to be copied
    /// into three services.
    /// </summary>
    public async Task ApplyValidity(
        IDocumentValidityLink link,
        DateTime? validFrom,
        DateTime? validTo,
        DateTime? issuedAt,
        Guid? issuedBy,
        CancellationToken cancellationToken)
    {
        var (from, to, issued) = DocumentValidity.Normalize(validFrom, validTo, issuedAt);
        await EnsureIssuerExists(issuedBy, cancellationToken);

        link.ValidFrom = from;
        link.ValidTo = to;
        link.IssuedAt = issued;
        link.IssuedBy = issuedBy;
    }

    /// <summary>
    /// Asserts the issuing contact exists — the only thing done with the id, so no write path here can
    /// create, rename or otherwise mutate a <see cref="Contact"/> (issue #146 §4.3).
    /// </summary>
    private async Task EnsureIssuerExists(Guid? issuedBy, CancellationToken cancellationToken)
    {
        if (issuedBy is not { } id)
        {
            return;
        }

        var lookup = contactLookup
            ?? throw new InvalidOperationException(
                $"The {surface.OwnerNoun} file surface records an issuer but was built without a contact lookup.");

        if (!(await lookup.ExistingIdsAsync([id], cancellationToken)).Contains(id))
        {
            throw new DomainValidationException(
                $"Contact with ID {id} was not found.",
                code: null,
                field: nameof(IDocumentValidityLink.IssuedBy));
        }
    }

    // ── Query shapes, built from the surface's owner selector ───────────────────────────────────
    //
    // The ids travel in a closure-shaped holder rather than as Expression.Constant, so EF parameterises
    // them and caches one query plan per surface instead of one per id.

    private Expression<Func<TLink, bool>> OwnedBy(Guid ownerId)
    {
        var parameter = surface.OwnerId.Parameters[0];
        var body = Expression.Equal(surface.OwnerId.Body, Captured(ownerId));
        return Expression.Lambda<Func<TLink, bool>>(body, parameter);
    }

    private Expression<Func<TLink, bool>> Pair(Guid ownerId, Guid fileMetadataId)
    {
        var parameter = surface.OwnerId.Parameters[0];
        var body = Expression.AndAlso(
            Expression.Equal(surface.OwnerId.Body, Captured(ownerId)),
            Expression.Equal(
                Expression.Property(parameter, nameof(IOwnedFileLink.FileMetadataId)),
                Captured(fileMetadataId)));
        return Expression.Lambda<Func<TLink, bool>>(body, parameter);
    }

    private static readonly Expression<Func<TLink, bool>> HasMetadata = BuildHasMetadata();

    private static readonly Expression<Func<TLink, DateTime>> AttachedAt =
        BuildMember<DateTime>(nameof(IOwnedFileLink.AttachedAtUtc));

    // The tiebreaker: two files attached in one clock tick would otherwise swap order between reads.
    private static readonly Expression<Func<TLink, Guid>> FileId =
        BuildMember<Guid>(nameof(IOwnedFileLink.FileMetadataId));

    private static Expression<Func<TLink, bool>> BuildHasMetadata()
    {
        var parameter = Expression.Parameter(typeof(TLink), "link");
        var metadata = Expression.Property(parameter, nameof(IOwnedFileLink.FileMetadata));
        var body = Expression.NotEqual(metadata, Expression.Constant(null, metadata.Type));
        return Expression.Lambda<Func<TLink, bool>>(body, parameter);
    }

    private static Expression<Func<TLink, TValue>> BuildMember<TValue>(string member)
    {
        var parameter = Expression.Parameter(typeof(TLink), "link");
        return Expression.Lambda<Func<TLink, TValue>>(Expression.Property(parameter, member), parameter);
    }

    private static MemberExpression Captured(Guid value) =>
        Expression.Field(Expression.Constant(new GuidBox(value)), nameof(GuidBox.Value));

    private sealed class GuidBox(Guid value)
    {
        public readonly Guid Value = value;
    }
}
