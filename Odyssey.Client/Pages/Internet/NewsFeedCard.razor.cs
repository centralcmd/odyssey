using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

public partial class NewsFeedCard
{
    [Parameter, EditorRequired] public ExistingNewsFeed Feed { get; set; } = default!;

    /// <summary>Every news tag, by id, to name the feed's tags.</summary>
    [Parameter] public IReadOnlyDictionary<Guid, ExistingNewsTag> TagsById { get; set; } = new Dictionary<Guid, ExistingNewsTag>();

    /// <summary>The row menu — only what the caller can do now; an unavailable item is absent (§3.3).</summary>
    [Parameter] public IReadOnlyList<OdsMenuItem> Menu { get; set; } = [];

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OnToggle { get; set; }

    [Parameter] public bool Highlight { get; set; }

    /// <summary>A write on this feed is in flight.</summary>
    [Parameter] public bool Busy { get; set; }

    [Parameter] public bool CanUpdate { get; set; }

    [Parameter] public bool CanDelete { get; set; }

    /// <summary>The page is polling this pending feed (§3.3, Create flow).</summary>
    [Parameter] public bool Polling { get; set; }

    /// <summary>The 2-minute poll ended without a first fetch.</summary>
    [Parameter] public bool PollExpired { get; set; }

    [Parameter] public EventCallback<ExistingNewsFeed> OnCheckAgain { get; set; }

    [Parameter] public EventCallback<ExistingNewsFeed> OnStopChecking { get; set; }

    [Parameter] public EventCallback<ExistingNewsFeed> OnUseNewAddress { get; set; }

    /// <summary>Raised with the copy's result text, for the page's announcer.</summary>
    [Parameter] public EventCallback<string> OnCopied { get; set; }

    private bool _showFullUrl;

    public static string CardId(Guid id) => $"nf-{id}";

    private string QuotaId => $"nf-quota-{Feed.Id}";

    private string? QuotaText => NewsVisuals.QuotaText(Feed.Health, Feed.QuotaReason);

    private string? Lang => string.IsNullOrWhiteSpace(Feed.Language) ? null : Feed.Language;

    /// <summary>The site's host from <c>siteUrl</c> only — never the fetch URL's — or null when there is none.</summary>
    internal string? SiteHost => SiteHostOf(Feed.SiteUrl);

    internal static string? SiteHostOf(string? siteUrl) =>
        Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri.IdnHost
            : null;

    private IEnumerable<ExistingNewsTag> FeedTags =>
        Feed.TagIds.Select(id => TagsById.GetValueOrDefault(id)).OfType<ExistingNewsTag>();

    private IReadOnlyList<RenderFragment?> Meta =>
    [
        SiteHost is { } host ? OdsRecordMeta.Text(host) : null,
        Feed.LastCheckedAtUtc is { } checkedAt ? CheckedMeta(Utc(checkedAt)) : OdsRecordMeta.Text("Not checked yet"),
        OdsRecordMeta.Text($"Every {Feed.EffectiveIntervalMinutes} min"),
    ];

    private IReadOnlyList<OdsRecordCount> Counts =>
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

    private Task StopChecking() => OnStopChecking.InvokeAsync(Feed);

    private Task UseNewAddress() => OnUseNewAddress.InvokeAsync(Feed);

    private async Task CopyUrlAsync(string url)
    {
        var copied = await Clipboard.CopyAsync(url, null);
        await OnCopied.InvokeAsync(copied ? "Link copied" : "Couldn't copy. Select the text instead.");
    }
}
