namespace Odyssey.Dtos.Application;

/// <summary>
/// The effective per-contract limits, for any authenticated caller — no permission claim, so a
/// contract's smart-tag section can pre-check an add and name the real limit regardless of which
/// claims the signed-in user holds (issue #166 §5.4).
///
/// <para>
/// A sibling of <see cref="AccountLimitsDto"/>, <see cref="UploadLimitsDto"/> and
/// <see cref="ImportLimitsDto"/> rather than another field on any of them: each has its own cache key,
/// its own eviction trigger and its own degraded posture, so collapsing them would make any settings
/// save evict all of them and let one concern's degraded read <c>503</c> an endpoint the others need.
/// </para>
/// </summary>
public sealed record ContractLimitsDto
{
    /// <summary>Smart tags one contract may carry — the number the section interpolates into its message.</summary>
    public int MaxSmartTagsPerContract { get; set; }
}
