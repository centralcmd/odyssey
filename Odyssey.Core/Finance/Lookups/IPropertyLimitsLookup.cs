namespace Odyssey.Core.Finance;

/// <summary>
/// The effective per-property limits (issue #167). Today that is one number, and the interface lives
/// here — in the consuming domain project — so <c>Odyssey.Core.Tests</c> can fake it without
/// referencing <c>Odyssey.Context</c>, exactly as <see cref="IAccountLimitsLookup"/> does.
/// </summary>
/// <param name="MaxSmartTagsPerProperty">Smart tags one property may carry.</param>
/// <param name="IsDegraded">
/// True when the value served is a fallback rather than configuration — the query failed, or a row was
/// present but unusable. An <em>absent</em> row is healthy: it resolves to the compiled default, the
/// same posture the settings service takes on reads. Conflating the two returns <c>503</c> on any
/// database whose rows have not been seeded, which is every fresh in-memory environment.
/// </param>
public sealed record PropertyLimits(int MaxSmartTagsPerProperty, bool IsDegraded);

/// <summary>Serves the effective property limits, cached briefly and evicted on write.</summary>
public interface IPropertyLimitsLookup
{
    Task<PropertyLimits> GetAsync(CancellationToken cancellationToken = default);
}
