namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// The row-keyset chunk size shared by the four Goal 8 export surfaces' streamed fetch (issue #343
/// §5): an internal, algorithmic constant under §6's boundary rule — an operator has no basis to
/// choose a chunk size, so it is not a System Settings field. 500 is the starting value; tune it
/// against the AC 11e peak-heap measurement and the AC 11f round-trip-count measurement together —
/// larger chunks trade round trips for peak heap (issue #343 §12).
/// </summary>
public static class ExportChunking
{
    public const int ChunkSize = 500;

    /// <summary>
    /// Re-orders a chunk's freshly-fetched rows back into the order captured by the up-front id
    /// snapshot <paramref name="idBatch"/> was sliced from — a <c>WHERE id IN (...)</c> fetch makes no
    /// promise about result order, and a row whose id no longer resolves (deleted between the snapshot
    /// and this chunk's fetch) is silently omitted rather than raised as an error.
    /// </summary>
    /// <remarks>
    /// Part of the PR #403 review fix for the bulk-export transaction bug: <c>OdysseyContext</c> runs
    /// with <c>EnableRetryOnFailure()</c> in production, which forbids a bare
    /// <c>Database.BeginTransactionAsync</c> unless the entire unit of work — begin, every query,
    /// commit — runs inside one <c>CreateExecutionStrategy().ExecuteAsync</c> call. That doesn't
    /// compose with a chunked read that yields output as it goes (a retry would re-emit chunks already
    /// streamed to the client), so the four export services no longer hold a transaction open across
    /// the whole export. Instead, the ordered id set is captured in one cheap up-front read (itself a
    /// plain, safely-retryable query), and each chunk is fetched independently by a fixed id batch —
    /// this method restores that batch's captured order to the result. The trade-off: a row edited
    /// between the snapshot and its chunk's fetch reflects its value at fetch time, not snapshot time,
    /// and a row deleted in that window drops out entirely — weaker than the point-in-time consistency
    /// a live RepeatableRead transaction would give every row, but a deliberate one for a bulk export
    /// (a short delivered-row count already reads as a failed/incomplete download to API clients, via
    /// the existing X-Odyssey-Export-Rows completeness-header contract).
    /// </remarks>
    public static List<T> ReorderToSnapshot<T>(IReadOnlyList<Guid> idBatch, IEnumerable<T> rows, Func<T, Guid> idSelector)
    {
        var byId = rows.ToDictionary(idSelector);
        var ordered = new List<T>(idBatch.Count);
        foreach (var id in idBatch)
        {
            if (byId.TryGetValue(id, out var row))
            {
                ordered.Add(row);
            }
        }

        return ordered;
    }
}
