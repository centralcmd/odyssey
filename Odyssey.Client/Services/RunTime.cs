using System.Globalization;

namespace Odyssey.Client.Services;

/// <summary>
/// How a background job's surfaces write a moment: an absolute local time, and a relative phrase ("in 8 hours",
/// "4 hours ago") computed against a given clock — the overview's <c>generatedAtUtc</c>, or an injected
/// <see cref="TimeProvider"/> — so a test can pin it and a render recomputes it. Shared by Background jobs,
/// Currencies' exchange-rate sync and Storage health (issue #410 F13; formerly <c>ExchangeRateSyncTime</c>).
/// </summary>
public static class RunTime
{
    /// <summary>"23 Oct, 19:00" in the viewer's local time.</summary>
    public static string Absolute(DateTime utc) =>
        Local(utc).ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"5 Nov 2026, 10:48" in the viewer's local time: the visible, unambiguous form (#410 F9, X6).</summary>
    public static string Full(DateTime utc) =>
        Local(utc).ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"19:00" in the viewer's local time.</summary>
    public static string Clock(DateTime utc) =>
        Local(utc).ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>A date-only value, "2 Jan 2026".</summary>
    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string Relative(DateTime utc, DateTimeOffset now)
    {
        var delta = now.UtcDateTime - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var span = delta.Duration();
        var (count, unit) = span.TotalMinutes < 1 ? (Math.Max(1, (int)Math.Round(span.TotalSeconds)), "second")
            : span.TotalHours < 1 ? ((int)Math.Round(span.TotalMinutes), "minute")
            : span.TotalDays < 1 ? ((int)Math.Round(span.TotalHours), "hour")
            : ((int)Math.Round(span.TotalDays), "day");
        var text = $"{count} {unit}{(count == 1 ? string.Empty : "s")}";
        return delta >= TimeSpan.Zero ? $"{text} ago" : $"in {text}";
    }

    private static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
