using Odyssey.Client.Components;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>A feed's status on the Feeds page (§3.3): disjoint, so the By status rows sum to the feed count (AC 60).</summary>
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

    public static readonly OdsBreakdownDef<string>[] StatusDefs =
    [
        new("active", "Active", "income", "play_circle"),
        new("paused", "Paused", "outline", "pause_circle"),
        new("archived", "Archived", "outline", "inventory_2"),
    ];

    public static readonly IReadOnlyList<OdsOption> StatusOptions = OdsBreakdown.Options(StatusDefs);

    public static string Label(NewsFeedStatus status) => StatusDefs.Single(d => d.Key == Key(status)).Label;

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
        new(tag.Id.ToString(), tag.Archived ? $"{tag.Name} (archived)" : tag.Name)
        {
            Icon = tag.Archived ? "archive" : OdsTagChip.GlyphFor(tag.Icon),
            IconColor = tag.Archived ? OdsBreakdown.Tone("outline") : UserColor.Glyph(tag.Color) ?? TagRowColor,
        };

    /// <summary>The Tag filter: active tags by name, then the archived ones (§3.3).</summary>
    public static IReadOnlyList<OdsOption> TagFilterOptions(IEnumerable<ExistingNewsTag> tags) =>
        [.. tags.OrderBy(t => t.Archived).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(TagOption)];

    /// <summary>
    /// A tag picker's options: the active tags, plus any archived one already chosen — so an existing link stays
    /// visible and removable, while no archived tag can be newly added (the server refuses it, §7.2).
    /// </summary>
    public static IReadOnlyList<OdsOption> TagPickerOptions(IEnumerable<ExistingNewsTag> tags, IEnumerable<Guid> chosen)
    {
        var keep = chosen.ToHashSet();
        return [.. tags.Where(t => !t.Archived || keep.Contains(t.Id))
            .OrderBy(t => t.Archived).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(TagOption)];
    }
}
