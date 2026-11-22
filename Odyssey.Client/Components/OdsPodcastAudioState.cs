using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Components;

/// <summary>
/// How an episode's <see cref="PodcastAudioState"/> reads (issue #559 §3.2.5; the design system's
/// <c>PODCAST_AUDIO_STATE</c> registry, components/PodcastAudioStateChip). The meaning lives in <see cref="Label"/>, never
/// in the tone or the glyph, which are decorative.
/// </summary>
/// <param name="Label">Visible state text.</param>
/// <param name="Tone">Chip tone class.</param>
/// <param name="Icon">Material Icons ligature (decorative).</param>
/// <param name="Unknown">True for a member this client does not know (a newer server).</param>
public sealed record OdsPodcastAudioStateMeta(string Label, string Tone, string Icon, bool Unknown = false);

/// <summary>
/// The podcast audio state registry: where an episode's audio stands ON THE SERVER. Tones are semantic only — success
/// (<c>income</c>) for On the server, <c>info</c> for the two in-flight states, <c>warning</c> for Waiting for space,
/// <c>error</c> for Download failed and the neutral <c>outline</c> for Not downloaded, the ordinary state of most episodes.
/// </summary>
public static class OdsPodcastAudioState
{
    private static readonly IReadOnlyDictionary<PodcastAudioState, OdsPodcastAudioStateMeta> Registry =
        new Dictionary<PodcastAudioState, OdsPodcastAudioStateMeta>
        {
            [PodcastAudioState.None] = new("Not downloaded", "outline", "cloud_off"),
            [PodcastAudioState.Queued] = new("Queued", "info", "schedule"),
            [PodcastAudioState.Downloading] = new("Downloading", "info", "downloading"),
            [PodcastAudioState.Stored] = new("On the server", "income", "cloud_done"),
            [PodcastAudioState.Failed] = new("Download failed", "error", "error_outline"),
            [PodcastAudioState.Deferred] = new("Waiting for space", "warning", "hourglass_top"),
        };

    /// <summary>The known members, in the design system's reading order.</summary>
    public static readonly IReadOnlyList<PodcastAudioState> Order =
    [
        PodcastAudioState.None,
        PodcastAudioState.Queued,
        PodcastAudioState.Downloading,
        PodcastAudioState.Stored,
        PodcastAudioState.Failed,
        PodcastAudioState.Deferred,
    ];

    /// <summary>The display row. An unrecognised member fails NEUTRALLY under its own name, never as a guessed state.</summary>
    public static OdsPodcastAudioStateMeta Meta(PodcastAudioState state) =>
        Registry.TryGetValue(state, out var meta) ? meta : new(state.ToString(), "outline", "help", Unknown: true);

    /// <summary>The states the Podcasts page polls for: Queued, Downloading and Deferred. An unknown member is never polled.</summary>
    public static bool InFlight(PodcastAudioState state) =>
        state is PodcastAudioState.Queued or PodcastAudioState.Downloading or PodcastAudioState.Deferred;

    /// <summary><c>failureKind</c> → the reason completing "Couldn't download: ‹reason›."; unknown ⇒ "an unknown error".</summary>
    public static string FailureText(PodcastAudioFailureKind? kind) => kind switch
    {
        PodcastAudioFailureKind.NotAudio => "the publisher didn’t send an audio file",
        PodcastAudioFailureKind.TooLarge => "the file is larger than the server allows",
        PodcastAudioFailureKind.HttpError => "the publisher’s server returned an error",
        PodcastAudioFailureKind.Timeout => "the publisher’s server was too slow",
        PodcastAudioFailureKind.Refused => "the address isn’t allowed",
        PodcastAudioFailureKind.Gone => "the episode is no longer available from the publisher",
        _ => "an unknown error",
    };

    /// <summary>The card's one-sentence status line for an episode's <c>audio</c> block (Stored: the host adds size and type).</summary>
    public static string StatusLine(PodcastEpisodeAudioInfo audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.State switch
        {
            PodcastAudioState.None => "Not downloaded to the server.",
            PodcastAudioState.Queued => "Queued for download to the server.",
            PodcastAudioState.Downloading => "Downloading to the server…",
            PodcastAudioState.Deferred => audio.DeferredReason == PodcastAudioDeferredReason.DiskHeadroom
                ? "Waiting: the server is low on disk space."
                : "Waiting: the audio storage limit is reached.",
            PodcastAudioState.Failed => $"Couldn’t download: {FailureText(audio.FailureKind)}.",
            PodcastAudioState.Stored => "On the server.",
            _ => $"Audio: {Meta(audio.State).Label}.",
        };
    }
}
