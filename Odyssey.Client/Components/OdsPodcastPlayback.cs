using System.Globalization;

namespace Odyssey.Client.Components;

/// <summary>What <see cref="OdsPodcastPlayer"/> draws (design system <c>PodcastPlayer</c> <c>state</c>). Idle is the panel's Play button, not a state.</summary>
public enum OdsPodcastPlayerState
{
    Loading,
    Playing,
    Paused,
    Buffering,

    /// <summary>The browser's autoplay policy refused <c>play()</c>: "Press play to start."</summary>
    Blocked,
    Ended,
}

/// <summary>Why playback failed (design system <c>PODCAST_PLAYER_ERROR</c>; frontend spec #594 §7).</summary>
public enum OdsPodcastPlayerError
{
    Network,
    Removed,
    Decode,
    Forbidden,
    Unsupported,
}

/// <summary>A save problem shown under the player (design system <c>PODCAST_PLAYER_SAVE_NOTE</c>).</summary>
public enum OdsPodcastPlayerSaveNote
{
    /// <summary>Three saves in a row failed.</summary>
    Trouble,

    /// <summary>A save was refused (401 / 403): saving stopped for the session.</summary>
    Off,
}

/// <summary>One playback chip (design system <c>PODCAST_PLAYBACK_STATE</c>): the label carries the meaning.</summary>
public sealed record OdsPodcastPlaybackChipMeta(string Label, string Tone, string Icon);

/// <summary>
/// The in-browser player's shared rules and wording (issue #594; the design system's <c>PodcastPlayer.jsx</c>
/// exports). The keyboard steps and the resume-near-end window are design, so they live here once; the save cadence is
/// behaviour and lives in <c>PodcastPlayerTiming</c>.
/// </summary>
public static class OdsPodcastPlayback
{
    /// <summary>←/→ (and ↓/↑) on the seek slider.</summary>
    public const int SeekStepSeconds = 5;

    /// <summary>PageUp/PageDown on the seek slider.</summary>
    public const int SeekPageSeconds = 60;

    /// <summary>A saved position this close to a KNOWN end resumes from the start.</summary>
    public const int ResumeNearEndSeconds = 5;

    /// <summary>The volume slider's step, in percent.</summary>
    public const int VolumeStep = 5;

    public static readonly OdsPodcastPlaybackChipMeta Playing = new("Playing", "info", "graphic_eq");

    public static readonly OdsPodcastPlaybackChipMeta Paused = new("Paused", "outline", "pause_circle");

    public static readonly OdsPodcastPlaybackChipMeta Listened = new("Listened", "outline", "headphones");

    /// <summary>A finite, positive length. Missing, 0, NaN and Infinity are "length unknown".</summary>
    public static bool LengthKnown(double? seconds) =>
        seconds is { } s && double.IsFinite(s) && s > 0;

    /// <summary>"4:05" · "52:07" · "1:13:30"; unknown ⇒ "--:--".</summary>
    public static string Clock(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0)
        {
            return "--:--";
        }

        var s = (long)Math.Floor(value);
        var h = s / 3600;
        var m = s % 3600 / 60;
        var r = s % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{r:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{r:00}");
    }

    /// <summary>"1 hour 2 minutes 5 seconds" · "0 seconds".</summary>
    public static string Spoken(double seconds)
    {
        var s = double.IsFinite(seconds) ? Math.Max(0, (long)Math.Floor(seconds)) : 0;
        var h = s / 3600;
        var m = s % 3600 / 60;
        var r = s % 60;
        static string Part(long n, string one) => string.Create(CultureInfo.InvariantCulture, $"{n} {one}{(n == 1 ? string.Empty : "s")}");
        var parts = new List<string>(3);
        if (h > 0)
        {
            parts.Add(Part(h, "hour"));
        }

        if (m > 0)
        {
            parts.Add(Part(m, "minute"));
        }

        if (r > 0 || parts.Count == 0)
        {
            parts.Add(Part(r, "second"));
        }

        return string.Join(' ', parts);
    }

    /// <summary>The seek slider's <c>aria-valuetext</c>.</summary>
    public static string ValueText(double current, double? total) =>
        LengthKnown(total) ? $"{Spoken(current)} of {Spoken(total!.Value)}" : $"{Spoken(current)} elapsed, length unknown";

    /// <summary>Where Resume starts: the saved position, or 0 when none or within the near-end window of a known length.</summary>
    public static int ResumeAt(int? positionSeconds, double? lengthSeconds)
    {
        var p = Math.Max(0, positionSeconds ?? 0);
        if (p == 0)
        {
            return 0;
        }

        return LengthKnown(lengthSeconds) && p >= lengthSeconds!.Value - ResumeNearEndSeconds ? 0 : p;
    }

    /// <summary>The Audio panel's play action label: "Play" or "Resume from 12:34".</summary>
    public static string PlayActionLabel(int? positionSeconds, double? lengthSeconds) =>
        ResumeAt(positionSeconds, lengthSeconds) is var at && at > 0 ? $"Resume from {Clock(at)}" : "Play";

    /// <summary>The status line per state (the page may pass its own).</summary>
    public static string? StatusText(OdsPodcastPlayerState state) => state switch
    {
        OdsPodcastPlayerState.Loading => "Loading audio…",
        OdsPodcastPlayerState.Buffering => "Buffering…",
        OdsPodcastPlayerState.Blocked => "Press play to start.",
        OdsPodcastPlayerState.Ended => "Finished.",
        _ => null,
    };

    /// <summary>The error block's text. <paramref name="contentTypeLabel"/> names the type in the unsupported case.</summary>
    public static string ErrorText(OdsPodcastPlayerError error, string? contentTypeLabel = null) => error switch
    {
        OdsPodcastPlayerError.Removed => "This episode’s audio is no longer on the server.",
        OdsPodcastPlayerError.Decode => "This audio file couldn’t be played.",
        OdsPodcastPlayerError.Forbidden => "You no longer have access to this audio.",
        OdsPodcastPlayerError.Unsupported => string.IsNullOrEmpty(contentTypeLabel)
            ? "This browser can’t play this audio format. Save it to your device instead."
            : $"This browser can’t play this audio format ({contentTypeLabel}). Save it to your device instead.",
        _ => "Couldn’t load the audio. Check your connection and try again.",
    };

    /// <summary>Only a network failure offers Try again.</summary>
    public static bool OffersRetry(OdsPodcastPlayerError error) => error == OdsPodcastPlayerError.Network;

    public static string SaveNoteText(OdsPodcastPlayerSaveNote note) => note switch
    {
        OdsPodcastPlayerSaveNote.Off => "Your place isn’t being saved.",
        _ => "Couldn’t save your place.",
    };
}
