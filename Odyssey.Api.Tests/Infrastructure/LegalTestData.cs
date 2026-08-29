using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Legal;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Helpers for tests that log in for real and therefore run the legal-acceptance claims factory
/// (issue #354). A freshly created user has accepted nothing, so their principal carries a
/// pending-acceptance claim and the gate answers every non-allowlisted call with a 451 — correct
/// behaviour, but noise for a test about something else. <see cref="AcceptAllAsync"/> puts a user in the
/// compliant state a seeded/real user would be in.
/// </summary>
/// <remarks>
/// Tests whose principal comes from <c>TestAuthHandler</c> need none of this: that principal never runs
/// through the claims factory, so it never carries the claim. The gate's own behaviour is covered
/// directly by <c>LegalComplianceGateTests</c>.
/// </remarks>
public static class LegalTestData
{
    /// <summary>The digest the API computes for the shipped <c>LICENSE</c> — the same code path, not a copy.</summary>
    public static string CurrentLicenseHash { get; } =
        new LicenseDocumentProvider(AppContext.BaseDirectory).Get().Sha256;

    /// <summary>Record an acceptance of the current License and, if one is published, the current ToS.</summary>
    public static async Task AcceptAllAsync(OdysseyContext context, string userId)
    {
        var respondedAt = DateTime.UtcNow;

        context.LicenseAcceptances.Add(new LicenseAcceptance
        {
            UserId = userId,
            LicenseHash = CurrentLicenseHash,
            Accepted = true,
            RespondedAt = respondedAt,
        });

        var currentVersionId = await context.TermsOfServiceVersions
            .OrderByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.Id)
            .Select(version => (int?)version.Id)
            .FirstOrDefaultAsync();

        if (currentVersionId is { } versionId)
        {
            context.TermsOfServiceAcceptances.Add(new TermsOfServiceAcceptance
            {
                UserId = userId,
                TermsOfServiceVersionId = versionId,
                Accepted = true,
                RespondedAt = respondedAt,
            });
        }

        await context.SaveChangesAsync();
    }
}
