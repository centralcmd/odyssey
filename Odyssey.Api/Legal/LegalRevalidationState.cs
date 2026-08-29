using System.Security.Claims;

namespace Odyssey.Api.Legal;

/// <summary>
/// Per-request marker telling <see cref="LegalComplianceClaimsPrincipalFactory"/> that it is running
/// inside <c>SecurityStampValidator</c>'s background revalidation rather than an interactive sign-in,
/// and carrying the principal being revalidated (issue #354 §5, §10.11).
/// </summary>
/// <remarks>
/// The distinction matters because the two paths must fail differently. A compliance-computation failure
/// at sign-in should surface as an ordinary login failure — the same as any other failure while building
/// the principal. During revalidation there is no user waiting on a login form: signing every active
/// session out on a transient DB blip would be a self-inflicted outage, and silently granting compliance
/// would defeat the feature. So the factory instead re-emits whatever pending-acceptance claims the
/// existing principal already carried and lets the next successful revalidation correct it — which it
/// can only do if it can see that principal, hence this.
///
/// Populated by the <c>OnValidatePrincipal</c> hook in <c>Program.cs</c>; left null on every other path,
/// which is exactly what makes "am I revalidating?" answerable.
/// </remarks>
public sealed class LegalRevalidationState
{
    public ClaimsPrincipal? ExistingPrincipal { get; set; }
}
