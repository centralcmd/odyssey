namespace Odyssey.Dtos.Application;

/// <summary>
/// The effective per-account limits, for any authenticated caller — no permission claim, so the
/// Accounts page's smart-tag section can pre-check an add and name the real limit regardless of which
/// claims the signed-in user holds (issue #434 key 15).
///
/// <para>
/// A sibling of <see cref="UploadLimitsDto"/> and <see cref="ImportLimitsDto"/> rather than another
/// field on either: each of the three has its own cache key, its own eviction trigger and its own
/// degraded posture, so collapsing them would make any settings save evict all three and let one
/// concern's degraded read <c>503</c> an endpoint the others need.
/// </para>
/// </summary>
public sealed record AccountLimitsDto
{
    /// <summary>Smart tags one account may carry — the number the section interpolates into its message.</summary>
    public int MaxSmartTagsPerAccount { get; set; }
}
