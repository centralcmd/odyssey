using System.Globalization;

namespace Odyssey.Client.Pages.Finance.ExchangeRateSync;

/// <summary>
/// How the exchange-rate sync surfaces write a moment: an absolute local time, and a relative phrase
/// ("in 8 hours", "4 hours ago") computed against an injected clock, so a test can pin it and a render
/// recomputes it (issue #351 §3.B, FE N2).
/// </summary>
public static class ExchangeRateSyncTime
{
    /// <summary>"23 Oct, 19:00" in the viewer's local time.</summary>
    public static string Absolute(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"19:00" in the viewer's local time.</summary>
    public static string Clock(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

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
}
