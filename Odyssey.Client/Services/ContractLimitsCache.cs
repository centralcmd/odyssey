using Odyssey.ApiClient.Resources;
using Odyssey.Dtos;

namespace Odyssey.Client.Services;

/// <summary>
/// The effective per-contract limits as the smart-tag section reads them (issue #166).
/// </summary>
/// <param name="MaxSmartTagsPerContract">
/// The cap to pre-check against. Meaningful only while <paramref name="IsDegraded"/> is false.
/// </param>
/// <param name="IsDegraded">
/// The server could not serve the configured value (<c>/api/contract-limits</c> answered <c>503</c>,
/// or the request failed). There is then <b>no number to pre-check against</b>: the section keeps its
/// adder open, says the limit is unavailable, and leaves the refusal to the server's conservative
/// bound. Guessing a ceiling here would either block an add the server would have allowed or allow one
/// it then refuses.
/// </param>
public readonly record struct ContractLimits(int MaxSmartTagsPerContract, bool IsDegraded);

/// <summary>
/// A per-session cache for <see cref="ContractLimits"/>, the same shape as
/// <see cref="IAccountLimitsCache"/> and the upload/import caches.
/// </summary>
/// <remarks>
/// <para>
/// <b>It differs from its account sibling in one way, deliberately.</b> That cache collapses a failed
/// read into <c>Fallback</c> and documents that there is no disable branch. Here the failure is
/// <em>reported</em>, because the design system's contract host has a state for it: a degraded limits
/// read stops the client-side pre-check instead of substituting a number nobody configured. The
/// account behaviour is unchanged — its specimen has no such state — so this is an addition, not a
/// correction of it.
/// </para>
/// <para>
/// <b>Failures are never cached.</b> A degraded read leaves the slot empty so the next reader retries;
/// it does not pin the whole session to a fallback.
/// </para>
/// <para>
/// <b>Mutations must invalidate.</b> A successful save on <c>/settings</c> calls
/// <see cref="Invalidate"/>, so an administrator who changes the cap and then expands a contract in
/// the same session pre-checks against the new value.
/// </para>
/// </remarks>
public interface IContractLimitsCache
{
    /// <summary>The effective contract limits, or a degraded reading when the server could not serve them.</summary>
    Task<ContractLimits> GetAsync(CancellationToken ct = default);

    /// <summary>Drops the cached limits; the next reader re-fetches.</summary>
    void Invalidate();
}

/// <inheritdoc cref="IContractLimitsCache" />
public sealed class ContractLimitsCache(IContractLimitsApiClient api) : IContractLimitsCache
{
    /// <summary>
    /// The shipped default, named by reference rather than restated: the same
    /// <see cref="SystemSettingsDefaults"/> constant the migration seeds and the server's
    /// <c>[Range]</c> bound names.
    ///
    /// <para>
    /// It is what a <b>degraded</b> reading carries so the UI has something to render, but the
    /// degraded flag is what the section actually acts on — the number is not presented as the cap.
    /// </para>
    /// </summary>
    public static readonly int FallbackMaxSmartTagsPerContract =
        SystemSettingsDefaults.ContractMaxSmartTagsPerContract;

    private Task<ContractLimits?>? pending;

    public async Task<ContractLimits> GetAsync(CancellationToken ct = default)
    {
        var task = pending ??= LoadAsync(ct);
        var result = await task;

        if (result is null && ReferenceEquals(pending, task))
        {
            pending = null;
        }

        return result ?? new ContractLimits(FallbackMaxSmartTagsPerContract, IsDegraded: true);
    }

    public void Invalidate() => pending = null;

    private async Task<ContractLimits?> LoadAsync(CancellationToken ct)
    {
        var result = await api.GetAsync(ct);
        return result.IsSuccess && result.Value is { } limits
            ? new ContractLimits(limits.MaxSmartTagsPerContract, IsDegraded: false)
            : null;
    }
}
