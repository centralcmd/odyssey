namespace Odyssey.Client.Components;

/// <summary>
/// The one wording for "how far away is this day", shared by every surface that phrases a date
/// relatively rather than absolutely.
/// </summary>
/// <remarks>
/// It exists because two copies appeared in one change — the contracts header signal's "Starting
/// soon" line and the charge row's own caption — and two copies of a phrasing rule drift into two
/// phrasings of the same fact on one screen. Note the asymmetry is deliberate rather than an
/// oversight: <see cref="Ahead"/> has "tomorrow" while <see cref="Ago"/> has no "yesterday", because
/// the backward-looking surfaces read "1 day ago" in the design system.
/// </remarks>
public static class OdsRelativeDay
{
    /// <summary>"today" · "tomorrow" · "in 12 days" — a day at or after today.</summary>
    public static string Ahead(int days) => days switch
    {
        <= 0 => "today",
        1 => "tomorrow",
        var d => $"in {d} days",
    };

    /// <summary>"today" · "1 day ago" · "12 days ago" — a day at or before today.</summary>
    public static string Ago(int days) => days switch
    {
        <= 0 => "today",
        1 => "1 day ago",
        var d => $"{d} days ago",
    };
}
