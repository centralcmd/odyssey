using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Components;

/// <summary>
/// How a news feed's derived <see cref="NewsFeedHealth"/> reads (issue #473 §3.3; the design system's
/// <c>NEWS_FEED_HEALTH</c> registry). The meaning lives in <see cref="Label"/>, never in the tone or the
/// glyph, which are decorative. Tones are semantic only — <c>income</c> (success), <c>warning</c>,
/// <c>error</c>, <c>info</c> (Pending first fetch: waiting, not wrong — the blue Contracts' Upcoming uses) and
/// neutral <c>outline</c> (Paused and Archived).
/// </summary>
/// <param name="Label">Visible health text.</param>
/// <param name="Tone">Chip tone class.</param>
/// <param name="Icon">Material Icons ligature (decorative).</param>
/// <param name="Unknown">True for a member this client does not know (a newer server).</param>
public sealed record OdsNewsFeedHealthMeta(string Label, string Tone, string Icon, bool Unknown = false);

/// <summary>The news feed health registry.</summary>
public static class OdsNewsFeedHealth
{
    private static readonly IReadOnlyDictionary<NewsFeedHealth, OdsNewsFeedHealthMeta> Registry =
        new Dictionary<NewsFeedHealth, OdsNewsFeedHealthMeta>
        {
            [NewsFeedHealth.Pending]      = new("Pending first fetch",   "info",    "schedule"),
            [NewsFeedHealth.Healthy]      = new("Healthy",               "income",  "check_circle"),
            [NewsFeedHealth.Failing]      = new("Failing",               "warning", "sync_problem"),
            [NewsFeedHealth.Gone]         = new("Gone",                  "error",   "link_off"),
            [NewsFeedHealth.Paused]       = new("Paused",                "outline", "pause_circle"),
            [NewsFeedHealth.Archived]     = new("Archived",              "outline", "inventory_2"),
            [NewsFeedHealth.Quarantined]  = new("Quarantined",           "error",   "gpp_maybe"),
            [NewsFeedHealth.QuotaReached] = new("Storage limit reached", "warning", "hourglass_top"),
        };

    /// <summary>The known members, in the design system's reading order.</summary>
    public static readonly IReadOnlyList<NewsFeedHealth> Order =
    [
        NewsFeedHealth.Pending,
        NewsFeedHealth.Healthy,
        NewsFeedHealth.Failing,
        NewsFeedHealth.Gone,
        NewsFeedHealth.Paused,
        NewsFeedHealth.Archived,
        NewsFeedHealth.Quarantined,
        NewsFeedHealth.QuotaReached,
    ];

    /// <summary>
    /// The display row for a health value. An unrecognised member — <see cref="NewsFeedHealth.Unknown"/>,
    /// which the tolerant converter maps a newer server's value to, or an out-of-range ordinal — fails
    /// NEUTRALLY under its own name, never as a guessed state.
    /// </summary>
    public static OdsNewsFeedHealthMeta Meta(NewsFeedHealth health) =>
        Registry.TryGetValue(health, out var meta)
            ? meta
            : new(health.ToString(), "outline", "help", Unknown: true);

    /// <summary>
    /// The states a person has to act on. Reads the shared <see cref="NewsFeedHealthSets.NeedsAttention"/>
    /// rather than restating it, so the header count and the server's <c>health</c> filter agree.
    /// </summary>
    public static bool NeedsAttention(NewsFeedHealth health) => NewsFeedHealthSets.NeedsAttention.Contains(health);
}
