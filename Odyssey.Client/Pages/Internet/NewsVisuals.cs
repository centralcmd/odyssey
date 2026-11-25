using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>A feed's lifecycle on the Feeds page (§3.3): exactly one per feed.</summary>
public enum NewsFeedStatus { Active, Paused, Archived }

/// <summary>
/// The Internet pages' shared presentation (issue #473; Odyssey Design System · ui_kits/web/NewsFeeds.jsx,
/// <c>NF_STATUS_OPTIONS</c>, <c>NF_QUOTA_REASON</c>): the feed status, the quota-reason sentences and the tag
/// options. Every word the pages show for a server enum is declared here once.
/// </summary>
public static class NewsVisuals
{
    /// <summary>Archived when <c>archivedAtUtc</c> is set, else Paused when <c>!isActive</c>, else Active.</summary>
    public static NewsFeedStatus StatusOf(ExistingNewsFeed feed) =>
        feed.ArchivedAtUtc is not null ? NewsFeedStatus.Archived
        : !feed.IsActive ? NewsFeedStatus.Paused
        : NewsFeedStatus.Active;

    public static string Key(NewsFeedStatus status) => status switch
    {
        NewsFeedStatus.Paused => "paused",
        NewsFeedStatus.Archived => "archived",
        _ => "active",
    };

    /// <summary>The Status filter member for "any health that needs attention".</summary>
    public const string NeedsAttentionKey = "NeedsAttention";

    /// <summary>
    /// The lifecycle members. Paused is amber (<c>pending</c>), as Contracts' Paused: a paused feed has stopped and
    /// waits on you; Archived is the standard archived neutral.
    /// </summary>
    public static readonly OdsBreakdownDef<string>[] LifecycleDefs =
    [
        new("active", "Active", "income", "play_circle"),
        new("paused", "Paused", "pending", "pause_circle"),
        new("archived", "Archived", "outline", "inventory_2"),
    ];

    /// <summary>The health members the Status filter offers (Paused and Archived are already lifecycle members).</summary>
    public static readonly IReadOnlyList<NewsFeedHealth> FilterHealths =
    [
        NewsFeedHealth.Healthy, NewsFeedHealth.Pending, NewsFeedHealth.Failing,
        NewsFeedHealth.QuotaReached, NewsFeedHealth.Gone, NewsFeedHealth.Quarantined,
    ];

    /// <summary>
    /// The Status filter (design system <c>NF_STATUS_OPTIONS</c>): ONE mixed list — the three lifecycle members, then
    /// Needs attention and the health members, each wearing its registry glyph and tone.
    /// </summary>
    public static readonly OdsBreakdownDef<string>[] StatusDefs =
    [
        .. LifecycleDefs,
        new(NeedsAttentionKey, "Needs attention", "warning", "report"),
        .. FilterHealths.Select(h => new OdsBreakdownDef<string>(h.ToString(), OdsNewsFeedHealth.Meta(h).Label,
            OdsNewsFeedHealth.Meta(h).Tone ?? "outline", OdsNewsFeedHealth.Meta(h).Icon)),
    ];

    public static readonly IReadOnlyList<OdsOption> StatusOptions = OdsBreakdown.Options(StatusDefs);

    private static readonly HashSet<string> LifecycleKeys = [.. LifecycleDefs.Select(d => d.Key)];

    public static string Label(NewsFeedStatus status) => LifecycleDefs.Single(d => d.Key == Key(status)).Label;

    /// <summary>
    /// Whether <paramref name="feed"/> passes the ticked Status members: lifecycle members OR together, health members
    /// OR together, and the two groups AND — so Active + Failing is "active feeds that are failing". None ticked = all.
    /// </summary>
    public static bool MatchesStatus(ExistingNewsFeed feed, IReadOnlyCollection<string> statuses)
    {
        var life = statuses.Where(LifecycleKeys.Contains).ToList();
        var health = statuses.Where(s => !LifecycleKeys.Contains(s)).ToList();
        if (life.Count > 0 && !life.Contains(Key(StatusOf(feed))))
        {
            return false;
        }

        return health.Count == 0 || health.Any(s => s == NeedsAttentionKey
            ? OdsNewsFeedHealth.NeedsAttention(feed.Health)
            : string.Equals(s, feed.Health.ToString(), StringComparison.Ordinal));
    }

    /// <summary>The sentence behind a "Storage limit reached" chip (§3.3, K13), or null when it does not apply.</summary>
    public static string? QuotaText(NewsFeedHealth health, NewsFeedQuotaReason? reason) =>
        health != NewsFeedHealth.QuotaReached ? null : reason switch
        {
            NewsFeedQuotaReason.DailyCap => "Daily article limit reached. Resumes tomorrow (UTC).",
            NewsFeedQuotaReason.TextQuota => "Text storage quota is full. An administrator can raise it in Settings.",
            _ => "A storage limit has been reached.",
        };

    /// <summary>The fallback glyph ink for a tag with no colour of its own.</summary>
    public const string TagRowColor = "var(--tag-text)";

    /// <summary>A tag as a filter or picker option; an archived tag says so in its name and wears the archive glyph (§6.4).</summary>
    public static OdsOption TagOption(ExistingNewsTag tag) =>
        new(tag.NewsTagId.ToString(), tag.Archived is not null ? $"{tag.Name} (archived)" : tag.Name)
        {
            Icon = tag.Archived is not null ? "archive" : OdsTagChip.GlyphFor(tag.Icon),
            IconColor = tag.Archived is not null ? OdsBreakdown.Tone("outline") : UserColor.Glyph(tag.Color) ?? TagRowColor,
        };

    /// <summary>The Tag filter: active tags by name, then the archived ones (§3.3).</summary>
    public static IReadOnlyList<OdsOption> TagFilterOptions(IEnumerable<ExistingNewsTag> tags) =>
        [.. tags.OrderBy(t => t.Archived is not null).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(TagOption)];

    /// <summary>
    /// The <c>CreateBlocked</c> sentence for a news Tags field: news tag names are unique across active and archived
    /// tags, and a tag just minted in the same field counts too.
    /// </summary>
    public static string? BlockedTagName(IEnumerable<ExistingNewsTag> tags, IEnumerable<OdsOption> created, string text) =>
        TagNameRule.Blocked(
            tags.Select(t => new TagNameEntry(t.Name, t.Archived is not null))
                .Concat(created.Select(o => new TagNameEntry(o.Label, false))),
            text, TagNameRule.Pages.News);

    /// <summary>
    /// A tag picker's options: the active tags, plus any archived one already chosen — so an existing link stays
    /// visible and removable, while no archived tag can be newly added (the server refuses it, §7.2).
    /// </summary>
    public static IReadOnlyList<OdsOption> TagPickerOptions(IEnumerable<ExistingNewsTag> tags, IEnumerable<Guid> chosen)
    {
        var keep = chosen.ToHashSet();
        return [.. tags.Where(t => t.Archived is null || keep.Contains(t.NewsTagId))
            .OrderBy(t => t.Archived is not null).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(TagOption)];
    }

    /// <summary>
    /// The Overview's "By feed" / "By podcast" rows (Odyssey Design System · ui_kits/web/News.jsx, NewsFeeds.jsx,
    /// Podcasts.jsx): one row per feed the caller passes — the pages pass only active ones — with what it holds,
    /// most first, then by name. Every feed is listed: no cap and no "Other" row. Display only. A feed with a
    /// <see cref="FeedCount.Unit"/> shows its count as text ("5 articles"): the glyph is aria-hidden, so where the
    /// units differ within one tile the text must carry them (WCAG 1.3.1).
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> CountByFeedRows(IEnumerable<FeedCount> feeds) =>
    [
        .. feeds.Select(f => f with { Count = Math.Max(0, f.Count) })
            .OrderByDescending(f => f.Count).ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new OdsBreakdownRow
            {
                Key = f.Id,
                Label = f.Title,
                Icon = f.Icon,
                IconColor = OdsBreakdown.Tone("outline"),
                Count = f.Unit is { } unit ? NewsFeedback.Count(f.Count, unit.One, unit.Many) : f.Count,
            }),
    ];
}

/// <summary>One feed's row in a "By feed" tile: what it holds, its glyph and, where a tile mixes kinds, the unit's words.</summary>
public sealed record FeedCount(Guid Id, string Title, int Count, string Icon, (string One, string Many)? Unit = null);
