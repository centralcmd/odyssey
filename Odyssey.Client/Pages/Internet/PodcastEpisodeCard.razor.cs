using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>An episode's detail as the page holds it for the card's lifetime (issue #559 §3.2.4).</summary>
/// <param name="Phase">The detail state (the article body phases are reused).</param>
/// <param name="Detail">The detail, once <see cref="NewsBodyPhase.Ready"/>.</param>
/// <param name="Slow">Still loading after 1 s: "Loading episode…" is announced.</param>
public sealed record PodcastEpisodeDetailState(NewsBodyPhase Phase, PodcastEpisodeDetail? Detail = null, bool Slow = false);

/// <summary>The card's one action alert (issue #559 §7): replaced by the next, cleared by another action or a poll.</summary>
/// <param name="Text">The message.</param>
/// <param name="LinkLabel">A link's text, when the message points somewhere (the archived podcast's Feeds page).</param>
/// <param name="LinkHref">That link's relative route.</param>
public sealed record PodcastCardAlert(string Text, string? LinkLabel = null, string? LinkHref = null);

public partial class PodcastEpisodeCard
{
    public const string ErrorText = "Couldn’t load this episode.";

    public const string StorageText = "The episode’s show notes and chapters aren’t available right now.";

    public const string ShowNotesUnavailableText = "The show notes aren’t available right now.";

    public const string ShowNotesOmittedText = "These show notes are too large or complex to show here.";

    [Parameter, EditorRequired] public PodcastEpisodeSummary Episode { get; set; } = default!;

    /// <summary>The podcast's language from the catalogue (§3.2.4); null sets none.</summary>
    [Parameter] public string? FeedLanguage { get; set; }

    /// <summary>The artwork's 200 px rendition URL, built from the id only; null without stored artwork.</summary>
    [Parameter] public string? ArtworkUrl { get; set; }

    /// <summary>Builds a show-note image's <c>&lt;img src&gt;</c> from the two ids only.</summary>
    [Parameter] public Func<Guid, Guid, string>? ImageUrl { get; set; }

    /// <summary>The row menu — navigation only, each item the caller can use.</summary>
    [Parameter] public IReadOnlyList<OdsMenuItem> Menu { get; set; } = [];

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OnToggle { get; set; }

    [Parameter] public PodcastEpisodeDetailState? Detail { get; set; }

    [Parameter] public PodcastCardAlert? CardAlert { get; set; }

    [Parameter] public bool CanRequest { get; set; }

    [Parameter] public bool CanDelete { get; set; }

    [Parameter] public PodcastAudioAction? Pending { get; set; }

    [Parameter] public bool PollTrouble { get; set; }

    [Parameter] public bool WaitStopped { get; set; }

    [Parameter] public DateTimeOffset Now { get; set; }

    [Parameter] public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    [Parameter] public EventCallback<PodcastAudioAction> OnAction { get; set; }

    [Parameter] public EventCallback OnCheckAgain { get; set; }

    [Parameter] public EventCallback<PodcastEpisodeSummary> OnRetryDetail { get; set; }

    /// <summary>The page's playback session (issue #594).</summary>
    [Parameter] public PodcastPlaybackSession? Session { get; set; }

    /// <summary>This browser cannot play the episode's audio type.</summary>
    [Parameter] public bool Unsupported { get; set; }

    /// <summary>The collapsed card's play/pause button.</summary>
    public static string HeaderToggleId(Guid episodeId) => $"pod-head-toggle-{episodeId}";

    private bool PlayingHere => Session?.Is(Episode.Id) == true;

    /// <summary>
    /// The collapsed card's Playing / Paused chip and toggle: only while there is something to play or pause — not after
    /// the end and not with an error, which the open card's player explains.
    /// </summary>
    private bool ShowsHeaderPlayback => PlayingHere && !Open && Session!.Error is null && Session.State != OdsPodcastPlayerState.Ended;

    private bool HeaderPlaying => Session is { } s && (s.PlayingNow || s.State == OdsPodcastPlayerState.Loading);

    public static string CardId(Guid id) => $"pod-{id}";

    public static string RetryId(Guid id) => $"pod-retry-{id}";

    /// <summary>The title, or "(untitled)".</summary>
    public static string TitleOf(PodcastEpisodeSummary episode) =>
        string.IsNullOrWhiteSpace(episode.Title) ? "(untitled)" : episode.Title;

    private string Title => TitleOf(Episode);

    private string? Lang => NewsArticleCard.LangOf(FeedLanguage);

    private PodcastSpoken? Duration => PodcastFormat.Duration(Episode.DurationSeconds);

    private PodcastSpoken? SeasonEpisode => PodcastFormat.SeasonEpisode(Episode.SeasonNumber, Episode.EpisodeNumber);

    private string? EpisodeTypeLabel => Episode.EpisodeType switch
    {
        PodcastEpisodeType.Trailer => "Trailer",
        PodcastEpisodeType.Bonus => "Bonus",
        _ => null,
    };

    private string StoredSize =>
        string.Join(" · ", new[] { Episode.Audio.SizeBytes is { } b ? PodcastFormat.Size(b) : null, PodcastFormat.Type(Episode.Audio.ContentType) }
            .Where(p => p is not null));

    /// <summary>The mark and the card take the audio state's tone; the chip's text says the state.</summary>
    private string AccentColor => OdsBreakdown.Tone(OdsPodcastAudioState.Meta(Episode.Audio.State).Tone);

    private string AccentSoftColor => OdsPodcastAudioState.Meta(Episode.Audio.State).Tone == "outline"
        ? "var(--mud-palette-action-default-hover)"
        : $"color-mix(in srgb, {AccentColor} 14%, transparent)";

    private IReadOnlyList<RenderFragment?> Meta =>
    [
        Bdi(Episode.FeedTitle, Lang),
        Episode.PublishedAtUtc is { } published ? When(NewsArticleCard.Utc(published)) : null,
        Duration is { } d ? Spoken(d) : null,
        SeasonEpisode is { } se ? Spoken(se) : null,
        Episode.HasChapters ? OdsRecordMeta.Text("Has chapters") : null,
    ];

    private IReadOnlyList<OdsCarouselImage> Images =>
        Detail?.Detail is { } detail
            ? [.. detail.Images.OrderBy(i => i.DisplayOrder).Select(i => new OdsCarouselImage(
                i.Id.ToString(), ImageUrl?.Invoke(Episode.Id, i.Id), i.AltText, i.Width, i.Height))]
            : [];

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static RenderFragment? Bdi(string? text, string? lang) =>
        string.IsNullOrWhiteSpace(text) ? null : builder =>
        {
            builder.OpenElement(0, "bdi");
            builder.AddAttribute(1, "lang", lang);
            builder.AddContent(2, text);
            builder.CloseElement();
        };

    /// <summary>The visible abbreviation aria-hidden, then its spoken form sr-only.</summary>
    private static RenderFragment Spoken(PodcastSpoken value) => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "aria-hidden", "true");
        builder.AddContent(2, value.Visible);
        builder.CloseElement();
        builder.OpenElement(3, "span");
        builder.AddAttribute(4, "class", "odc-sr-only");
        builder.AddContent(5, value.Spoken);
        builder.CloseElement();
    };

    private static RenderFragment When(DateTimeOffset at) => builder =>
    {
        builder.OpenComponent<OdsRelativeTime>(0);
        builder.AddComponentParameter(1, nameof(OdsRelativeTime.Value), at);
        builder.CloseComponent();
    };

    private static RenderFragment AbsoluteTime(DateTime? value, string fallback) => builder =>
    {
        if (value is not { } at)
        {
            builder.AddContent(0, fallback);
            return;
        }

        builder.OpenElement(1, "time");
        builder.AddAttribute(2, "datetime", Iso(at));
        builder.AddContent(3, OdsRelativeTime.Absolute(NewsArticleCard.Utc(at)));
        builder.CloseElement();
    };
}
