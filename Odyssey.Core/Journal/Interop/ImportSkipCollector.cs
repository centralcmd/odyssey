namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Accumulates skipped items by reason during an ICS/vCard import, keeping at most
/// <paramref name="maxSamplesPerReason"/> sample titles/names per reason while still counting every
/// skip. Shared by the Calendar/JournalEntry/Task/Contact import pipelines (architect finding F-9) —
/// previously four byte-identical private nested classes, differing only in the group DTO
/// <see cref="ToGroups{TGroup}"/> produced.
/// </summary>
internal sealed class ImportSkipCollector(int maxSamplesPerReason)
{
    private readonly Dictionary<string, (int Count, List<string> Samples)> byReason = new(StringComparer.Ordinal);
    private readonly List<string> order = [];

    public void Add(string reason, string sample)
    {
        if (!byReason.TryGetValue(reason, out var entry))
        {
            entry = (0, []);
            order.Add(reason);
        }

        entry.Count++;
        if (entry.Samples.Count < maxSamplesPerReason)
        {
            entry.Samples.Add(sample);
        }

        byReason[reason] = entry;
    }

    /// <summary>Projects the accumulated skips into the caller's group DTO shape, in first-seen reason
    /// order.</summary>
    public IReadOnlyList<TGroup> ToGroups<TGroup>(Func<string, int, IReadOnlyList<string>, TGroup> selector) =>
        order.Select(reason => selector(reason, byReason[reason].Count, byReason[reason].Samples)).ToList();
}
