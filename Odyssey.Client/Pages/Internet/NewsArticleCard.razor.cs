using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>Where an expanded card's body stands (issue #473 §3.2.7).</summary>
public enum NewsBodyPhase { Loading, Ready, Error, StorageUnavailable }

/// <summary>An article's detail as the page holds it for the card's lifetime (§5.1: cached once loaded).</summary>
/// <param name="Phase">The body state.</param>
/// <param name="Detail">The detail, once <see cref="NewsBodyPhase.Ready"/>.</param>
/// <param name="Retrying">A Retry is waiting out <c>Retry-After</c> or in flight: the button stays, aria-disabled.</param>
public sealed record NewsArticleDetailState(NewsBodyPhase Phase, NewsArticleDetail? Detail = null, bool Retrying = false);

public partial class NewsArticleCard
{
    public const string ErrorText = "Couldn’t load this article.";

    public const string StorageText = "The article text isn’t available right now.";

    public const string UnavailableText = "The article text isn’t available right now.";

    public const string OmittedText = "This article is too large or complex to show here.";

    [Parameter, EditorRequired] public NewsArticleSummary Article { get; set; } = default!;

    /// <summary>The feed's language from the catalogue, the fallback when the article carries none (§6.8).</summary>
    [Parameter] public string? FeedLanguage { get; set; }

    /// <summary>Every news tag, by id, to name the article's own and inherited tags.</summary>
    [Parameter] public IReadOnlyDictionary<Guid, ExistingNewsTag> TagsById { get; set; } = new Dictionary<Guid, ExistingNewsTag>();

    /// <summary>The row menu — only what the caller can do; an unavailable item is absent.</summary>
    [Parameter] public IReadOnlyList<OdsMenuItem> Menu { get; set; } = [];

    /// <summary>The Original link tile's own ⋯ menu: Open original (a safe URL only) and Copy link.</summary>
    [Parameter] public IReadOnlyList<OdsMenuItem> LinkMenu { get; set; } = [];

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OnToggle { get; set; }

    /// <summary>The detail state, once the card has been expanded.</summary>
    [Parameter] public NewsArticleDetailState? Detail { get; set; }

    /// <summary>Seconds left before the store may be asked again (<c>NewsStoreGate</c>), for the Retry line.</summary>
    [Parameter] public int? RetryWaitSeconds { get; set; }

    /// <summary>The caller holds <c>system-settings.read</c>, the Background jobs gate (K10).</summary>
    [Parameter] public bool CanReadJobs { get; set; }

    /// <summary>Builds an image's <c>&lt;img src&gt;</c> from the two ids only (§5.1).</summary>
    [Parameter] public Func<Guid, Guid, string>? ImageUrl { get; set; }

    /// <summary>A read-state write on this article is in flight.</summary>
    [Parameter] public bool Busy { get; set; }

    [Parameter] public EventCallback<NewsArticleSummary> OnRetryDetail { get; set; }

    [Parameter] public EventCallback<NewsArticleSummary> OnShowVersions { get; set; }

    public static string CardId(Guid id) => $"nwa-{id}";

    public static string RetryId(Guid id) => $"nwa-retry-{id}";

    /// <summary>The title, or "(untitled)" for an article that has none.</summary>
    public static string TitleOf(NewsArticleFields article) =>
        string.IsNullOrWhiteSpace(article.Title) ? "(untitled)" : article.Title;

    /// <summary>A language tag to set, or <c>null</c>: absent or blank sets none (§6.8; the API validates the tag).</summary>
    public static string? LangOf(string? language) => string.IsNullOrWhiteSpace(language) ? null : language.Trim();

    /// <summary>
    /// Read, then changed by the publisher (<c>ContentChangedAtUtc &gt; ReadAtUtc</c>) — the server's Updated status. It
    /// stays read; opening it and leaving it again moves <c>ReadAtUtc</c> on, which clears it.
    /// </summary>
    public static bool UpdatedSinceRead(NewsArticleSummary article) =>
        article.IsRead && article.ContentChangedAtUtc is { } changed && article.ReadAtUtc is { } read && changed > read;

    private bool IsUpdatedSinceRead => UpdatedSinceRead(Article);

    /// <summary>The article glyph takes the Status filter's colour: amber updated since read, grey read, blue unread.</summary>
    private string AccentColor => IsUpdatedSinceRead ? OdsBreakdown.Tone("warning")
        : Article.IsRead ? OdsBreakdown.Tone("outline")
        : OdsBreakdown.Tone("info");

    private string AccentSoftColor => !IsUpdatedSinceRead && Article.IsRead
        ? "var(--mud-palette-action-default-hover)"
        : $"color-mix(in srgb, {AccentColor} 14%, transparent)";

    private string Title => TitleOf(Article);

    private string? Lang => LangOf(Article.Language) ?? LangOf(FeedLanguage);

    private IEnumerable<ExistingNewsTag> OwnTags =>
        Article.TagIds.Select(id => TagsById.GetValueOrDefault(id)).OfType<ExistingNewsTag>();

    private IEnumerable<ExistingNewsTag> InheritedTags =>
        Article.FeedTagIds.Where(id => !Article.TagIds.Contains(id))
            .Select(id => TagsById.GetValueOrDefault(id)).OfType<ExistingNewsTag>();

    private IReadOnlyList<RenderFragment?> Meta =>
    [
        Bdi(Article.FeedTitle, LangOf(FeedLanguage)),
        string.IsNullOrWhiteSpace(Article.Author) ? null : Bdi(Article.Author, Lang),
        When(Utc(Article.PublishedAtUtc ?? Article.FirstSeenAtUtc)),
        Article.ImageCount > 0 ? OdsRecordMeta.Text(NewsFeedback.Count(Article.ImageCount, "image", "images")) : null,
    ];

    private IReadOnlyList<OdsCarouselImage> Images =>
        Detail?.Detail is { } detail
            ? [.. detail.Images.OrderBy(i => i.DisplayOrder).Select(i => new OdsCarouselImage(
                i.Id.ToString(), ImageUrl?.Invoke(Article.Id, i.Id), i.AltText, i.Width, i.Height))]
            : [];

    private string ReadingClass => Images.Count > 0 ? "nw-reading has-images" : "nw-reading";

    internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>"12 Nov 2026", in the viewer's local time.</summary>
    internal static string Day(DateTime value) =>
        Utc(value).ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static RenderFragment? Bdi(string? text, string? lang) =>
        string.IsNullOrWhiteSpace(text) ? null : builder =>
        {
            builder.OpenElement(0, "bdi");
            builder.AddAttribute(1, "lang", lang);
            builder.AddContent(2, text);
            builder.CloseElement();
        };

    private static RenderFragment When(DateTimeOffset at) => builder =>
    {
        builder.OpenComponent<OdsRelativeTime>(0);
        builder.AddComponentParameter(1, nameof(OdsRelativeTime.Value), at);
        builder.CloseComponent();
    };

    /// <summary>An absolute time in a <c>&lt;time datetime&gt;</c>, or the fallback text (AC 27: never only a tooltip).</summary>
    private static RenderFragment AbsoluteTime(DateTime? value, string fallback) => builder =>
    {
        if (value is not { } at)
        {
            builder.AddContent(0, fallback);
            return;
        }

        builder.OpenElement(1, "time");
        builder.AddAttribute(2, "datetime", Iso(at));
        builder.AddContent(3, OdsRelativeTime.Absolute(Utc(at)));
        builder.CloseElement();
    };

    private Task RetryDetail() => Detail?.Retrying == true ? Task.CompletedTask : OnRetryDetail.InvokeAsync(Article);

    private Task ShowVersions() => OnShowVersions.InvokeAsync(Article);
}
