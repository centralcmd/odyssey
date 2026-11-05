using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Journal;

/// <summary>
/// The Overview figures the journal-module headers render — Journal, Tasks and the Photos/Albums
/// library panel. A static here rather than private members on each page, so the counting rules and
/// the per-tag glyph are testable without rendering a page (the <c>OverviewBreakdowns</c> precedent).
/// </summary>
/// <remarks>
/// Every "By tag" row leads with ITS tag's icon through <see cref="JournalTagChips.GlyphFor"/>, the
/// rule the tag chips use, so a tag without a known icon shows the same generic glyph on both, and
/// tints it in ITS tag's colour through <see cref="UserColor.Glyph"/>; a tag without a valid colour keeps
/// the family's ink. None of these tiles takes a total: a record carries several tags, so a sum counts it
/// once per tag.
/// </remarks>
public static class JournalOverviews
{
    /// <summary>The By tag row colour for a Journal or Tasks tag with no colour of its own.</summary>
    public const string TagRowColor = "var(--tag-text)";

    /// <summary>The By tag row colour for a Photos tag with no colour of its own — the panel's other tiles' ink.</summary>
    public const string PhotoTagRowColor = "var(--mud-palette-text-secondary)";

    /// <summary>
    /// Journal "By tag": the active tags, each counting the given entries that carry it. Zero-count
    /// rows are omitted.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> JournalTagRows(
        IEnumerable<ExistingJournalTag> tags, IEnumerable<JournalEntrySummary> entries) =>
        TagRows(tags.Where(t => t.Archived is null), t => t.JournalTagId, t => t.Name, t => t.Icon, t => t.Color,
            CountByTag(entries.Select(e => e.TagIds)), TagRowColor);

    /// <summary>
    /// Tasks "By tag": the active tags, each counting the tasks that carry it and are not archived —
    /// the same basis as the Open and Done tiles. Zero-count rows are omitted.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> TaskTagRows(
        IEnumerable<ExistingJournalTaskTag> tags, IEnumerable<JournalTaskSummary> tasks) =>
        TagRows(tags.Where(t => t.Archived is null), t => t.JournalTaskTagId, t => t.Name, t => t.Icon, t => t.Color,
            CountByTag(tasks.Where(t => t.Status != JournalTaskStatus.Archived).Select(t => t.TagIds)), TagRowColor);

    /// <summary>
    /// Photos "By tag", from the server's per-tag counts. Every tag the server counted is listed,
    /// archived or not, since the count is of active photos; zero-count rows are omitted.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> PhotoTagRows(
        IEnumerable<ExistingPhotoTag> tags, IEnumerable<PhotoCountByKey> counts) =>
        TagRows(tags, t => t.PhotoTagId, t => t.Name, t => t.Icon, t => t.Color,
            counts.ToDictionary(c => c.Key, c => c.Count), PhotoTagRowColor);

    /// <summary>The Tasks Overview's three figures, computed in one pass over a loaded task set.</summary>
    public static TaskOverview Tasks(IEnumerable<ExistingJournalTaskTag> tags, IReadOnlyCollection<JournalTaskSummary> tasks) =>
        new(
            Open: tasks.Count(t => t.Status is JournalTaskStatus.Backlog or JournalTaskStatus.Doing),
            Done: tasks.Count(t => t.Status == JournalTaskStatus.Done),
            TagRows: TaskTagRows(tags, tasks));

    /// <summary>The Tasks Overview: Open (backlog + doing), Done (not archived) and By tag.</summary>
    public sealed record TaskOverview(int Open, int Done, IReadOnlyList<OdsBreakdownRow> TagRows)
    {
        public static readonly TaskOverview Empty = new(0, 0, []);
    }

    // One pass over the records, so a row costs a lookup rather than a scan of every record.
    private static Dictionary<Guid, int> CountByTag(IEnumerable<IEnumerable<Guid>> tagIdsPerRecord)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in tagIdsPerRecord.SelectMany(ids => ids.Distinct()))
            counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts;
    }

    /// <summary>An album's glyph and colour — the By album row and the Album filter row.</summary>
    public const string AlbumGlyph = "photo_album";

    public const string AlbumColor = "var(--tide-400)";

    /// <summary>
    /// A tag as a filter row: its icon in its own colour, by the same rule as its By tag row, so the
    /// filter and the overview show one tag the same way.
    /// </summary>
    public static OdsOption TagOption(Guid id, string name, string? icon, string? color, string fallbackColor) =>
        new(id.ToString(), name)
        {
            Icon = JournalTagChips.GlyphFor(icon),
            IconColor = UserColor.Glyph(color) ?? fallbackColor,
        };

    /// <summary>A journal tag's filter row, with the fallback ink its By tag row uses.</summary>
    public static OdsOption JournalTagOption(ExistingJournalTag tag) =>
        TagOption(tag.JournalTagId, tag.Name, tag.Icon, tag.Color, TagRowColor);

    /// <summary>A task tag's filter row, with the fallback ink its By tag row uses.</summary>
    public static OdsOption TaskTagOption(ExistingJournalTaskTag tag) =>
        TagOption(tag.JournalTaskTagId, tag.Name, tag.Icon, tag.Color, TagRowColor);

    /// <summary>A photo tag's filter row, with the fallback ink its By tag row uses.</summary>
    public static OdsOption PhotoTagOption(ExistingPhotoTag tag) =>
        TagOption(tag.PhotoTagId, tag.Name, tag.Icon, tag.Color, PhotoTagRowColor);

    private static IReadOnlyList<OdsBreakdownRow> TagRows<TTag>(
        IEnumerable<TTag> tags, Func<TTag, Guid> id, Func<TTag, string> name, Func<TTag, string?> icon,
        Func<TTag, string?> color, IReadOnlyDictionary<Guid, int> counts, string fallbackColor) =>
        [.. tags
            .Select(t => (Tag: t, Count: counts.GetValueOrDefault(id(t))))
            .Where(x => x.Count > 0)
            .Select(x => new OdsBreakdownRow
            {
                Key = id(x.Tag),
                Icon = JournalTagChips.GlyphFor(icon(x.Tag)),
                IconColor = UserColor.Glyph(color(x.Tag)) ?? fallbackColor,
                Label = name(x.Tag),
                Count = x.Count,
            })];
}
