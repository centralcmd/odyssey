using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// History windows (issue #436) through the real services on MariaDB, where EF InMemory cannot answer: the fence race
/// with a window edit stores nothing at all (AC 13a — InMemory is not atomic), the point service's diff-by-start write
/// replaces, shifts, adds and removes windows in one save without tripping the unique index (AC 22, 26), and the
/// relational purger handles coverage, the marker and the windows (AC 16).
/// </summary>
[Collection(MariaDbCollection.Name)]
public class WeatherHistoryWindowsRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_weather_windows_services";
    private const string AdminId = "weather-admin";

    // 09:00 UTC is 10:00 in Oslo: past the morning window, so the last complete day is 6 November.
    internal static readonly DateTime Morning = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private static DateOnly D(string date) => DateOnly.Parse(date);

    private static WeatherHistoryWindowDto W(string from, string? to = null) =>
        new() { From = D(from), To = to is null ? null : D(to) };

    // ── AC 13a ───────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_window_edit_while_a_fetch_is_out_stores_nothing_and_the_next_tick_plans_from_the_new_set()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var id = await CreateAsync(W("2026-02-10", "2026-02-17"));
        await ResolveStationsAsync(id);

        Guid versionAfterEdit = default;
        var source = new WindowObservations
        {
            DuringDaily = async () =>
            {
                await using var other = NewContext();
                await ReplaceWindowsAsync(other, id, W("2026-03-01", "2026-03-05"));
                await using var read = NewContext();
                versionAfterEdit = (await read.WeatherPointSchedules.AsNoTracking().SingleAsync(s => s.WeatherPointId == id)).Version;
            },
        };

        await using (var context = NewContext())
        {
            await Fetch(context, source).FetchObservationsAsync(id, CancellationToken.None);
        }

        Assert.Equal((D("2026-02-10"), D("2026-02-17")), Assert.Single(source.Calls));
        await using (var verify = NewContext())
        {
            Assert.False(await verify.WeatherObservations.AnyAsync(o => o.WeatherPointId == id));
            Assert.False(await verify.WeatherDays.AnyAsync(d => d.WeatherPointId == id));
            Assert.False(await verify.WeatherObservationCoverages.AnyAsync(c => c.WeatherPointId == id));
            var schedule = await verify.WeatherPointSchedules.AsNoTracking().SingleAsync(s => s.WeatherPointId == id);
            Assert.Equal(versionAfterEdit, schedule.Version);
            Assert.Equal(Morning, schedule.NextObservationFetchAfterUtc);
            Assert.Null(schedule.LeaseUntilUtc);
        }

        source.DuringDaily = null;
        await using (var context = NewContext())
        {
            await Fetch(context, source).FetchObservationsAsync(id, CancellationToken.None);
        }

        Assert.Equal((D("2026-03-01"), D("2026-03-05")), source.Calls[^1]);
        await using (var verify = NewContext())
        {
            var coverage = await verify.WeatherObservationCoverages.AsNoTracking().SingleAsync(c => c.WeatherPointId == id);
            Assert.Equal((D("2026-03-01"), D("2026-03-05")), (coverage.FromDate, coverage.ToDate));
            Assert.Equal(5, await verify.WeatherObservations.CountAsync(o => o.WeatherPointId == id));
        }
    }

    // ── AC 26 / AC 22 ────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task One_update_that_extends_shifts_reuses_a_start_adds_and_removes_windows_saves_cleanly()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var id = await CreateAsync(
            W("2026-02-10", "2026-02-17"), W("2026-06-01", "2026-06-07"), W("2026-09-01", "2026-09-05"), W("2026-10-01"));
        Dictionary<DateOnly, Guid> before;
        await using (var context = NewContext())
        {
            before = await context.WeatherPointHistoryWindows.Where(w => w.WeatherPointId == id).ToDictionaryAsync(w => w.FromDate, w => w.WeatherPointHistoryWindowId);
        }

        // Feb: end extended in place. Jun: start shifted (remove + insert of a different key). Sep 1: kept as a start but
        // now ongoing — the old ongoing window's start (Oct 1) vanishes, so Sep 1's row is updated, not re-inserted.
        // Mar: added.
        await using (var context = NewContext())
        {
            await ReplaceWindowsAsync(context, id,
                W("2026-09-01"), W("2026-06-02", "2026-06-07"), W("2026-03-01", "2026-03-03"), W("2026-02-10", "2026-02-20"));
        }

        await using var verify = NewContext();
        var after = await verify.WeatherPointHistoryWindows.AsNoTracking().Where(w => w.WeatherPointId == id).OrderBy(w => w.FromDate).ToListAsync();
        Assert.Equal(
            [(D("2026-02-10"), (DateOnly?)D("2026-02-20")), (D("2026-03-01"), D("2026-03-03")), (D("2026-06-02"), D("2026-06-07")), (D("2026-09-01"), null)],
            after.Select(w => (w.FromDate, w.ToDate)));
        Assert.Equal(before[D("2026-02-10")], after[0].WeatherPointHistoryWindowId);
        Assert.Equal(before[D("2026-09-01")], after[3].WeatherPointHistoryWindowId);
        Assert.DoesNotContain(after[1].WeatherPointHistoryWindowId, before.Values);
        Assert.DoesNotContain(after[2].WeatherPointHistoryWindowId, before.Values);

        var read = (await Admin(verify).GetAsync(id, true, CancellationToken.None))!;
        Assert.Equal(after.Select(w => w.FromDate), read.HistoryWindows!.Select(w => w.From));
    }

    [SkippableFact]
    public async Task Two_thousand_windows_replace_and_shift_in_one_save_each_and_read_back_whole()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var id = await CreateAsync(W("2026-02-10", "2026-02-17"));

        // One-day windows every other day from 2000-01-01; shifting by a day changes every start, so the second update
        // is 2 000 deletes and 2 000 inserts in one save. Request order is reversed: it is not start order.
        static WeatherHistoryWindowDto[] Set(int shift) => Enumerable.Range(0, 2000)
            .Select(i => new DateOnly(2000, 1, 1).AddDays(2 * i + shift))
            .Select(d => new WeatherHistoryWindowDto { From = d, To = d })
            .Reverse()
            .ToArray();

        foreach (var shift in new[] { 0, 1 })
        {
            await using var context = NewContext();
            await ReplaceWindowsAsync(context, id, Set(shift));
        }

        await using var verify = NewContext();
        var read = (await Admin(verify).GetAsync(id, true, CancellationToken.None))!;
        Assert.Equal(2000, read.HistoryWindows!.Count);
        Assert.Equal(Set(1).Select(w => w.From!.Value).Order(), read.HistoryWindows.Select(w => w.From));
        Assert.Equal(2000, await verify.WeatherPointHistoryWindows.CountAsync(w => w.WeatherPointId == id));
    }

    // ── AC 16 ────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_relational_history_purge_resets_the_marker_and_every_sites_coverage_and_keeps_the_windows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var id = await CreateAsync(W("2026-02-10", "2026-02-17"), W("2026-11-01"));
        await ResolveStationsAsync(id);
        var source = new WindowObservations();
        for (var i = 0; i < 2; i++)
        {
            await using var context = NewContext();
            await Fetch(context, source).FetchObservationsAsync(id, CancellationToken.None);
        }

        await using (var context = NewContext())
        {
            Assert.NotNull((await context.WeatherPointSchedules.SingleAsync(s => s.WeatherPointId == id)).ObservationsCheckedThroughDate);
            Assert.True(await context.WeatherObservations.AnyAsync(o => o.WeatherPointId == id));
            // A former site's checked range, which the purge removes too (§3.4: all site versions).
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 7, FromDate = D("2025-01-01"), ToDate = D("2025-01-31"),
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            await new RelationalWeatherHistoryPurger(context).PurgeAsync(id, WeatherPurgeMode.HistoryOnly, CancellationToken.None);
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherObservationCoverages.AnyAsync(c => c.WeatherPointId == id));
        Assert.False(await verify.WeatherObservations.AnyAsync(o => o.WeatherPointId == id));
        var schedule = await verify.WeatherPointSchedules.AsNoTracking().SingleAsync(s => s.WeatherPointId == id);
        Assert.Null(schedule.ObservationsCheckedThroughDate);
        Assert.Null(schedule.LastObservationSuccessUtc);
        Assert.Equal([D("2026-02-10"), D("2026-11-01")],
            await verify.WeatherPointHistoryWindows.Where(w => w.WeatherPointId == id).OrderBy(w => w.FromDate).Select(w => w.FromDate).ToListAsync());
    }

    [SkippableFact]
    public async Task The_relational_full_purge_removes_the_windows_and_coverage_with_the_point()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigratedAsync();
        var id = await CreateAsync(W("2026-02-10", "2026-02-17"));
        await using (var context = NewContext())
        {
            context.WeatherObservationCoverages.Add(new WeatherObservationCoverage
            {
                WeatherPointId = id, SiteVersion = 1, FromDate = D("2026-02-10"), ToDate = D("2026-02-17"),
            });
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            await new RelationalWeatherHistoryPurger(context).PurgeAsync(id, WeatherPurgeMode.Full, CancellationToken.None);
        }

        await using var verify = NewContext();
        Assert.False(await verify.WeatherPoints.AnyAsync(p => p.WeatherPointId == id));
        Assert.False(await verify.WeatherPointHistoryWindows.AnyAsync(w => w.WeatherPointId == id));
        Assert.False(await verify.WeatherObservationCoverages.AnyAsync(c => c.WeatherPointId == id));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateAsync(params WeatherHistoryWindowDto[] windows)
    {
        await using var context = NewContext();
        var point = await Admin(context).CreateAsync(new NewWeatherPoint
        {
            Name = "Cabin " + Guid.NewGuid().ToString("N")[..6],
            Latitude = 61.517m, Longitude = 10.165m, TimeZoneId = "Europe/Oslo",
            ForecastEnabled = false, HistoryEnabled = true, IsTracked = false,
            HistoryWindows = [.. windows],
        }, AdminId, CancellationToken.None);
        return point.WeatherPointId;
    }

    private static async Task ReplaceWindowsAsync(OdysseyContext context, Guid id, params WeatherHistoryWindowDto[] windows)
    {
        var admin = Admin(context);
        var current = (await admin.GetAsync(id, true, CancellationToken.None))!;
        var update = new UpdateWeatherPoint
        {
            Name = current.Name,
            Latitude = current.Site.Latitude,
            Longitude = current.Site.Longitude,
            AltitudeMeters = current.Site.AltitudeMeters,
            TimeZoneId = current.Site.TimeZoneId,
            ForecastEnabled = current.ForecastEnabled,
            HistoryEnabled = current.HistoryEnabled,
            IsTracked = current.IsTracked,
            SettingsVersion = current.SettingsVersion,
            HistoryWindows = [.. windows],
        };
        Assert.NotNull(await admin.UpdateAsync(id, update, AdminId, true, CancellationToken.None));
    }

    private async Task ResolveStationsAsync(Guid id) => await WindowFetching.ResolveStationsAsync(NewContext(), id, Morning);

    private static WeatherFetchService Fetch(OdysseyContext context, IWeatherObservationSource source) =>
        WindowFetching.Fetch(context, source, Morning);

    private static WeatherPointService Admin(OdysseyContext context) =>
        new(context, Fetch(context, new WindowObservations()), new WindowFetching.Settings(), new RelationalWeatherHistoryPurger(context),
            new WindowFetching.FixedTime(Morning));

    private async Task MigratedAsync()
    {
        await fixture.CreateMigratedDatabaseAsync(Database);
        await using var context = NewContext();
        await AttributionUsers.EnsureAsync(context, AdminId);
    }

    private OdysseyContext NewContext() => new(new DbContextOptionsBuilder<OdysseyContext>()
        .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
        .Options);
}

/// <summary>The fakes the window tests fetch observations through: a contact, a Frost client id and a fixed clock.</summary>
internal static class WindowFetching
{
    public static WeatherFetchService Fetch(OdysseyContext context, IWeatherObservationSource source, DateTime now) =>
        new(context, new NoForecasts(), source, new Settings(), new FrostSecret(), new FixedTime(now));

    /// <summary>Every measure group already has its station, so a fetch's one request is a daily one.</summary>
    public static async Task ResolveStationsAsync(OdysseyContext context, Guid id, DateTime now)
    {
        await using (context)
        {
            (await context.WeatherPoints.SingleAsync(p => p.WeatherPointId == id)).StationResolvedAtUtc = now;
            foreach (var group in new[] { WeatherMeasureGroup.Temperature, WeatherMeasureGroup.Precipitation, WeatherMeasureGroup.Wind })
            {
                context.WeatherStations.Add(new WeatherStation { WeatherPointId = id, Group = group, StationId = "SN18700", ResolvedAtUtc = now });
            }

            await context.SaveChangesAsync();
        }
    }

    internal sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    internal sealed class Settings : IWeatherSettingsLookup
    {
        public Task<WeatherSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WeatherSettings("ops@example.org", 60, 5, null, false));
    }

    private sealed class FrostSecret : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(SecretResult.Found("client-id"));
    }

    private sealed class NoForecasts : IWeatherForecastSource
    {
        public Task<ForecastFetchResult> FetchAsync(
            WeatherSiteQuery site, string contact, string? ifModifiedSince, TimeSpan budget, CancellationToken cancellationToken) =>
            Task.FromResult(new ForecastFetchResult(WeatherFetchOutcome.NotModified));
    }
}

/// <summary>Frost as the window tests see it: one value per requested day, every call recorded, a hook mid-request.</summary>
internal sealed class WindowObservations : IWeatherObservationSource
{
    public List<(DateOnly From, DateOnly To)> Calls { get; } = [];

    public Func<Task>? DuringDaily { get; set; }

    public Task<StationLookupResult> FindStationAsync(
        WeatherSiteQuery site, WeatherMeasureGroup group, string contact, string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(new StationLookupResult(true, new FoundStation("SN18700", "STATION", 4.2, 90)));

    public async Task<ObservationFetchResult> FetchDailyAsync(
        IReadOnlyCollection<string> stationIds, IReadOnlyCollection<string> elements, DateOnly from, DateOnly to,
        string contact, string clientId, CancellationToken cancellationToken)
    {
        Calls.Add((from, to));
        if (DuringDaily is { } during)
        {
            await during();
        }

        var days = new List<ObservationDayData>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            foreach (var id in stationIds)
            {
                days.Add(new ObservationDayData(d, 1, 0, 2, 3, 5, 0.4m) with { StationId = id });
            }
        }

        return new ObservationFetchResult(true, days);
    }
}
