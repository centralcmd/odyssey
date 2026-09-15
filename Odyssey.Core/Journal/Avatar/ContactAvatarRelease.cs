using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Avatar;

/// <summary>What a release site did with the outgoing file (issue #86 §5, "Releasing an avatar file").</summary>
public enum AvatarReleaseOutcome
{
    /// <summary>There was no avatar to release.</summary>
    NoAvatar,

    /// <summary>The file was avatar-legal: both rows were staged for removal.</summary>
    Deleted,

    /// <summary>
    /// The outgoing file was <b>not</b> avatar-legal, so only the reference was released and the file
    /// was left intact. Not a user-facing failure: the operation succeeds and the anomaly goes to the
    /// log for an operator (<c>AvatarReferenceMismatch</c>, §11).
    /// </summary>
    DetachedOnly,
}

/// <summary>
/// <b>The release rule, expressed once</b> (issue #86 §5). <b>Three</b> code paths release a contact's
/// avatar file, and every one of them calls this, so a fourth site cannot be added without inheriting it:
/// <list type="number">
///   <item><c>DELETE /api/contacts/{id}/avatar</c></item>
///   <item>the contact-delete cascade</item>
///   <item><b>the replace half of <c>POST /api/contacts/{id}/avatar</c></b></item>
/// </list>
///
/// <para>
/// An earlier draft guarded sites 1 and 2 and missed site 3, so a contact mis-pointed at a PDF would
/// have had that PDF <b>destroyed by the next upload</b> — the most likely of the three to be reached,
/// since replacing an image is an ordinary action while deleting one is not.
/// </para>
///
/// <para>
/// It is a static over the context rather than a service method so the contact-delete cascade in
/// <c>ContactService</c> can call the same code without taking a dependency on the avatar service for
/// the sake of one call.
/// </para>
/// </summary>
public static class ContactAvatarRelease
{
    /// <summary>
    /// Stages the outgoing file's removal onto <paramref name="context"/> — <b>without saving</b>, so the
    /// release commits with whatever the caller is doing to the reference itself (nulling it, repointing
    /// it, or deleting the whole contact row).
    ///
    /// <para>
    /// Tracked <c>Remove</c>, never <c>ExecuteDeleteAsync</c>: the latter lives in
    /// <c>EntityFrameworkCore.Relational</c> and throws on the EF InMemory provider, which is the only
    /// provider two of the three sites are exercised on in the fast test tiers.
    /// </para>
    /// </summary>
    /// <param name="site">Which of the three release paths is calling, for the warning log only.</param>
    public static async Task<AvatarReleaseOutcome> StageAsync(
        OdysseyContext context,
        Contact contact,
        ILogger? logger,
        string site,
        CancellationToken cancellationToken = default)
    {
        if (contact.AvatarFileId is not { } fileId)
        {
            return AvatarReleaseOutcome.NoAvatar;
        }

        var metadata = await context.FileMetadata
            .Include(fm => fm.FileBlob)
            .FirstOrDefaultAsync(fm => fm.Id == fileId, cancellationToken);

        if (metadata is null)
        {
            // The reference outlived its file. Nothing to release; the caller still clears the reference.
            return AvatarReleaseOutcome.NoAvatar;
        }

        if (!ContactAvatarLimits.IsAllowedContentType(metadata.ContentType))
        {
            // Detach-never-delete, chosen over refuse-and-404 because it keeps the row recoverable: a
            // mis-pointed reference is a defect to investigate, not a licence to destroy what it points
            // at. The contact id and the offending content type, nothing else — no filename, no hash, no
            // size, and no bytes.
            logger?.LogWarning(
                "Contact {ContactId} points at a file whose content type '{ContentType}' is not a "
                + "permitted contact image; the reference was released without deleting the file "
                + "({Site} path).",
                contact.ContactId, metadata.ContentType, site);
            return AvatarReleaseOutcome.DetachedOnly;
        }

        if (metadata.FileBlob is not null)
        {
            context.FileBlob.Remove(metadata.FileBlob);
        }

        context.FileMetadata.Remove(metadata);
        return AvatarReleaseOutcome.Deleted;
    }
}
