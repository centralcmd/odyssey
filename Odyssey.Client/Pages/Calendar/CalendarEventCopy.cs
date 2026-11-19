namespace Odyssey.Client.Pages.Calendar;

/// <summary>
/// The event dialog's and the calendar page's wording for time zones and recurrence exceptions (issue #520 §3, v3–v6
/// notes). Copy is the spec's, or the design system's (<c>AddCalendarEventModal.jsx</c>) where the spec is silent; every
/// count is plural-correct and a zero clause is dropped. Nothing here takes a value from an <c>.ics</c> file.
/// </summary>
internal static class CalendarEventCopy
{
    public const string ZoneError = "Choose a time zone from the list.";

    public const string FloatingHelp =
        "Shown at the same clock time everywhere. Choose a zone to keep its local time across daylight-saving changes.";

    public const string FloatingOccurrence = "No time zone (floating). Set a zone on the series to change it.";

    public const string OccurrenceOtherViewers = "People in other time zones will see this occurrence at their own local time.";

    public const string OccurrenceBecomesOverride = "This occurrence will be edited separately from its series.";

    public const string EditedSeparately = "Edited separately";

    public const string OverrideHint =
        "Changing the series' title, description or place doesn't overwrite this occurrence. A schedule change can discard it if it no longer falls on the new schedule.";

    public const string DeleteOccurrenceSentence =
        "It stays deleted when the series' details change. A schedule change that no longer includes this date also discards the deletion.";

    public const string ConfirmHeading = "Discard separately edited occurrences?";

    public const string ConfirmHeadingAgain = "More occurrences are affected now";

    public const string PreviewFailed = "Couldn't check what this change affects. Your changes are not saved. Try again.";

    public const string RateLimited = "Too many requests right now. Your changes are not saved. Wait a moment and try again.";

    public const string Forbidden = "You no longer have permission to edit this series.";

    public const string Busy = "Someone else is saving this series. Try again in a moment.";

    public const string Conflict = "This series changed while you were editing. Your changes are not saved.";

    public const string NotFound = "This event no longer exists.";

    public const string DetachedEdit = "This occurrence had become a separate event; your change was saved to that event.";

    public const string DetachedDelete = "This occurrence had become a separate event; it was deleted on its own.";

    public const string MovedOverride = "Moved. This occurrence is now edited separately from its series.";

    /// <summary>The "From the series" help line, before the Edit series… link (issue #522 §3.2).</summary>
    public const string LinksBelongToSeries = "Attachments belong to the whole series.";

    /// <summary>Said once the links arrive, only after a slow load (v3 §C).</summary>
    public const string LinksLoaded = "Attachments loaded.";

    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    public static string DefaultZoneHelp(string zone) =>
        $"Defaults to your device's time zone ({zone}). The zone is stored with the event.";

    public static string ZoneChanged(string startTime, string zone, string viewerTime, string viewerDay) =>
        $"Still {startTime} in {zone}; that's {viewerTime}, {viewerDay} your time.";

    public static string SeriesReadAs(string zone) => $"All occurrences still in this series will be read as {zone}.";

    public static string GapHint(string zone, string savedTime) =>
        $"This time doesn't exist on that day in {zone}; Odyssey will save it as {savedTime}.";

    public static string AmbiguousHint(string zone) =>
        $"This time occurs twice on that day in {zone}; Odyssey will save the first one.";

    public static string Exclusions(int n) => $"{Plural(n, "occurrence", "occurrences")} deleted from this series.";

    /// <summary>"2 occurrences you edited separately and 1 occurrence you deleted" — a zero clause omitted.</summary>
    private static (string Text, bool Singular) DropSubject(int overrides, int exclusions)
    {
        var parts = new List<string>();
        if (overrides > 0)
            parts.Add($"{Plural(overrides, "occurrence", "occurrences")} you edited separately");
        if (exclusions > 0)
            parts.Add($"{Plural(exclusions, "occurrence", "occurrences")} you deleted");
        return (string.Join(" and ", parts), parts.Count == 1 && overrides + exclusions == 1);
    }

    /// <summary>The confirm step's body (§3 step 3).</summary>
    public static string ConfirmBody(int overrides, int exclusions)
    {
        var (text, singular) = DropSubject(overrides, exclusions);
        return Capitalise($"{text} no longer {(singular ? "falls" : "fall")} on the new schedule and will be discarded.");
    }

    /// <summary>The Warning toast after a save that discarded exceptions; it replaces "Series updated." (§3 step 4).</summary>
    public static string DropToast(int overrides, int exclusions)
    {
        var (text, singular) = DropSubject(overrides, exclusions);
        return $"Series updated. {Capitalise(text)} {(singular ? "was" : "were")} discarded because "
            + $"{(singular ? "it no longer falls" : "they no longer fall")} on the new schedule.";
    }

    /// <summary>The detached-rows line, one copy for the occurrence and the series paths (v6 F5).</summary>
    public static string DetachedNow(int n) => n == 1
        ? "1 past occurrence that duplicated another start is now a separate event."
        : $"{n} past occurrences that duplicated another start are now separate events.";

    /// <summary>The confirm step's detached-rows line (v4 §3).</summary>
    public static string DetachedWill(int n) => n == 1
        ? "1 past occurrence that duplicated another start will become a separate event."
        : $"{n} past occurrences that duplicated another start will become separate events.";

    /// <summary>The DST-adjusted save (v3 §F, v4 §7): worded from the server's stored value.</summary>
    public static string Adjusted(string savedTime, string enteredTime, string date, string zone, bool repeats) =>
        $"Saved as {savedTime}. {enteredTime} doesn't exist on {date} in {zone}."
        + (repeats ? " Other occurrences on that date each year are saved the same way." : string.Empty);

    public static string AdjustedCount(int n) =>
        $"{Plural(n, "occurrence", "occurrences")} fell in a daylight-saving gap and {(n == 1 ? "was" : "were")} moved forward.";

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
