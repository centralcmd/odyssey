using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Api.Legal;

/// <summary>
/// Adds one <see cref="LegalClaims.PendingAcceptanceType"/> claim per document the user still owes a
/// response to (issue #354 §5). This is the server-side enforcement point: the gate middleware reads
/// only these claims, so the check costs no database round trip per request and cannot be bypassed by
/// skipping the client interstitial.
/// </summary>
/// <remarks>
/// <para>
/// It <b>subclasses</b> <see cref="UserClaimsPrincipalFactory{TUser, TRole}"/> and calls
/// <c>base.GenerateClaimsAsync</c> rather than building an identity from scratch, which is a security
/// property and not just a modelling preference (§10.8): the base implementation emits the
/// security-stamp claim that <c>SecurityStampValidator</c> uses to tell "recompute this principal" from
/// "sign this session out", plus the role and role-claim (permission) claims every <c>[Authorize]</c>
/// policy in the app depends on. A from-scratch factory would break session revalidation and
/// authorization platform-wide, not merely this feature.
/// </para>
/// <para>
/// It runs at every sign-in and, for an already-active session, is re-invoked by
/// <c>SecurityStampValidator</c> on its existing 30-minute <c>ValidationInterval</c> — which is what
/// bounds how long a session can outlive a newly published ToS (§2 non-goal 2).
/// </para>
/// <para>
/// Registration is load-bearing: see the <c>services.Replace(...)</c> call in <c>Program.cs</c> and the
/// DI-resolution test that pins it.
/// </para>
/// </remarks>
public sealed class LegalComplianceClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor,
    LegalComplianceService compliance,
    LegalRevalidationState revalidation,
    ILogger<LegalComplianceClaimsPrincipalFactory> logger)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        IReadOnlyList<string> pending;
        try
        {
            var outstanding = await compliance.GetOutstandingDocumentsAsync(user.Id, CancellationToken.None);
            pending = outstanding.Select(document => document.ToString()).ToList();
        }
        catch (Exception exception) when (revalidation.ExistingPrincipal is { } existing)
        {
            // Background revalidation: preserve what the session already had (see LegalRevalidationState).
            logger.LogError(
                exception,
                "Legal compliance computation failed during session revalidation for user {UserId}; "
                + "preserving the existing pending-acceptance claims.",
                user.Id);

            pending = existing.FindAll(LegalClaims.PendingAcceptanceType).Select(claim => claim.Value).ToList();
        }

        foreach (var document in pending)
        {
            identity.AddClaim(new Claim(LegalClaims.PendingAcceptanceType, document));
        }

        return identity;
    }
}
