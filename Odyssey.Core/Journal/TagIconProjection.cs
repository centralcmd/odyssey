using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;

namespace Odyssey.Core.Journal;

/// <summary>
/// Fills <see cref="ITagIconRecord.TagIds"/> and <see cref="ITagIconRecord.DisplayIcon"/> on every journal
/// entry, task and photo response (issue #345) — list, detail, create and update alike.
/// </summary>
/// <remarks>
/// This and <c>Finance/MapsterConfig.cs</c> are the only places a display icon is assigned; both call
/// <see cref="TagIcons"/>, and a source-lint pins that. Resolution runs after materialisation, so the EF
/// InMemory tier runs the same code and <c>OrdinalIgnoreCase</c> needs no SQL translation.
///
/// <para>
/// Single-record responses read the tags through the batched lookups below rather than through
/// navigations: a create builds link rows with no tag loaded, and an update adds links to an entity
/// loaded with its links only, so a navigation would be empty on exactly the paths that change tags.
/// </para>
/// </remarks>
public static class TagIconProjection
{
    /// <summary>Orders <paramref name="tags"/>, writes their ids and resolves the display icon.</summary>
    public static T Apply<T>(T record, IEnumerable<TagIconCandidate> tags) where T : ITagIconRecord
    {
        var ordered = TagIcons.Order(tags);
        record.TagIds = ordered.Select(tag => tag.Id).ToList();
        record.DisplayIcon = TagIcons.Resolve(ordered);
        return record;
    }

    /// <summary>
    /// <see cref="Apply{T}(T, IEnumerable{TagIconCandidate})"/> over ids resolved through a lookup. An id
    /// the lookup lacks (a dangling link, possible only on the FK-free InMemory tier) is kept, sorts last
    /// and contributes no icon.
    /// </summary>
    public static T Apply<T>(T record, IEnumerable<Guid> tagIds, IReadOnlyDictionary<Guid, TagIconCandidate> lookup)
        where T : ITagIconRecord =>
        Apply(record, tagIds.Distinct().Select(id => lookup.GetValueOrDefault(id) ?? new TagIconCandidate(id, null, null)));

    public static Task<Dictionary<Guid, TagIconCandidate>> JournalTagsAsync(
        OdysseyContext context, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        context.JournalTags.AsNoTracking()
            .Where(tag => ids.Contains(tag.JournalTagId))
            .Select(tag => new TagIconCandidate(tag.JournalTagId, tag.Name, tag.Icon))
            .ToDictionaryAsync(tag => tag.Id, cancellationToken);

    public static Task<Dictionary<Guid, TagIconCandidate>> TaskTagsAsync(
        OdysseyContext context, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        context.JournalTaskTags.AsNoTracking()
            .Where(tag => ids.Contains(tag.JournalTaskTagId))
            .Select(tag => new TagIconCandidate(tag.JournalTaskTagId, tag.Name, tag.Icon))
            .ToDictionaryAsync(tag => tag.Id, cancellationToken);

    public static Task<Dictionary<Guid, TagIconCandidate>> PhotoTagsAsync(
        OdysseyContext context, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        context.PhotoTags.AsNoTracking()
            .Where(tag => ids.Contains(tag.PhotoTagId))
            .Select(tag => new TagIconCandidate(tag.PhotoTagId, tag.Name, tag.Icon))
            .ToDictionaryAsync(tag => tag.Id, cancellationToken);
}
