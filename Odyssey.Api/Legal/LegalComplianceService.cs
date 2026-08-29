using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Odyssey.Api.Identity;
using Odyssey.Context;
using Odyssey.Context.Legal;
using Odyssey.Dtos.Application;

namespace Odyssey.Api.Legal;

/// <summary>
/// The one place that knows what "compliant" means (issue #354 §5). Everything else — the claims
/// factory, the gate middleware, the endpoints — asks this service rather than re-deriving the rule.
/// </summary>
/// <remarks>
/// A user is compliant with a document when their <em>most recent</em> response against the exact
/// artefact currently in force (the computed <c>LICENSE</c> digest, or the current ToS version id) is an
/// acceptance. Older responses against a superseded artefact never count, which is what makes both a
/// License text change and a ToS publish automatically re-gate everyone with no backfill (§13).
///
/// With no ToS version ever published there is nothing to accept, so every user is trivially
/// ToS-compliant — "not yet published" is a supported state, not an error (§11, AC 17).
/// </remarks>
public sealed class LegalComplianceService(
    OdysseyContext context,
    ILicenseDocumentProvider license,
    IUserDisplayNameResolver displayNames,
    TimeProvider timeProvider)
{
    public LicenseDocument GetLicense() => license.Get();

    public async Task<TermsOfServiceDocument?> GetCurrentTermsOfServiceAsync(CancellationToken cancellationToken)
    {
        var current = await CurrentVersionQuery().AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        return current is null
            ? null
            : new TermsOfServiceDocument
            {
                Id = current.Id,
                Content = current.Content,
                PublishedAt = current.PublishedAt,
            };
    }

    public async Task<LegalComplianceStatus> GetStatusAsync(string userId, CancellationToken cancellationToken)
    {
        var currentVersionId = await CurrentVersionIdAsync(cancellationToken);

        return new LegalComplianceStatus
        {
            LicenseCompliant = await IsLicenseCompliantAsync(userId, cancellationToken),
            TosCompliant = await IsTermsOfServiceCompliantAsync(userId, currentVersionId, cancellationToken),
            CurrentTosVersionId = currentVersionId,
        };
    }

    /// <summary>
    /// The documents still owed, in the order the interstitial renders them. Empty ⇒ compliant. This is
    /// what the claims factory turns into pending-acceptance claims and what the gate middleware
    /// ultimately enforces, so it is deliberately the same computation as <see cref="GetStatusAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<LegalDocumentType>> GetOutstandingDocumentsAsync(
        string userId, CancellationToken cancellationToken)
    {
        var outstanding = new List<LegalDocumentType>(2);

        if (!await IsLicenseCompliantAsync(userId, cancellationToken))
        {
            outstanding.Add(LegalDocumentType.License);
        }

        var currentVersionId = await CurrentVersionIdAsync(cancellationToken);
        if (!await IsTermsOfServiceCompliantAsync(userId, currentVersionId, cancellationToken))
        {
            outstanding.Add(LegalDocumentType.TermsOfService);
        }

        return outstanding;
    }

    /// <summary>
    /// Record one accept/decline. The artefact responded against is resolved server-side in both cases:
    /// the License digest is never accepted from the client at all, and the echoed ToS version id is
    /// verified against the current version rather than trusted (§10.3).
    /// </summary>
    public async Task RespondAsync(string userId, LegalDocumentResponse request, CancellationToken cancellationToken)
    {
        // [ApiController] model validation rejects an omitted/invalid value first; these guard the
        // service against direct (non-HTTP) callers, matching the ListQuery clamp convention.
        if (request.DocumentType is not { } documentType || !Enum.IsDefined(documentType))
        {
            throw new LegalValidationException("A valid documentType is required.");
        }

        if (request.Accepted is not { } accepted)
        {
            throw new LegalValidationException("accepted is required.");
        }

        var respondedAt = timeProvider.GetUtcNow().UtcDateTime;

        if (documentType is LegalDocumentType.License)
        {
            context.LicenseAcceptances.Add(new LicenseAcceptance
            {
                UserId = userId,
                LicenseHash = license.Get().Sha256,
                Accepted = accepted,
                RespondedAt = respondedAt,
            });
        }
        else
        {
            var currentVersionId = await CurrentVersionIdAsync(cancellationToken)
                ?? throw new LegalVersionConflictException("No Terms of Service version has been published.");

            if (request.TosVersionId is not { } echoedVersionId)
            {
                throw new LegalValidationException("tosVersionId is required when responding to the Terms of Service.");
            }

            if (echoedVersionId != currentVersionId)
            {
                throw new LegalVersionConflictException(
                    "The Terms of Service changed while you were reading it. Reload and respond to the current version.");
            }

            context.TermsOfServiceAcceptances.Add(new TermsOfServiceAcceptance
            {
                UserId = userId,
                TermsOfServiceVersionId = currentVersionId,
                Accepted = accepted,
                RespondedAt = respondedAt,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Metadata only — the list never carries content, however long the history grows (§12, AC 25).</summary>
    public async Task<IReadOnlyList<ExistingTermsOfServiceVersion>> GetVersionsAsync(
        ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var rows = await context.TermsOfServiceVersions
            .AsNoTracking()
            .OrderByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.Id)
            .Select(version => new { version.Id, version.PublishedAt, version.PublishedByUserId })
            .ToListAsync(cancellationToken);

        var resolved = await displayNames.ResolveAsync(
            caller, rows.Select(row => row.PublishedByUserId), cancellationToken);

        return rows
            .Select(row => new ExistingTermsOfServiceVersion
            {
                Id = row.Id,
                PublishedAt = row.PublishedAt,
                PublishedByUserId = row.PublishedByUserId,
                PublishedByDisplayName = DisplayNameFor(row.PublishedByUserId, resolved),
            })
            .ToList();
    }

    public async Task<TermsOfServiceVersionDetail?> GetVersionAsync(
        ClaimsPrincipal caller, int id, CancellationToken cancellationToken)
    {
        var version = await context.TermsOfServiceVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return version is null ? null : await ToDetailAsync(caller, version, cancellationToken);
    }

    /// <summary>
    /// Publish a new current version. Purely additive: prior versions and every prior acceptance row are
    /// untouched, so the history stays complete and old acceptances simply stop satisfying the rule
    /// above (AC 10).
    /// </summary>
    public async Task<TermsOfServiceVersionDetail> PublishAsync(
        ClaimsPrincipal caller, string actorUserId, NewTermsOfServiceVersion request, CancellationToken cancellationToken)
    {
        var content = request.Content?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            throw new LegalValidationException("Content is required.");
        }

        if (content.Length > LegalLimits.MaxTermsOfServiceContentLength)
        {
            throw new LegalValidationException(
                $"Content must be at most {LegalLimits.MaxTermsOfServiceContentLength} characters.");
        }

        var version = new TermsOfServiceVersion
        {
            Content = content,
            PublishedAt = timeProvider.GetUtcNow().UtcDateTime,
            PublishedByUserId = string.IsNullOrWhiteSpace(actorUserId) ? null : actorUserId,
        };

        context.TermsOfServiceVersions.Add(version);
        await context.SaveChangesAsync(cancellationToken);

        return await ToDetailAsync(caller, version, cancellationToken);
    }

    private async Task<TermsOfServiceVersionDetail> ToDetailAsync(
        ClaimsPrincipal caller, TermsOfServiceVersion version, CancellationToken cancellationToken)
    {
        var displayName = version.PublishedByUserId is null
            ? null
            : await displayNames.ResolveAsync(caller, version.PublishedByUserId, cancellationToken);

        return new TermsOfServiceVersionDetail
        {
            Id = version.Id,
            Content = version.Content,
            PublishedAt = version.PublishedAt,
            PublishedByUserId = version.PublishedByUserId,
            PublishedByDisplayName = displayName,
        };
    }

    private async Task<bool> IsLicenseCompliantAsync(string userId, CancellationToken cancellationToken)
    {
        var hash = license.Get().Sha256;

        return await context.LicenseAcceptances
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.LicenseHash == hash)
            .OrderByDescending(row => row.RespondedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => (bool?)row.Accepted)
            .FirstOrDefaultAsync(cancellationToken) == true;
    }

    private async Task<bool> IsTermsOfServiceCompliantAsync(
        string userId, int? currentVersionId, CancellationToken cancellationToken)
    {
        if (currentVersionId is not { } versionId)
        {
            return true;
        }

        return await context.TermsOfServiceAcceptances
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.TermsOfServiceVersionId == versionId)
            .OrderByDescending(row => row.RespondedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => (bool?)row.Accepted)
            .FirstOrDefaultAsync(cancellationToken) == true;
    }

    private Task<int?> CurrentVersionIdAsync(CancellationToken cancellationToken) =>
        CurrentVersionQuery().AsNoTracking().Select(version => (int?)version.Id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>"Current" is the highest <c>PublishedAt</c>; a tie breaks to the highest <c>Id</c> (AC 26).</summary>
    private IQueryable<TermsOfServiceVersion> CurrentVersionQuery() =>
        context.TermsOfServiceVersions
            .OrderByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.Id);

    private static string? DisplayNameFor(string? userId, IReadOnlyDictionary<string, string> resolved) =>
        userId is null ? null : resolved.GetValueOrDefault(userId);
}
