using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Avatar;

/// <summary>
/// What the read path needs to answer a conditional request, resolved from <see cref="FileMetadata"/>
/// <b>alone</b> — no <c>Include(fm =&gt; fm.FileBlob)</c>.
/// </summary>
/// <remarks>
/// <c>Cache-Control: private, no-cache</c> makes revalidation the hot path, so loading the
/// <c>LONGBLOB</c> to answer a conditional request would make every return visit pay full
/// materialisation for a response that carries no body.
/// </remarks>
/// <param name="FileId">The avatar's file id — also the URL key that re-keys on a replace.</param>
/// <param name="ContentType">The <b>stored</b> content type, which the read path re-checks against the allow-list.</param>
/// <param name="Sha256Hash">The strong <c>ETag</c> value.</param>
public sealed record ContactAvatarDescriptor(Guid FileId, string ContentType, string Sha256Hash);

/// <summary>
/// Attach / replace / remove a contact's one image, the transactional file lifecycle behind it, and the
/// read projection (issue #86 §5.1).
///
/// <para>
/// The endpoint takes <b>file bytes, never a <c>fileId</c></b>. That is the load-bearing control here: a
/// caller-supplied id would let a <c>contacts.update</c> holder point a contact at any row in the Files
/// store and read its bytes back through a <c>contacts.read</c>-gated endpoint — an arbitrary-file-read
/// primitive assembled from two innocuous claims. "Pick an existing file" is a non-goal for that
/// reason, not for want of convenience.
/// </para>
/// </summary>
public class ContactAvatarService
{
    /// <summary>A fixed, non-PII marker. Never the contact's name, and never the user's filename.</summary>
    private const string AvatarDescription = "Contact image";

    private readonly OdysseyContext context;
    private readonly FileService fileService;
    private readonly IUploadLimitsLookup uploadLimits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ContactAvatarService>? logger;

    public ContactAvatarService(
        OdysseyContext context,
        FileService fileService,
        IUploadLimitsLookup uploadLimits,
        TimeProvider? timeProvider = null,
        ILogger<ContactAvatarService>? logger = null)
    {
        this.context = context;
        this.fileService = fileService;
        this.uploadLimits = uploadLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
    }

    /// <summary>
    /// <c>min(global upload cap, <see cref="ContactAvatarLimits.MaxAvatarBytes"/>)</c>.
    /// <c>min</c> is the only correct direction: a surface may be stricter than the instance, but it
    /// must never override a cap an administrator has lowered.
    /// </summary>
    public async Task<long> GetEffectiveMaxBytesAsync(CancellationToken cancellationToken = default)
    {
        var limits = await uploadLimits.GetAsync(cancellationToken);
        return Math.Min(limits.MaxUploadBytes, ContactAvatarLimits.MaxAvatarBytes);
    }

    // ── Read ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves what the read path needs, or <c>null</c> when the contact does not exist, has no image,
    /// <b>or the stored content type is not avatar-legal</b>.
    /// </summary>
    /// <remarks>
    /// That last clause is not redundant with the unique index. The index prevents two contacts
    /// <i>sharing</i> a file; it does not prevent a row pointing somewhere it should not. Without this
    /// check a mis-pointed <c>AvatarFileId</c> would stream a tax statement to any <c>contacts.read</c>
    /// holder — a Guest included.
    /// </remarks>
    public async Task<ContactAvatarDescriptor?> GetDescriptorAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var descriptor = await context.Contacts
            .AsNoTracking()
            .Where(c => c.ContactId == contactId && c.AvatarFileId != null)
            .Join(context.FileMetadata.AsNoTracking(),
                c => c.AvatarFileId,
                fm => (Guid?)fm.Id,
                (_, fm) => new ContactAvatarDescriptor(fm.Id, fm.ContentType, fm.Sha256Hash))
            .FirstOrDefaultAsync(cancellationToken);

        if (descriptor is null)
        {
            return null;
        }

        if (!ContactAvatarLimits.IsAllowedContentType(descriptor.ContentType))
        {
            LogReferenceMismatch(contactId, descriptor.ContentType);
            return null;
        }

        return descriptor;
    }

    /// <summary>The bytes, loaded only once a conditional request has failed to match.</summary>
    public async Task<byte[]?> GetContentAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        await context.FileMetadata
            .AsNoTracking()
            .Where(fm => fm.Id == fileId)
            .Select(fm => fm.FileBlob!.Content)
            .FirstOrDefaultAsync(cancellationToken);

    // ── Write ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches or replaces the contact's image, in one transaction. Returns <c>false</c> when the
    /// contact does not exist.
    /// </summary>
    /// <remarks>
    /// Validation runs <b>outside</b> the transaction (it touches no rows) and the whole write — stage
    /// the new file, release the previous one, repoint, bump <c>UpdatedAt</c> — is one
    /// <c>SaveChangesAsync</c> inside it.
    /// </remarks>
    public async Task<bool> AttachAsync(
        Guid contactId,
        byte[] bytes,
        string? declaredContentType,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        // Existence FIRST, then validation. Validation is the CPU-bound half — a full container walk
        // over attacker-supplied bytes, twice — and running it before knowing the target exists lets a
        // caller spend that work on an id that resolves to nothing. Cheap to order correctly, and the
        // 404 it produces is unchanged either way.
        var contact = await context.Contacts.FirstOrDefaultAsync(c => c.ContactId == contactId, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        var effectiveMaxBytes = await GetEffectiveMaxBytesAsync(cancellationToken);
        var validated = ContactAvatarValidator.Validate(bytes, declaredContentType, effectiveMaxBytes);

        await ExecuteAtomicallyAsync(async () =>
        {
            await StageReleaseAsync(contact, "replace", cancellationToken);
            await StageAttachAsync(contact, validated, userId, cancellationToken);
            await SaveDetectingAvatarConflictAsync(cancellationToken);
        });

        // The VALIDATED content type, never the client-declared string — that one is attacker-controlled
        // and a log-injection vector. No filename, no hash, no bytes.
        logger?.LogInformation(
            "Contact {ContactId} image set to file {FileId} ({ContentType}, {SizeBytes} bytes).",
            contactId, contact.AvatarFileId, validated.ContentType, validated.Bytes.LongLength);

        return true;
    }

    /// <summary>
    /// Removes the contact's image, applying the release rule. Returns <c>false</c> when the contact
    /// does not exist or has no image.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await context.Contacts.FirstOrDefaultAsync(c => c.ContactId == contactId, cancellationToken);
        if (contact?.AvatarFileId is null)
        {
            return false;
        }

        await ExecuteAtomicallyAsync(async () =>
        {
            await StageReleaseAsync(contact, "delete", cancellationToken);
            contact.AvatarFileId = null;
            contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(cancellationToken);
        });

        logger?.LogInformation("Contact {ContactId} image removed.", contactId);
        return true;
    }

    /// <summary>
    /// Applies the shared release rule to this contact's outgoing file. The rule itself lives in
    /// <see cref="ContactAvatarRelease"/> so the contact-delete cascade calls the same code.
    /// </summary>
    private Task<AvatarReleaseOutcome> StageReleaseAsync(Contact contact, string site, CancellationToken cancellationToken) =>
        ContactAvatarRelease.StageAsync(context, contact, logger, site, cancellationToken);

    /// <summary>
    /// Stages the new file and repoints the contact at it — the second half of an attach, factored out
    /// so the vCard import path can run the identical write inside its own transaction.
    /// </summary>
    public Task StageAttachAsync(
        Contact contact,
        ValidatedAvatar validated,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        // The extension follows the VALIDATED content type rather than being hardcoded, and the name
        // carries no personal data: not the user's original filename (which can itself carry some) and
        // not the contact's name.
        var fileName = $"contact-avatar-{contact.ContactId.ToString("N")[..8]}{ContactAvatarLimits.ExtensionFor(validated.ContentType)}";

        var metadata = fileService.StageUpload(
            validated.Bytes, fileName, validated.ContentType, userId, AvatarDescription);

        contact.AvatarFileId = metadata.Id;
        contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        _ = cancellationToken;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates bytes for the vCard import path, which decodes base64 rather than receiving a
    /// multipart part but must run the <b>identical</b> pipeline.
    /// </summary>
    public ValidatedAvatar ValidateForImport(byte[] bytes, string? declaredContentType, long effectiveMaxBytes) =>
        ContactAvatarValidator.Validate(bytes, declaredContentType, effectiveMaxBytes);

    /// <summary>
    /// A <c>SaveChangesAsync</c> that turns the avatar unique index's violation into a
    /// <see cref="DomainConflictException"/> — a <c>409</c> — rather than letting a raw FK/index error
    /// surface as a <c>500</c>. The race it catches is a concurrent double-POST for the same contact.
    /// </summary>
    private async Task SaveDetectingAvatarConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The OTHER shape the same race takes, and the one a unique-index check alone misses: two
            // requests replacing the same contact's image both stage the removal of the outgoing file,
            // and the loser finds that row already gone. Same situation for the caller, so the same 409
            // rather than a 500 — DbUpdateConcurrencyException derives from DbUpdateException, so the
            // filtered catch below would not have covered it.
            throw new DomainConflictException(
                "This contact's image was changed by another request. Reload the contact and try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new DomainConflictException(
                "This contact's image was changed by another request. Reload the contact and try again.");
        }
    }

    /// <summary>
    /// Provider-agnostic detection: MySqlConnector reports 1062 (<c>ER_DUP_ENTRY</c>), and the message
    /// check keeps the mapping working where the inner exception is not the typed one.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains("1062", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One transaction, wrapped in the context's execution strategy because <c>EnableRetryOnFailure</c>
    /// is configured and a retrying strategy refuses an ambient transaction it did not open itself — a
    /// bare <c>BeginTransactionAsync</c> throws. Mirrors <c>ContactService.ExecuteAtomicallyAsync</c>.
    /// </summary>
    private async Task ExecuteAtomicallyAsync(Func<Task> work)
    {
        if (!context.Database.IsRelational())
        {
            // The InMemory provider honours neither transactions nor the execution strategy, and
            // BeginTransactionAsync would warn. The real coverage lives in Odyssey.IntegrationTests.
            await work();
            return;
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await work();
            await transaction.CommitAsync();
        });
    }

    /// <summary>
    /// The read path's counterpart to the release rule's warning: a mis-pointed reference is a 404 here
    /// and an operator's problem in the log. The contact id and the offending content type, nothing
    /// else — no filename, no hash, no size, and no bytes.
    /// </summary>
    private void LogReferenceMismatch(Guid contactId, string contentType) =>
        logger?.LogWarning(
            "Contact {ContactId} points at a file whose content type '{ContentType}' is not a permitted "
            + "contact image; its image reads as absent.",
            contactId, contentType);
}
