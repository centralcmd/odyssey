using System.Net;

namespace Odyssey.Core.Journal;

/// <summary>
/// A series write that cannot go ahead as sent (issue #519 §9; v4 §3): a <c>409</c> whose <see cref="Code"/> tells a
/// stale edit from changed consent and from a series another writer holds. Messages are fixed text and the extensions
/// carry counts only.
/// </summary>
public sealed class CalendarSeriesConflictException : DomainException
{
    /// <summary>The <c>expectedVersion</c> sent is not the series' version: someone else edited the series.</summary>
    public const string VersionConflict = "calendar-series-version-conflict";

    /// <summary>The edit would discard more overrides or exclusions than the caller's <c>confirmedDrops</c>.</summary>
    public const string DropsChanged = "calendar-series-drops-changed";

    /// <summary>Another writer held the series (a lock wait, a deadlock, or a key-index race) past the retries.</summary>
    public const string Busy = "calendar-series-busy";

    private readonly string code;
    private readonly Dictionary<string, object?>? extensions;

    private CalendarSeriesConflictException(string code, string message, Dictionary<string, object?>? extensions = null)
        : base(message)
    {
        this.code = code;
        this.extensions = extensions;
    }

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => code;

    public override IReadOnlyDictionary<string, object?>? Extensions => extensions;

    public static CalendarSeriesConflictException Stale() =>
        new(VersionConflict, "This series was changed by someone else since you opened it. Reload it and try again.");

    public static CalendarSeriesConflictException Drops(int overrides, int exclusions) =>
        new(DropsChanged, "This edit now discards more separately edited or deleted occurrences than you confirmed.",
            new Dictionary<string, object?>
            {
                ["droppedOverrideCount"] = overrides,
                ["droppedExclusionCount"] = exclusions,
            });

    public static CalendarSeriesConflictException SeriesBusy() =>
        new(Busy, "This series is being changed by another request. Try again in a moment.");
}
