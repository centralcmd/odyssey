namespace Odyssey.Core.Locations.Conversion;

/// <summary>
/// The in-memory lookups the one-time conversion matches against (issue #380 §11, L1): every location's
/// address key, exact-letter name and exact-letter address text, the merge forwarders, and the set of names
/// taken under the <c>_ci</c> collation. Loaded once at start; every location the conversion creates is
/// added, so matching and in-run dedupe are dictionary lookups, never a per-row query.
/// </summary>
/// <remarks>
/// <para>
/// Additions are journalled so a batch that rolls back can take its locations back out with
/// <see cref="Rollback"/> — otherwise a retried batch would link to a location its first attempt created
/// and the database no longer has. <see cref="Commit"/> clears the journal once the batch is durable.
/// </para>
/// <para>
/// Pure: nothing here touches a database, so <c>Odyssey.Core.Tests</c> covers it directly.
/// </para>
/// </remarks>
public sealed class LocationIndex
{
    private readonly Dictionary<string, Guid> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> byAddressText = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Guid> forwards = [];
    private readonly HashSet<string> takenNames = new(LocationNaming.CollationComparer);
    private readonly List<Action> journal = [];

    public int Count { get; private set; }

    /// <summary>Adds an existing or newly created location. Journalled until <see cref="Commit"/>.</summary>
    public void Add(Guid locationId, string name, string? addressText, string? addressKey, Guid? mergedInto = null)
    {
        Count++;
        journal.Add(() => Count--);

        if (addressKey is not null)
        {
            TryAdd(byKey, addressKey, locationId);
        }

        TryAdd(byName, LocationNaming.ExactLetterKey(name), locationId);
        if (addressText is not null)
        {
            TryAdd(byAddressText, LocationNaming.ExactLetterKey(addressText), locationId);
        }

        if (takenNames.Add(name))
        {
            journal.Add(() => takenNames.Remove(name));
        }

        if (mergedInto is { } survivor)
        {
            forwards[locationId] = survivor;
            journal.Add(() => forwards.Remove(locationId));
        }
    }

    /// <summary>
    /// Marks a name as taken although no loaded location carries it — a name the database refused with a
    /// duplicate-key error the collation comparer did not predict. Not journalled: the refusal stands.
    /// </summary>
    public void ReserveName(string name) => takenNames.Add(name);

    public bool IsNameTaken(string name) => takenNames.Contains(name);

    /// <summary>The location holding <paramref name="addressKey"/>, following a merge forwarder.</summary>
    public Guid? FindByKey(string addressKey) =>
        byKey.TryGetValue(addressKey, out var id) ? Survivor(id) : null;

    /// <summary>
    /// The location a free-text place names: its <c>Name</c> exactly on letters (§7.1.9), else its
    /// <c>AddressText</c> (R7), archived included, following a merge forwarder.
    /// </summary>
    public Guid? FindByText(string value)
    {
        var key = LocationNaming.ExactLetterKey(value);
        return byName.TryGetValue(key, out var byNameId) ? Survivor(byNameId)
            : byAddressText.TryGetValue(key, out var byTextId) ? Survivor(byTextId)
            : null;
    }

    /// <summary>A free name for <paramref name="baseName"/>, suffixed when the collation would collide.</summary>
    public string AllocateName(string baseName) => LocationNaming.Allocate(baseName, IsNameTaken);

    /// <summary>The batch committed: its additions stay.</summary>
    public void Commit() => journal.Clear();

    /// <summary>The batch rolled back: undo its additions, newest first.</summary>
    public void Rollback()
    {
        for (var i = journal.Count - 1; i >= 0; i--)
        {
            journal[i]();
        }

        journal.Clear();
    }

    private Guid Survivor(Guid id) => forwards.TryGetValue(id, out var survivor) ? survivor : id;

    private void TryAdd(Dictionary<string, Guid> map, string key, Guid id)
    {
        if (map.TryAdd(key, id))
        {
            journal.Add(() => map.Remove(key));
        }
    }
}
