namespace Odyssey.Core.Finance;

/// <summary>
/// The two Insurance settings migrated off <c>appsettings.json</c> and into the database-backed
/// system-settings store (issue #349). Cosmetic/policy fields — cached with a bounded TTL by the
/// implementation, unlike the authentication-perimeter fields (which are always read live and have
/// no lookup surface here at all).
/// </summary>
public sealed record InsurancePolicySettings(int ExpiringSoonWindowDays, int MaxSummaryPolicies);

/// <summary>
/// Narrow cross-domain lookup (issue #349), following the established pattern (<see cref="IContactLookup"/>,
/// <see cref="IFileLookup"/>) rather than a direct reference to <c>Odyssey.Context</c>: the
/// interface lives here so <c>Odyssey.Core.Tests</c> (EF InMemory, no dependency on that context)
/// can fake it, while the real implementation — <c>Odyssey.Api.SystemSettings.SystemSettingsLookup</c>,
/// backed by the <c>SystemSetting</c> table and a 30s <c>IMemoryCache</c> TTL — is wired at the API
/// composition root (<c>Odyssey.Api/Program.cs</c>).
/// </summary>
public interface ISystemSettingsLookup
{
    Task<InsurancePolicySettings> GetInsurancePolicySettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The finance-side per-request caps (issue #421 Wave 3). Added here rather than as a third
    /// Finance interface: these are consumed by the same project, and the insurance pair shares the
    /// existing cache entry, so one eviction point covers it.
    /// </summary>
    Task<FinanceRequestCaps> GetRequestCapsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The Subscriptions summary limits (issue #437). A third method here rather than a fourth Finance
    /// lookup interface, following <see cref="GetRequestCapsAsync"/>'s precedent — but on its own cache
    /// key in the implementation, which is forced rather than chosen:
    /// <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single string per descriptor, so sharing one
    /// entry would make a subscriptions change evict the insurance settings and vice versa.
    /// </summary>
    Task<SubscriptionSettings> GetSubscriptionSettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Subscriptions page-header roll-up's three limits (issue #437). Two replace <c>private const</c>s
/// on <c>SubscriptionService</c>; the third is new, because that summary's fetch was <strong>unbounded</strong>
/// — unlike its Insurance and Contracts siblings.
///
/// <para>
/// There is no <c>IsDegraded</c> flag and no <c>503</c> path, unlike <c>UploadLimits</c>/
/// <c>AccountLimits</c>: that machinery exists on those because their values are also served by
/// claim-free lookup endpoints that must fail closed. These three have no such endpoint — the
/// Subscriptions page holds no client-side copy of any of them — so every read path yields usable
/// numbers and the summary never fails because of a settings read.
/// </para>
/// </summary>
public sealed record SubscriptionSettings(
    int RenewalWindowDays,
    int MaxSummaryRenewals,
    int MaxSummarySubscriptions);

/// <summary>
/// Per-request caps for contracts and insurance, migrated out of POCO defaults nobody could change
/// (issue #421 Wave 3) — the <c>Contracts</c> and <c>Insurance</c> configuration sections had no
/// <c>appsettings.json</c> entry and no environment plumbing at all.
/// </summary>
public sealed record FinanceRequestCaps(
    int MaxPartiesPerContract,
    int MaxFilesPerContract,
    int MaxSummaryContracts,
    int MaxRenewalsPerPolicy,
    int MaxFilesPerParent);
