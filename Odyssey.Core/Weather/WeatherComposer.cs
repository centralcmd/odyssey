using Odyssey.Context;

namespace Odyssey.Core.Weather;

/// <summary>
/// The composition rules of issue #348 §3, kept pure so they are tested without a database.
///
/// <para>
/// <b>Effective step.</b> For an instant <i>t</i>, the step at <i>t</i> from the run that contains <i>t</i>
/// and sorts first by <c>SiteVersion</c> desc, <c>ProviderUpdatedAt</c> desc, <c>FetchedAtUtc</c> desc. The
/// tie-break is total, so exactly one effective step exists per instant (AC 28).
/// </para>
/// </summary>
public static class WeatherComposer
{
    /// <summary>
    /// The effective steps of a point in <c>[fromUtc, toUtc)</c>, picked IN THE DATABASE: a step is kept
    /// only when no step at the same instant outranks it by the same total order <see cref="EffectiveSteps"/>
    /// uses. Superseded runs are never pruned (they are history), so selecting every candidate and ranking in
    /// memory would materialise one row per stored run per hour on every read — a cost any reader could
    /// multiply. The anti-join walks the <c>(WeatherPointId, TimeUtc, SiteVersion, RunProviderUpdatedAt,
    /// RunFetchedAtUtc)</c> index, so the result is one row per hour however many runs exist.
    /// </summary>
    public static IQueryable<WeatherForecastStep> EffectiveStepsQuery(
        IQueryable<WeatherForecastStep> steps, Guid pointId, DateTime fromUtc, DateTime toUtc) =>
        steps.Where(s => s.WeatherPointId == pointId && s.TimeUtc >= fromUtc && s.TimeUtc < toUtc
            && !steps.Any(t => t.WeatherPointId == s.WeatherPointId && t.TimeUtc == s.TimeUtc
                && (t.SiteVersion > s.SiteVersion
                    || (t.SiteVersion == s.SiteVersion && (t.RunProviderUpdatedAt > s.RunProviderUpdatedAt
                        || (t.RunProviderUpdatedAt == s.RunProviderUpdatedAt && (t.RunFetchedAtUtc > s.RunFetchedAtUtc
                            || (t.RunFetchedAtUtc == s.RunFetchedAtUtc && t.WeatherForecastStepId > s.WeatherForecastStepId))))))));

    /// <summary>One effective step per distinct <see cref="WeatherForecastStep.TimeUtc"/>, ascending.</summary>
    public static List<WeatherForecastStep> EffectiveSteps(IEnumerable<WeatherForecastStep> candidates) =>
        candidates
            .GroupBy(step => step.TimeUtc)
            .Select(group => group
                .OrderByDescending(step => step.SiteVersion)
                .ThenByDescending(step => step.RunProviderUpdatedAt)
                .ThenByDescending(step => step.RunFetchedAtUtc)
                .ThenByDescending(step => step.WeatherForecastStepId)
                .First())
            .OrderBy(step => step.TimeUtc)
            .ToList();

    /// <summary>The longest period a stored step can have; how far before a window a step can start and still reach into it.</summary>
    public const int LongestPeriodHours = 12;

    /// <summary>
    /// The rows of one window for the hourly read's day mode (issue #414): the <paramref name="effective"/> steps whose
    /// period overlaps <c>[windowStartUtc, windowEndUtc)</c>, with every coarser step dropped where a finer one overlaps
    /// it, in time order. Superseded runs are kept as history, so an older run's 6-hour block can sit beside a newer
    /// run's hourly steps; the finer data wins and any hour it leaves uncovered is a gap, never a double-counted row.
    /// The overlap is judged against the overlapping set as a whole, before anything is dropped, so the order of
    /// removal cannot change the answer. The result never overlaps.
    /// </summary>
    public static List<WeatherForecastStep> DayRows(
        IReadOnlyList<WeatherForecastStep> effective, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var overlapping = effective
            .Where(step => step.TimeUtc < windowEndUtc && step.TimeUtc.AddHours(step.PeriodHours) > windowStartUtc)
            .ToList();

        var rows = new List<WeatherForecastStep>();
        foreach (var step in overlapping
            .Where(step => !overlapping.Any(finer => finer.PeriodHours < step.PeriodHours && Overlaps(finer, step)))
            .OrderBy(step => step.TimeUtc))
        {
            // Two steps of one period never overlap in MET's grid; should a stored row ever break that, the earlier
            // one is kept rather than letting the read contradict its own no-overlap contract.
            if (rows.Count == 0 || step.TimeUtc >= rows[^1].TimeUtc.AddHours(rows[^1].PeriodHours))
            {
                rows.Add(step);
            }
        }

        return rows;
    }

    private static bool Overlaps(WeatherForecastStep a, WeatherForecastStep b) =>
        a.TimeUtc < b.TimeUtc.AddHours(b.PeriodHours) && b.TimeUtc < a.TimeUtc.AddHours(a.PeriodHours);

    /// <summary>The effective step whose period covers <paramref name="instantUtc"/>, preferring the finest period.</summary>
    public static WeatherForecastStep? Covering(IReadOnlyList<WeatherForecastStep> effective, DateTime instantUtc) =>
        effective
            .Where(step => step.TimeUtc <= instantUtc && instantUtc < step.TimeUtc.AddHours(step.PeriodHours))
            .OrderBy(step => step.PeriodHours)
            .ThenByDescending(step => step.TimeUtc)
            .FirstOrDefault();

    /// <summary>
    /// The forecast half of one local day (§3 Day composition), or null when no step starts on it. The
    /// values come from the steps that START on the day, so a period straddling midnight is counted once,
    /// on its own day; <paramref name="overlapping"/> — every effective step whose period reaches into the
    /// day — decides only the coverage, so a 6 h step starting at 18:00 UTC still covers the first hour of
    /// a day whose midnight falls at 23:00 UTC.
    /// </summary>
    public static ForecastHalf? ForecastHalf(
        IReadOnlyList<WeatherForecastStep> effectiveForDay, DateTime dayStartUtc, DateTime dayEndUtc, DateTime noonUtc,
        IReadOnlyList<WeatherForecastStep>? overlapping = null)
    {
        if (effectiveForDay.Count == 0)
        {
            return null;
        }

        var temperatures = effectiveForDay.Select(step => step.AirTemperatureC)
            .Concat(effectiveForDay.Select(step => step.TempMinC))
            .Concat(effectiveForDay.Select(step => step.TempMaxC))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToList();

        // Precipitation over non-overlapping periods: walk the steps in time order and count a step's
        // amount only when its period starts at or after the end of the last period counted, so a 6 h
        // amount is never added on top of the six hourly amounts it overlaps.
        decimal? precipitation = null;
        var countedUntil = DateTime.MinValue;
        foreach (var step in effectiveForDay)
        {
            if (step.PrecipitationMm is { } amount && step.TimeUtc >= countedUntil)
            {
                precipitation = (precipitation ?? 0) + amount;
                countedUntil = step.TimeUtc.AddHours(step.PeriodHours);
            }
        }

        var winds = effectiveForDay.Where(step => step.WindSpeedMps is not null).Select(step => step.WindSpeedMps!.Value).ToList();
        var gusts = effectiveForDay.Where(step => step.WindGustMps is not null).Select(step => step.WindGustMps!.Value).ToList();

        return new ForecastHalf(
            Covering(effectiveForDay, noonUtc)?.SymbolCode,
            temperatures.Count > 0 ? temperatures.Min() : null,
            temperatures.Count > 0 ? temperatures.Max() : null,
            winds.Count > 0 ? Math.Round(winds.Average(), 1, MidpointRounding.AwayFromZero) : null,
            gusts.Count > 0 ? gusts.Max() : null,
            precipitation is { } p ? Math.Round(p, 1, MidpointRounding.AwayFromZero) : null,
            CoverageHours(overlapping ?? effectiveForDay, dayStartUtc, dayEndUtc),
            effectiveForDay.Max(step => step.RunProviderUpdatedAt));
    }

    /// <summary>Whole hours of <c>[dayStart, dayEnd)</c> that some effective step's period covers.</summary>
    public static int CoverageHours(IReadOnlyList<WeatherForecastStep> effective, DateTime dayStartUtc, DateTime dayEndUtc)
    {
        var covered = 0;
        for (var hour = dayStartUtc; hour < dayEndUtc; hour = hour.AddHours(1))
        {
            if (effective.Any(step => step.TimeUtc <= hour && hour < step.TimeUtc.AddHours(step.PeriodHours)))
            {
                covered++;
            }
        }

        return covered;
    }
}

public sealed record ForecastHalf(
    string? SymbolCode,
    decimal? MinTempC,
    decimal? MaxTempC,
    decimal? MeanWindSpeedMps,
    decimal? MaxWindGustMps,
    decimal? PrecipitationMm,
    int CoverageHours,
    DateTime IssuedAtUtc);
