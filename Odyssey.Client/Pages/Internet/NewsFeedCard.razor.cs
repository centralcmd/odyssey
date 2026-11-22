using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsFeedCard
{
    /// <summary>The page's announcement after a successful copy of the feed URL.</summary>
    public const string CopiedText = "Feed URL copied.";

    /// <summary>The page's announcement after a failed copy.</summary>
    public const string CopyFailedText = "Couldn’t copy. Select the text and copy it yourself.";

    [Parameter, EditorRequired] public ExistingNewsFeed Feed { get; set; } = default!;

    /// <summary>Every news tag, by id, to name the feed's tags.</summary>
    [Parameter] public IReadOnlyDictionary<Guid, ExistingNewsTag> TagsById { get; set; } = new Dictionary<Guid, ExistingNewsTag>();

    /// <summary>The row menu — View articles first, then only what the caller can do now (§3.3).</summary>
    [Parameter] public IReadOnlyList<OdsMenuItem> Menu { get; set; } = [];

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OnToggle { get; set; }

    [Parameter] public bool Highlight { get; set; }

    /// <summary>A write on this feed is in flight.</summary>
    [Parameter] public bool Busy { get; set; }

    /// <summary>Check again on a pending feed: the page re-reads it once.</summary>
    [Parameter] public EventCallback<ExistingNewsFeed> OnCheckAgain { get; set; }

    /// <summary>Raised with the copy's result text, for the page's announcer.</summary>
    [Parameter] public EventCallback<string> OnCopied { get; set; }

    /// <summary>A podcast's show artwork (the 200 px rendition), built from its id only; null without stored artwork.</summary>
    [Parameter] public string? ArtworkUrl { get; set; }

    private bool IsPodcast => Feed.Kind == NewsFeedKind.Podcast;

    public static string CardId(Guid id) => $"nf-{id}";

    private string QuotaId => $"nf-quota-{Feed.Id}";

    private string? QuotaText => NewsVisuals.QuotaText(Feed.Health, Feed.QuotaReason);

    private string? Lang => string.IsNullOrWhiteSpace(Feed.Language) ? null : Feed.Language;

    /// <summary>
    /// The feed glyph's colour (design system <c>nfHealthAccent</c>): the health registry's tone, except Paused, which
    /// is amber (<c>pending</c>) as in the Status filter. It only supports the chip, whose text says the state.
    /// </summary>
    internal string Accent => OdsBreakdown.Tone(Feed.Health == NewsFeedHealth.Paused
        ? "pending"
        : OdsNewsFeedHealth.Meta(Feed.Health).Tone ?? "outline");

    private string AccentSoft => $"color-mix(in srgb, {Accent} 14%, transparent)";

    /// <summary>The site's host from <c>siteUrl</c> only — never the fetch URL's — or null when there is none.</summary>
    internal string? SiteHost => SiteHostOf(Feed.SiteUrl);

    internal static string? SiteHostOf(string? siteUrl) =>
        Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri.IdnHost
            : null;

    /// <summary>The feed URL as a link target, only when it is safe to open (<see cref="OdsExternalLink.Target"/>).</summary>
    private static Uri? UrlTarget(string url) => OdsExternalLink.Target(url);

    private IEnumerable<ExistingNewsTag> FeedTags =>
        Feed.TagIds.Select(id => TagsById.GetValueOrDefault(id)).OfType<ExistingNewsTag>();

    private IReadOnlyList<RenderFragment?> Meta =>
    [
        IsPodcast && !string.IsNullOrWhiteSpace(Feed.Podcast?.Author) ? Bdi(Feed.Podcast!.Author!, Lang) : null,
        SiteHost is { } host ? OdsRecordMeta.Text(host) : null,
        Feed.LastCheckedAtUtc is { } checkedAt ? CheckedMeta(Utc(checkedAt)) : OdsRecordMeta.Text("Not checked yet"),
        OdsRecordMeta.Text($"Every {Feed.EffectiveIntervalMinutes} min"),
    ];

    private IReadOnlyList<OdsRecordCount> Counts => IsPodcast
        ?
        [
            new("podcasts", (Feed.Podcast?.EpisodeCount ?? 0).ToString("N0", System.Globalization.CultureInfo.CurrentCulture), "episodes"),
            new("cloud_done", (Feed.Podcast?.StoredAudioCount ?? 0).ToString("N0", System.Globalization.CultureInfo.CurrentCulture), "on the server"),
        ]
        :
    [
        new("mark_email_unread", Feed.UnreadCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), "unread"),
        new("article", Feed.ArticleCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), "articles"),
    ];

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>The last result in words: the server's sentence (it names a host only), else the plain state.</summary>
    private static string LastResult(NewsFeedManagement management) =>
        !string.IsNullOrWhiteSpace(management.LastErrorMessage) ? management.LastErrorMessage!
        : management.LastErrorKind == NewsFeedErrorKind.None ? "Fetched without errors"
        : "The last fetch failed";

    /// <summary>The Feed URL tile's own ⋯ menu: Copy URL.</summary>
    private IReadOnlyList<OdsMenuItem> UrlMenu(string url) =>
    [
        new()
        {
            Icon = "content_copy",
            Label = "Copy URL",
            AriaLabel = $"Copy URL of {Feed.DisplayTitle}",
            OnClick = EventCallback.Factory.Create(this, () => CopyUrlAsync(url)),
        },
    ];

    private RenderFragment CheckedMeta(DateTimeOffset at) => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddContent(1, "Checked ");
        builder.OpenComponent<OdsRelativeTime>(2);
        builder.AddComponentParameter(3, nameof(OdsRelativeTime.Value), at);
        builder.CloseComponent();
        builder.CloseElement();
    };

    private Task CheckAgain() => OnCheckAgain.InvokeAsync(Feed);

    private static RenderFragment Bdi(string text, string? lang) => builder =>
    {
        builder.OpenElement(0, "bdi");
        builder.AddAttribute(1, "lang", lang);
        builder.AddContent(2, text);
        builder.CloseElement();
    };

    private async Task CopyUrlAsync(string url)
    {
        var copied = await Clipboard.CopyAsync(url, null);
        await OnCopied.InvokeAsync(copied ? CopiedText : CopyFailedText);
    }
}
