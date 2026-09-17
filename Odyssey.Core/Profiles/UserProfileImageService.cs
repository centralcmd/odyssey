using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Profiles;

/// <summary>
/// What the read path needs to answer a conditional request, resolved from
/// <see cref="UserProfileImage"/> <b>alone</b> — never touching the blob.
/// </summary>
/// <remarks>
/// <c>Cache-Control: private, no-cache</c> makes revalidation the hot path, so loading the
/// <c>LONGBLOB</c> to answer a conditional request would make every return visit pay full
/// materialisation for a response that carries no body.
/// </remarks>
/// <param name="ImageId">The row's own id — the key the bytes are then loaded by.</param>
/// <param name="ContentType">The <b>stored</b> content type, which the read path re-checks against the allow-list.</param>
/// <param name="Sha256Hash">The strong <c>ETag</c> value.</param>
public sealed record UserProfileImageDescriptor(Guid ImageId, string ContentType, string Sha256Hash);

/// <summary>The outcome of a write, so the controller can answer without a second query.</summary>
public enum ProfileImageRemoval
{
    /// <summary>The picture and its bytes are gone.</summary>
    Removed,

    /// <summary>There was no picture to remove — a <c>404</c>.</summary>
    NotFound,
}

/// <summary>
/// Attach / replace / remove the <b>caller's own</b> profile picture, and the read projection
/// (issue #94 §5, §7, §9).
///
/// <para>
/// <b>It lives in <c>Odyssey.Core</c>, not <c>Odyssey.Api</c></b>, so <c>Odyssey.Core.Tests</c> can
/// drive the pipeline with no HTTP plumbing at all — the API project keeps only the controller. Every
/// write method takes the caller's own user id; only the read takes a target id, by design.
/// </para>
///
/// <para>
/// <b>The write endpoints take no user id in route or body at all</b> (issue #94 §10.1). Unlike the
/// finance domain — a shared workspace with no per-row owner — a profile picture is per-user owned
/// data and a genuine IDOR surface, so the mitigation is structural rather than a check: there is no
/// id to tamper with and no authorization comparison to forget.
/// </para>
/// </summary>
public class UserProfileImageService
{
    private readonly OdysseyContext context;
    private readonly IUploadLimitsLookup uploadLimits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<UserProfileImageService>? logger;

    public UserProfileImageService(
        OdysseyContext context,
        IUploadLimitsLookup uploadLimits,
        TimeProvider? timeProvider = null,
        ILogger<UserProfileImageService>? logger = null)
    {
        this.context = context;
        this.uploadLimits = uploadLimits;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger;
    }

    /// <summary>
    /// <c>min(instance upload cap, <see cref="UserProfileImageLimits.MaxImageBytes"/>)</c>.
    /// <c>min</c> is the only correct direction: a surface may be stricter than the instance, but it
    /// must never override a cap an administrator has lowered.
    /// </summary>
    public async Task<long> GetEffectiveMaxBytesAsync(CancellationToken cancellationToken = default)
    {
        var limits = await uploadLimits.GetAsync(cancellationToken);
        return Math.Min(limits.MaxUploadBytes, UserProfileImageLimits.MaxImageBytes);
    }

    // ── Read ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves what the read path needs for <paramref name="userId"/>, or <c>null</c> when that user
    /// does not exist, has no picture, is <b>administratively disabled</b>, or the stored content type
    /// is not profile-picture-legal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The disabled test is the sentinel and only the sentinel.</b> Disabling is the remedy for an
    /// abusive upload — the security-stamp rotation revokes the <i>target's</i> sessions, while this
    /// endpoint keys off the <i>caller's</i> authentication and would otherwise keep serving the
    /// picture until the account was deleted outright. Keying it off sign-in eligibility instead would
    /// hide a picture for five minutes after five mistyped passwords and, worse, destroy the ambiguity
    /// the whole decision rests on: a deliberate removal never reverts, a transient lockout reverts in
    /// about five minutes, so <c>200 → 404 → 200</c> on a known id would confirm a failed-login lockout
    /// to any <c>profile-images.read</c> holder — a password-spray oracle outside the login endpoint
    /// and its rate limiter. See <c>AccountLockout.IsAdministrativelyDisabled</c>.
    /// </para>
    /// <para>
    /// The comparison is written <b>inline</b> against <see cref="AccountLockout.DisabledLockoutEnd"/>
    /// rather than through that helper, because this is a join onto <c>AspNetUsers</c> and EF Core
    /// translates no arbitrary static method call: the helper inside this <c>Where</c> would throw
    /// "The LINQ expression could not be translated" at runtime on the first request, having compiled
    /// clean and passed every criterion. The constant is still named rather than a literal.
    /// </para>
    /// <para>
    /// The content-type check is not redundant with the write path's validation: it makes a row that
    /// somehow held a disallowed type read as <i>absent</i> rather than streaming untrusted bytes
    /// inline (§10.7).
    /// </para>
    /// </remarks>
    public async Task<UserProfileImageDescriptor?> GetDescriptorAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var disabled = AccountLockout.DisabledLockoutEnd;

        // A two-column projection over a table holding nothing else, plus an indexed PK join onto the
        // identity table for one column — so an identity read can never widen into domain file
        // metadata, and the revalidation path stays cheap.
        var descriptor = await context.UserProfileImages
            .AsNoTracking()
            .Where(image => image.UserId == userId)
            .Join(context.Users.AsNoTracking(),
                image => image.UserId,
                user => user.Id,
                (image, user) => new { image.UserProfileImageId, image.ContentType, image.Sha256Hash, user.LockoutEnd })
            .Where(row => row.LockoutEnd != disabled)
            .Select(row => new UserProfileImageDescriptor(row.UserProfileImageId, row.ContentType, row.Sha256Hash))
            .FirstOrDefaultAsync(cancellationToken);

        if (descriptor is null)
        {
            return null;
        }

        if (!UserProfileImageLimits.IsAllowedContentType(descriptor.ContentType))
        {
            logger?.LogWarning(
                "A profile image row holds content type '{ContentType}', which is not a permitted "
                + "profile picture; it reads as absent.",
                descriptor.ContentType);
            return null;
        }

        return descriptor;
    }

    /// <summary>The bytes, loaded only once a conditional request has failed to match.</summary>
    public async Task<byte[]?> GetContentAsync(Guid imageId, CancellationToken cancellationToken = default) =>
        await context.UserProfileImageBlobs
            .AsNoTracking()
            .Where(blob => blob.UserProfileImageId == imageId)
            .Select(blob => blob.Content)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The caller's own <c>ImageVersion</c> token, or <c>null</c> when they have no picture. Response
    /// data for <c>GET /api/profile</c>; the client uses it to tell picture-present from
    /// picture-absent without probing the byte endpoint.
    /// </summary>
    public async Task<Guid?> GetVersionAsync(string userId, CancellationToken cancellationToken = default) =>
        await context.UserProfileImages
            .AsNoTracking()
            .Where(image => image.UserId == userId)
            .Select(image => (Guid?)image.ImageVersion)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The <c>ImageVersion</c> tokens for a set of users, for the admin list's per-row projection so a
    /// 50-row page costs one query rather than fifty. Callers must still apply the
    /// renderable-by-this-caller rule (a disabled subject's token is nulled) — this answers only
    /// "does a row exist, and what is its version".
    /// </summary>
    public async Task<Dictionary<string, Guid>> GetVersionsAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        return await context.UserProfileImages
            .AsNoTracking()
            .Where(image => userIds.Contains(image.UserId))
            .Select(image => new { image.UserId, image.ImageVersion })
            .ToDictionaryAsync(row => row.UserId, row => row.ImageVersion, StringComparer.Ordinal, cancellationToken);
    }

    // ── Write ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches or replaces the caller's own picture and returns the new <c>ImageVersion</c>.
    ///
    /// <para>
    /// <b>A replace UPDATES the row in place; it never deletes and re-inserts</b> (issue #94 §9). Three
    /// things break at once if that is ever changed: EF does not guarantee DELETE-before-INSERT
    /// ordering within one <c>SaveChangesAsync</c>, so the <i>ordinary</i> replace path would raise a
    /// duplicate-key error against the unique index on <c>UserId</c> — self-inflicted, not a race; the
    /// concurrency token becomes dead weight, because EF emits no <c>WHERE ImageVersion = @original</c>
    /// on an <c>INSERT</c>; and <c>UserProfileImageId</c> would change on every replace, which is the
    /// blob's PK <i>and</i> FK, so the old blob would cascade away and "new blob content" becomes
    /// inexpressible.
    /// </para>
    ///
    /// <para>
    /// <b>The blob write shares the metadata write's <c>SaveChangesAsync</c></b>, and that is not a
    /// tidiness point. The concurrency token guards <see cref="UserProfileImage"/>;
    /// <see cref="UserProfileImageBlob"/> has none of its own. Split across two saves, the loser of a
    /// replace race commits its <i>bytes</i> and is then refused on the <i>metadata</i>, leaving its
    /// image stored under the winner's <c>Sha256Hash</c> — not a lost update but a silent integrity
    /// break, on a read path whose whole design rests on revalidation being correct.
    /// </para>
    /// </summary>
    public async Task<Guid> SetAsync(
        string userId,
        byte[] bytes,
        string? declaredContentType,
        CancellationToken cancellationToken = default)
    {
        var effectiveMaxBytes = await GetEffectiveMaxBytesAsync(cancellationToken);
        var validated = UserProfileImageValidator.Validate(bytes, declaredContentType, effectiveMaxBytes);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var version = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(validated.Bytes)).ToLowerInvariant();

        var existing = await context.UserProfileImages
            .Include(image => image.Blob)
            .FirstOrDefaultAsync(image => image.UserId == userId, cancellationToken);

        if (existing is null)
        {
            var image = new UserProfileImage
            {
                UserProfileImageId = Guid.NewGuid(),
                UserId = userId,
                ImageVersion = version,
                ContentType = validated.ContentType,
                SizeBytes = validated.Bytes.LongLength,
                Sha256Hash = hash,
                Width = validated.Width,
                Height = validated.Height,
                UploadedAtUtc = now,
                UpdatedAtUtc = now,
            };
            image.Blob = new UserProfileImageBlob
            {
                UserProfileImageId = image.UserProfileImageId,
                Content = validated.Bytes,
            };

            context.UserProfileImages.Add(image);
        }
        else
        {
            // UploadedAtUtc is preserved across a replace; only UpdatedAtUtc is stamped.
            existing.ImageVersion = version;
            existing.ContentType = validated.ContentType;
            existing.SizeBytes = validated.Bytes.LongLength;
            existing.Sha256Hash = hash;
            existing.Width = validated.Width;
            existing.Height = validated.Height;
            existing.UpdatedAtUtc = now;

            if (existing.Blob is null)
            {
                context.UserProfileImageBlobs.Add(new UserProfileImageBlob
                {
                    UserProfileImageId = existing.UserProfileImageId,
                    Content = validated.Bytes,
                });
            }
            else
            {
                existing.Blob.Content = validated.Bytes;
            }
        }

        await SaveDetectingImageConflictAsync(cancellationToken);

        // The subject's user id, the VALIDATED content type and the byte length — never the
        // client-declared type (attacker-controlled, a log-injection vector), the original filename,
        // the hash, or any part of the image (§10.9).
        logger?.LogInformation(
            "Profile image set for user {UserId} ({ContentType}, {SizeBytes} bytes).",
            userId, validated.ContentType, validated.Bytes.LongLength);

        return version;
    }

    /// <summary>
    /// Removes the caller's own picture and its bytes — one of the three destructive erasure routes
    /// (issue #94 §10.13), all of which work only because the blob is the cascade's dependent.
    /// </summary>
    /// <remarks>
    /// Tracked <c>Remove</c>, never <c>ExecuteDeleteAsync</c>: that lives in
    /// <c>EntityFrameworkCore.Relational</c> and throws on the EF InMemory provider, so cleanup
    /// written that way is unrunnable on the tier that exercises it.
    /// </remarks>
    public async Task<ProfileImageRemoval> RemoveAsync(string userId, CancellationToken cancellationToken = default)
    {
        var existing = await context.UserProfileImages
            .Include(image => image.Blob)
            .FirstOrDefaultAsync(image => image.UserId == userId, cancellationToken);

        if (existing is null)
        {
            return ProfileImageRemoval.NotFound;
        }

        if (existing.Blob is not null)
        {
            context.UserProfileImageBlobs.Remove(existing.Blob);
        }

        context.UserProfileImages.Remove(existing);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A POST bumped the row's ImageVersion first, so this DELETE matched zero rows. The user's
            // intent ("I want no picture") is satisfied by re-reading and treating the row as gone if it
            // is — and only if it is not do we report the conflict. Unhandled, this is a 500 (§7).
            context.ChangeTracker.Clear();
            var stillThere = await context.UserProfileImages
                .AsNoTracking()
                .AnyAsync(image => image.UserId == userId, cancellationToken);

            if (stillThere)
            {
                throw new DomainConflictException(
                    "Your profile picture was changed by another request. Reload the page and try again.");
            }

            logger?.LogInformation("Profile image removed for user {UserId}.", userId);
            return ProfileImageRemoval.Removed;
        }

        logger?.LogInformation("Profile image removed for user {UserId}.", userId);
        return ProfileImageRemoval.Removed;
    }

    /// <summary>
    /// A <c>SaveChangesAsync</c> that turns both shapes of the write race into a
    /// <see cref="DomainConflictException"/> — a <c>409</c> with a curated message — rather than
    /// letting a raw index error surface as a <c>500</c> or as
    /// <c>GlobalExceptionHandler</c>'s generic conflict text.
    ///
    /// <para>
    /// Under the update-in-place shape above, the two catches cover <b>different</b> races and neither
    /// substitutes for the other: the <b>unique index</b> catches two concurrent <i>first</i> uploads,
    /// and the <b>concurrency token</b> catches two concurrent <i>replaces</i>.
    /// </para>
    /// </summary>
    private async Task SaveDetectingImageConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // DbUpdateConcurrencyException derives from DbUpdateException, so the filtered catch below
            // would not have covered it.
            throw new DomainConflictException(
                "Your profile picture was changed by another request. Reload the page and try again.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new DomainConflictException(
                "Your profile picture was changed by another request. Reload the page and try again.");
        }
    }

    /// <summary>
    /// Provider-agnostic detection: MySqlConnector reports 1062 (<c>ER_DUP_ENTRY</c>), and the message
    /// check keeps the mapping working where the inner exception is not the typed one.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains("1062", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
