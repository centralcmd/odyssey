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
    /// The Contracts page-header windows. A third method here rather than folding the two windows
    /// into <see cref="GetRequestCapsAsync"/>: that record's entry is evicted by a per-request-cap
    /// change, and <c>SystemSettingDescriptor.CacheKeyToEvict</c> is a single string per descriptor,
    /// so sharing it would cross-evict.
    /// </summary>
    Task<ContractSummarySettings> GetContractSummarySettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Contracts page-header roll-up's windows and its next-charge row cap.
///
/// <para>
/// <see cref="EndingWindowDays"/> is returned to the CLIENT on <c>ContractSummary</c>, unlike the
/// other settings here, because the client renders it: the "Ending soon · Nd" summary row interpolates
/// it and the record headline reads "ending soon" against it. A client-side <c>const 45</c> beside an
/// admin-editable server value is the copy CLAUDE.md forbids, so the number travels rather than being
/// duplicated.
/// </para>
/// </summary>
public sealed record ContractSummarySettings(
    int EndingWindowDays,
    int ChargeWindowDays,
    int MaxSummaryCharges);

/// <summary>
/// Per-request caps for contracts and insurance, migrated out of POCO defaults nobody could change
/// (issue #421 Wave 3) — the <c>Contracts</c> and <c>Insurance</c> configuration sections had no
/// <c>appsettings.json</c> entry and no environment plumbing at all.
/// </summary>
public sealed record FinanceRequestCaps(
    int MaxPartiesPerContract,
    int MaxFilesPerContract,
    int MaxTermsPerContract,
    int MaxSummaryContracts,
    int MaxRenewalsPerPolicy,
    int MaxFilesPerParent,
    int MaxLinksPerPolicy);
