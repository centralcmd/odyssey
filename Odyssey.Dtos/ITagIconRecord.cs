namespace Odyssey.Dtos;

/// <summary>
/// A journal entry, task or photo read model that carries tag ids and the icon resolved from them
/// (issue #345). Exists so one server-side helper fills both members on every response shape, rather
/// than each producer assigning <see cref="DisplayIcon"/> itself.
/// </summary>
public interface ITagIconRecord
{
    /// <summary>The record's tag ids, in <see cref="TagIcons.Comparer"/> order.</summary>
    List<Guid> TagIds { get; set; }

    /// <summary>
    /// The icon representing the record: <see cref="TagIcons.Resolve(IEnumerable{TagIconCandidate}?)"/>
    /// over its tags — the first with a known icon, else <see cref="TagIcons.Default"/>. Never <c>null</c>.
    /// </summary>
    string DisplayIcon { get; set; }
}
