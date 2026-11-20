namespace Odyssey.Client.Services;

/// <summary>
/// The temporary-id bookkeeping a picker's inline create needs, shared by <see cref="ContactQuickCreate"/> and
/// <see cref="TagQuickCreate{TTag}"/>: a create is staged under a temporary id the picker can select at once, its
/// POST runs behind it, and the host later maps each selected id back to the real one.
/// </summary>
/// <remarks>
/// One ledger per creator, and a creator is registered transient — one per dialog — because the temporary ids
/// are only meaningful to the surface holding them.
/// </remarks>
internal sealed class QuickCreateLedger
{
    private readonly List<Task> pending = [];
    private readonly Dictionary<string, string> resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> staged = new(StringComparer.Ordinal);

    /// <summary>True while any staged create is still in flight.</summary>
    public bool HasPending => pending.Any(t => !t.IsCompleted);

    /// <summary>The typed name trimmed and cut to <paramref name="maxLength"/>, or null when it is blank.</summary>
    public static string? CleanName(string? name, int maxLength)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            return null;
        return clean.Length > maxLength ? clean[..maxLength] : clean;
    }

    /// <summary>Mints a temporary id, records it as staged and starts <paramref name="create"/> for it.</summary>
    public string Stage(Func<string, Task> create)
    {
        var tempId = Guid.NewGuid().ToString();
        staged.Add(tempId);
        pending.Add(create(tempId));
        return tempId;
    }

    /// <summary>Records the real id a staged create came back with.</summary>
    public void Complete(string tempId, Guid id) => resolved[tempId] = id.ToString();

    /// <summary>Await every create staged so far.</summary>
    public Task WhenSettledAsync() => Task.WhenAll(pending.ToArray());

    /// <summary>
    /// The real id for <paramref name="id"/>: the value itself when it was never staged here, and <c>null</c> when
    /// its create failed or has not finished.
    /// </summary>
    public string? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        if (!staged.Contains(id))
            return id;
        return resolved.GetValueOrDefault(id);
    }
}
