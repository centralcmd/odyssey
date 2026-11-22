using System.Globalization;
using Odyssey.Client.Pages.Storage;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>A value in two forms: what the eye reads (aria-hidden) and what a screen reader says once (issue #559 §3.2.3).</summary>
/// <param name="Visible">The abbreviation shown ("52 min", "S2 · E14", "12:04").</param>
/// <param name="Spoken">The sr-only form ("52 minutes", "Season 2, episode 14").</param>
/// <param name="Iso">An ISO 8601 duration for <c>&lt;time datetime&gt;</c>, when the value is one.</param>
public sealed record PodcastSpoken(string Visible, string Spoken, string? Iso = null);

/// <summary>
/// The podcast pages' formatting (issue #559 §4.1 <c>PodcastFormat</c>; design system <c>podcasts-data.js</c>
/// <c>podDuration</c> · <c>podClock</c> · <c>podSeasonEpisode</c> · <c>podType</c>): every visible form with its spoken
/// one, so an abbreviation is read once.
/// </summary>
public static class PodcastFormat
{
    /// <summary>"52 min" / "1 h 13 min" / "45 s", spoken "52 minutes" / "1 hour 13 minutes"; null without a duration.</summary>
    public static PodcastSpoken? Duration(int? seconds)
    {
        if (seconds is not { } total || total < 0)
        {
            return null;
        }

        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        var visible = h > 0 ? $"{h} h {m} min" : m > 0 ? $"{m} min" : $"{s} s";
        var spoken = string.Join(' ', new[]
        {
            h > 0 ? Unit(h, "hour") : null,
            m > 0 ? Unit(m, "minute") : null,
            h == 0 && m == 0 ? Unit(s, "second") : null,
        }.Where(p => p is not null));
        return new PodcastSpoken(visible, spoken, Iso(h, m, s));
    }

    /// <summary>A chapter's start: "12:04" / "1:02:09", spoken "12 minutes 4 seconds".</summary>
    public static PodcastSpoken Clock(long startMs)
    {
        var total = (int)Math.Clamp(startMs / 1000, 0, int.MaxValue);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        var visible = h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
        var spoken = string.Join(' ', new[]
        {
            h > 0 ? Unit(h, "hour") : null,
            Unit(m, "minute"),
            s > 0 ? Unit(s, "second") : null,
        }.Where(p => p is not null));
        return new PodcastSpoken(visible, spoken, Iso(h, m, s, alwaysMinutes: true));
    }

    /// <summary>"S2 · E14" spoken "Season 2, episode 14"; "E62" spoken "Episode 62"; null when neither is set.</summary>
    public static PodcastSpoken? SeasonEpisode(int? season, int? episode)
    {
        if (season is null && episode is null)
        {
            return null;
        }

        var visible = string.Join(" · ", new[]
        {
            season is { } sv ? $"S{sv.ToString(CultureInfo.InvariantCulture)}" : null,
            episode is { } ev ? $"E{ev.ToString(CultureInfo.InvariantCulture)}" : null,
        }.Where(p => p is not null));
        var spoken = string.Join(", ", new[]
        {
            season is { } ss ? $"Season {ss.ToString(CultureInfo.InvariantCulture)}" : null,
            episode is { } es ? $"{(season is null ? "Episode" : "episode")} {es.ToString(CultureInfo.InvariantCulture)}" : null,
        }.Where(p => p is not null));
        return new PodcastSpoken(visible, spoken);
    }

    /// <summary>The stored type as a word: MP3, AAC, M4A, Ogg, FLAC; anything else "Audio".</summary>
    public static string Type(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => "MP3",
        "audio/aac" or "audio/aacp" => "AAC",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "M4A",
        "audio/ogg" or "audio/opus" => "Ogg",
        "audio/flac" or "audio/x-flac" => "FLAC",
        _ => "Audio",
    };

    /// <summary>A byte count in 1024 steps ("47 MB", "1.2 GB").</summary>
    public static string Size(long bytes) => StorageHealth.Bytes(Math.Max(0, bytes));

    /// <summary>"On the server · 47 MB · MP3." — the Stored status line.</summary>
    public static string StoredLine(PodcastEpisodeAudioInfo audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var parts = new List<string> { "On the server" };
        if (audio.SizeBytes is { } size)
        {
            parts.Add(Size(size));
        }

        parts.Add(Type(audio.ContentType));
        return string.Join(" · ", parts) + ".";
    }

    /// <summary>The chapter-source note: "From the podcast feed" · "From the publisher’s chapter file" · "From the audio file".</summary>
    public static string? ChapterSource(PodcastChapterSource? source) => source switch
    {
        PodcastChapterSource.Feed => "From the podcast feed",
        PodcastChapterSource.Json => "From the publisher’s chapter file",
        PodcastChapterSource.AudioFile => "From the audio file",
        _ => null,
    };

    /// <summary>A local clock time ("14:32") for the cooldown line; the reader's culture and time zone.</summary>
    public static string LocalTime(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("t", CultureInfo.CurrentCulture);

    private static string Unit(int value, string unit) =>
        $"{value.ToString(CultureInfo.InvariantCulture)} {unit}{(value == 1 ? string.Empty : "s")}";

    private static string Iso(int h, int m, int s, bool alwaysMinutes = false)
    {
        var text = "PT";
        if (h > 0)
        {
            text += $"{h.ToString(CultureInfo.InvariantCulture)}H";
        }

        if (m > 0 || alwaysMinutes)
        {
            text += $"{m.ToString(CultureInfo.InvariantCulture)}M";
        }

        if (s > 0 || (h == 0 && m == 0 && !alwaysMinutes))
        {
            text += $"{s.ToString(CultureInfo.InvariantCulture)}S";
        }

        return text;
    }
}
