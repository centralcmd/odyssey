namespace Odyssey.Core.Finance.ExchangeRateSync;

/// <summary>
/// Norwegian time for fetched rates (issue #349 §4): an observation for date <c>d</c> is stamped at
/// 16:00 Europe/Oslo — when Norges Bank publishes — converted to UTC, so a rate is not in force before it
/// is published. The zone is resolved on each use rather than at startup: a host without zone data still
/// starts, and the run fails with <c>TimeZoneUnavailable</c>.
/// </summary>
public static class ExchangeRateSyncClock
{
    /// <summary>Europe/Oslo, or null when the host has no zone data for it.</summary>
    public static TimeZoneInfo? Oslo
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ExchangeRateSyncConstants.FetchedRateTimeZone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return null;
            }
        }
    }

    /// <summary>The UTC instant a fetched rate for <paramref name="date"/> takes effect.</summary>
    public static DateTime FetchedAsOf(DateOnly date, TimeZoneInfo oslo) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(ExchangeRateSyncConstants.FetchedRateLocalTime), oslo);

    public static DateOnly TodayIn(TimeZoneInfo oslo, DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), oslo));

    /// <summary>The Oslo calendar date of a stored instant; the UTC date when zone data is missing.</summary>
    public static DateOnly DateIn(TimeZoneInfo? oslo, DateTime utc) =>
        oslo is null ? DateOnly.FromDateTime(utc) : TodayIn(oslo, utc);
}

/// <summary>
/// The waits between requests and before a retry, injectable so the fast test tiers do not sleep. A
/// singleton; production uses <see cref="Default"/>.
/// </summary>
public sealed class ExchangeRateSyncPacing(Func<TimeSpan, CancellationToken, Task> delay)
{
    public static readonly ExchangeRateSyncPacing Default = new(Task.Delay);

    public static readonly ExchangeRateSyncPacing None = new((_, _) => Task.CompletedTask);

    public Task Delay(TimeSpan wait, CancellationToken cancellationToken) => delay(wait, cancellationToken);
}
