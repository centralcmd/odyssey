namespace Odyssey.Dtos.Application;

/// <summary>
/// The effective per-property limits, for any authenticated caller — no permission claim, so a
/// property's smart-tag section can pre-check an add and name the real limit regardless of which claims
/// the signed-in user holds (issue #167 §5, endpoint 14).
///
/// <para>
/// A sibling of <see cref="AccountLimitsDto"/> and <see cref="ContractLimitsDto"/> rather than another
/// field on either, for the reason those two give: each has its own cache key, its own eviction trigger
/// and its own degraded posture.
/// </para>
/// </summary>
public sealed record PropertyLimitsDto
{
    /// <summary>Smart tags one property may carry — the number the section interpolates into its message.</summary>
    public int MaxSmartTagsPerProperty { get; set; }
}
