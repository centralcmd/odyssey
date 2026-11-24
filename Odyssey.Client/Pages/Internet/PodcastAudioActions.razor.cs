using System.Globalization;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>An audio action on one episode (issue #559 §3.2.5).</summary>
public enum PodcastAudioAction { Download, Cancel, Save, Remove, Retry, Clear, Play }

public partial class PodcastAudioActions
{
    [Parameter, EditorRequired] public PodcastEpisodeSummary Episode { get; set; } = default!;

    /// <summary><c>podcast-audio.create</c>: Download to server and Try again.</summary>
    [Parameter] public bool CanRequest { get; set; }

    /// <summary><c>podcast-audio.delete</c>: Cancel, Clear and Remove from server.</summary>
    [Parameter] public bool CanDelete { get; set; }

    /// <summary>The action whose request is in flight on this episode, if any.</summary>
    [Parameter] public PodcastAudioAction? Pending { get; set; }

    /// <summary>Three polls in a row failed: the in-flight line shows.</summary>
    [Parameter] public bool PollTrouble { get; set; }

    /// <summary>The page stopped checking a waiting download after 30 minutes.</summary>
    [Parameter] public bool WaitStopped { get; set; }

    /// <summary>The reader's "now", from the page's clock, to tell whether the cooldown still runs.</summary>
    [Parameter] public DateTimeOffset Now { get; set; }

    /// <summary>The reader's time zone, for the "Available again at" clock time.</summary>
    [Parameter] public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    [Parameter] public EventCallback<PodcastAudioAction> OnAction { get; set; }

    [Parameter] public EventCallback OnCheckAgain { get; set; }

    /// <summary>The page's playback session; the player renders here while it holds this episode (#594).</summary>
    [Parameter] public PodcastPlaybackSession? Session { get; set; }

    /// <summary>This browser cannot play the episode's audio type: Play is removed and an info line says so.</summary>
    [Parameter] public bool Unsupported { get; set; }

    /// <summary>The podcast's language, for the player's title.</summary>
    [Parameter] public string? Lang { get; set; }

    /// <summary>The info line naming an unsupported format; the page moves focus here when Save to device is absent.</summary>
    public static string UnsupportedId(Guid episodeId) => $"pod-unsupported-{episodeId}";

    /// <summary>The status line: focus lands here when the audio went away mid-listen.</summary>
    public static string StatusId(Guid episodeId) => $"pod-status-{episodeId}";

    public static string PlayerToggleId(Guid episodeId) => $"pod-player-toggle-{episodeId}";

    public static string PlayerRetryId(Guid episodeId) => $"pod-player-retry-{episodeId}";

    public static string PlayerAlertId(Guid episodeId) => $"pod-player-alert-{episodeId}";

    private bool PlayerHere => Session?.Is(Episode.Id) == true;

    /// <summary>The button id an action renders under — the page moves focus to it after a state change.</summary>
    public static string ActionId(PodcastAudioAction action, Guid episodeId) =>
        $"pod-{action.ToString().ToLowerInvariant()}-{episodeId}";

    private sealed record ActionSpec(PodcastAudioAction Kind, string Label, string Icon, OdsButtonVariant Variant, bool Cooled = false);

    private string Title => PodcastEpisodeCard.TitleOf(Episode);

    private string CooldownId => $"pod-cd-{Episode.Id}";

    /// <summary>When the cooldown ends, while it still runs.</summary>
    private DateTime? CooldownUntil =>
        Episode.Audio.RequestAvailableAtUtc is { } at && new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) > Now ? at : null;

    private bool ShowsCooldown =>
        CanRequest && Episode.Audio.State is PodcastAudioState.None or PodcastAudioState.Failed;

    private string CooldownText => CooldownUntil is { } at ? PodcastFormat.LocalTime(at, TimeZone) : string.Empty;

    private string StatusText => Episode.Audio.State == PodcastAudioState.Stored
        ? PodcastFormat.StoredLine(Episode.Audio) + (Episode.Audio.RequestedBy == PodcastAudioRequestedBy.Auto ? " Downloaded automatically." : string.Empty)
        : OdsPodcastAudioState.StatusLine(Episode.Audio);

    private IReadOnlyList<ActionSpec> Actions
    {
        get
        {
            var cooled = CooldownUntil is not null;
            var list = new List<ActionSpec>();
            switch (Episode.Audio.State)
            {
                case PodcastAudioState.None when CanRequest:
                    list.Add(new(PodcastAudioAction.Download, "Download to server", "cloud_download", OdsButtonVariant.Filled, cooled));
                    break;
                case PodcastAudioState.Queued or PodcastAudioState.Deferred when CanDelete:
                    list.Add(new(PodcastAudioAction.Cancel, "Cancel", "close", OdsButtonVariant.Outlined));
                    break;
                case PodcastAudioState.Stored:
                    // Play / Resume leads while this card has no player (#594 §3); Save to device turns outlined.
                    if (!PlayerHere && !Unsupported)
                    {
                        list.Add(new(PodcastAudioAction.Play, OdsPodcastPlayback.PlayActionLabel(Episode.PlaybackPositionSeconds, Episode.DurationSeconds), "play_arrow", OdsButtonVariant.Filled));
                    }

                    list.Add(new(PodcastAudioAction.Save, "Save to device", "download", OdsButtonVariant.Outlined));
                    if (CanDelete)
                    {
                        list.Add(new(PodcastAudioAction.Remove, "Remove from server", "delete", OdsButtonVariant.Outlined));
                    }

                    break;
                case PodcastAudioState.Failed:
                    if (CanRequest)
                    {
                        list.Add(new(PodcastAudioAction.Retry, "Try again", "refresh", OdsButtonVariant.Filled, cooled));
                    }

                    if (CanDelete)
                    {
                        list.Add(new(PodcastAudioAction.Clear, "Clear", "close", OdsButtonVariant.Outlined));
                    }

                    break;
            }

            return list;
        }
    }

    /// <summary>A cooled action, and every action but Save while another is pending, is aria-disabled (focus kept): Play waits for a removal in flight.</summary>
    private bool IsLocked(ActionSpec action) =>
        action.Cooled || (Pending is { } pending && pending != action.Kind && action.Kind != PodcastAudioAction.Save);

    private Task PressAsync(ActionSpec action) =>
        IsLocked(action) || Pending == action.Kind ? Task.CompletedTask : OnAction.InvokeAsync(action.Kind);

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
