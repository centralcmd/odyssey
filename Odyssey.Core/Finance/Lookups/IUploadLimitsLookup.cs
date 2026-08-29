namespace Odyssey.Core.Finance;

/// <summary>
/// The effective upload cap (issue #421 Wave 4), for the file-validation path and the transport
/// middleware. The interface lives in the consuming domain project so its tests can fake it.
/// </summary>
/// <param name="MaxUploadBytes">
/// The cap on file content, in bytes. Converted from the stored megabytes once, here at the lookup
/// boundary, so no consumer repeats the arithmetic.
/// </param>
/// <param name="MaxUploadMegabytes">The same cap as stored, for messages that should name a round number.</param>
/// <param name="IsDegraded">
/// True when the value served is a fallback rather than configuration — the query failed, or a row was
/// present but unusable. An <em>absent</em> row is healthy: it resolves to the compiled default, the
/// same posture the settings service takes on reads.
/// </param>
public sealed record UploadLimits(long MaxUploadBytes, int MaxUploadMegabytes, bool IsDegraded);

/// <summary>Serves the effective upload cap, cached briefly and evicted on write.</summary>
public interface IUploadLimitsLookup
{
    Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default);
}
