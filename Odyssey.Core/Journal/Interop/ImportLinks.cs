namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// The link-collection rules the calendar-file imports share (issue #287 M8). The journal-entry and
/// task imports each carried their own copy of these loops, and the copies had drifted: one checked the
/// per-kind cap before de-duplicating and the other after, so a duplicate reference at the cap was
/// reported as "capped" by one import and ignored as a duplicate by the other.
/// </summary>
internal static class ImportLinks
{
    /// <summary>
    /// The media types an <c>.ics</c> upload may declare. Browsers and OSes label calendar files
    /// inconsistently, so the generic types are accepted too; the parse is the real validity gate.
    /// </summary>
    public static readonly string[] CalendarContentTypes = ["text/calendar", "application/octet-stream", "text/plain"];

    /// <summary>
    /// Reported when a record carries more links of one kind than the cap allows (issue #434 §9-A). The
    /// number is interpolated because the cap is admin-editable — a literal in the text would go stale
    /// the moment it changed. <paramref name="recordNoun"/> is what one imported row is called.
    /// </summary>
    public static string LinksCappedReason(string recordNoun, int maxLinksPerKind) =>
        $"Links over the per-{recordNoun} cap of {maxLinksPerKind} were not imported.";

    /// <summary>
    /// Resolves references to target ids in file order. In this order, for each reference: one that
    /// <paramref name="resolve"/> cannot resolve (null) is counted through <paramref name="onUnresolved"/>;
    /// a repeat of an id already kept is dropped silently — a duplicate is never "capped"; a new id past
    /// <paramref name="maxLinksPerKind"/> is counted through <paramref name="onCapped"/>.
    /// </summary>
    public static List<Guid> Resolve<TReference>(
        IEnumerable<TReference> references, Func<TReference, Guid?> resolve, int maxLinksPerKind,
        Action onUnresolved, Action onCapped)
    {
        var resolved = new List<Guid>();
        foreach (var reference in references)
        {
            if (resolve(reference) is not { } id)
            {
                onUnresolved();
                continue;
            }

            if (resolved.Contains(id))
            {
                continue;
            }

            if (resolved.Count >= maxLinksPerKind)
            {
                onCapped();
                continue;
            }

            resolved.Add(id);
        }

        return resolved;
    }

    /// <summary>
    /// Makes <paramref name="links"/> name exactly <paramref name="desiredIds"/>: links to anything else
    /// are removed, missing ones are created, and a link already present is left as it is.
    /// </summary>
    public static void Replace<TLink>(
        ICollection<TLink> links, IReadOnlyList<Guid> desiredIds, Func<TLink, Guid> targetOf, Func<Guid, TLink> create)
    {
        var desired = desiredIds.ToHashSet();
        foreach (var link in links.Where(link => !desired.Contains(targetOf(link))).ToList())
        {
            links.Remove(link);
        }

        var current = links.Select(targetOf).ToHashSet();
        foreach (var id in desiredIds.Where(current.Add))
        {
            links.Add(create(id));
        }
    }
}
