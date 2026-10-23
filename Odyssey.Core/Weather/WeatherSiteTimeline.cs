using Odyssey.Context;

namespace Odyssey.Core.Weather;

/// <summary>
/// Which frozen site owns a date or an instant (issue #348 §3 Local dates, B1 and amendment 3).
///
/// <list type="bullet">
/// <item>Site <i>v</i> owns its first date — <see cref="WeatherLocationSite.AppliedAt"/> converted to site
/// <i>v</i>'s own zone — through the day before site <i>v+1</i>'s first date. The last site is open-ended,
/// and site 1 also owns every date before its first date. The apply day belongs to the new site.</item>
/// <item>An instant is first given the site whose <c>[AppliedAt, next AppliedAt)</c> contains it (site 1
/// before its own <c>AppliedAt</c>); its date is computed in <b>that</b> site's zone. That date's
/// <c>WeatherDay</c> is then read even when the date is owned by another site — so an apply that changes
/// the zone can never make an instant resolve to a date in no zone at all.</item>
/// </list>
///
/// Built in memory from a location's handful of site rows, once per request.
/// </summary>
public sealed class WeatherSiteTimeline
{
    private readonly List<WeatherLocationSite> sites;
    private readonly List<DateOnly> firstDates;

    public WeatherSiteTimeline(IEnumerable<WeatherLocationSite> sites)
    {
        this.sites = sites.OrderBy(site => site.SiteVersion).ToList();
        if (this.sites.Count == 0)
        {
            throw new ArgumentException("A weather location always has at least one site.", nameof(sites));
        }

        firstDates = this.sites.Select(site => LocalDate(site.AppliedAt, site)).ToList();
    }

    public WeatherLocationSite Current => sites[^1];

    public IReadOnlyList<WeatherLocationSite> Sites => sites;

    /// <summary>The first local date <paramref name="site"/> owns.</summary>
    public DateOnly FirstOwnedDate(WeatherLocationSite site) => firstDates[sites.IndexOf(site)];

    /// <summary>The site that owns local date <paramref name="date"/>.</summary>
    public WeatherLocationSite SiteForDate(DateOnly date)
    {
        for (var i = sites.Count - 1; i > 0; i--)
        {
            if (date >= firstDates[i])
            {
                return sites[i];
            }
        }

        return sites[0];
    }

    /// <summary>The site whose <c>[AppliedAt, next AppliedAt)</c> contains <paramref name="instantUtc"/>.</summary>
    public WeatherLocationSite SiteForInstant(DateTime instantUtc)
    {
        for (var i = sites.Count - 1; i > 0; i--)
        {
            if (instantUtc >= sites[i].AppliedAt)
            {
                return sites[i];
            }
        }

        return sites[0];
    }

    /// <summary>The local date of an instant, by the instant rule (amendment 3).</summary>
    public DateOnly DateForInstant(DateTime instantUtc) => LocalDate(instantUtc, SiteForInstant(instantUtc));

    /// <summary>The UTC bounds of local date <paramref name="date"/> in its owning site's zone.</summary>
    public (DateTime FromUtc, DateTime ToUtc) DayBoundsUtc(DateOnly date)
    {
        var zone = Zone(SiteForDate(date));
        return (StartOfDayUtc(date, zone), StartOfDayUtc(date.AddDays(1), zone));
    }

    /// <summary>12:00 local on <paramref name="date"/>, in UTC, in the owning site's zone.</summary>
    public DateTime NoonUtc(DateOnly date)
    {
        var zone = Zone(SiteForDate(date));
        return ToUtc(date.ToDateTime(new TimeOnly(12, 0)), zone);
    }

    public static DateOnly LocalDate(DateTime instantUtc, WeatherLocationSite site) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(instantUtc), Zone(site)));

    public static DateTime LocalTime(DateTime instantUtc, WeatherLocationSite site) =>
        TimeZoneInfo.ConvertTimeFromUtc(AsUtc(instantUtc), Zone(site));

    public static TimeZoneInfo Zone(WeatherLocationSite site) => TimeZoneInfo.FindSystemTimeZoneById(site.TimeZoneId);

    /// <summary>Local midnight of <paramref name="date"/>, in UTC. A midnight skipped by DST is the first valid instant.</summary>
    public static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone) =>
        ToUtc(date.ToDateTime(TimeOnly.MinValue), zone);

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
