using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Weather;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests.Jobs;

/// <summary>
/// The weather job's rows in the shared run log (issue #409 §3.2, §3.3, §3.5): the heartbeat before the
/// not-configured return, one row per burst with its counts, and a manual run's bounded refresh. MET is a stub.
/// </summary>
public sealed class WeatherJobRunTests
{
    /// <summary>AC 4, 5: three points fetched, one failing, are one burst row, closed PartiallySucceeded by an idle tick.</summary>
    [Fact]
    public async Task A_burst_counts_its_points_and_an_idle_tick_closes_it_partially_succeeded()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Home");
        await h.EnableAsync("Cabin");
        await h.EnableAsync("Boat");
        var calls = 0;
        h.Forecasts.Respond = _ => Task.FromResult(++calls == 2
            ? new ForecastFetchResult(WeatherFetchOutcome.Failed, FailureKind: WeatherFetchFailureKind.RateLimited)
            : new ForecastFetchResult(WeatherFetchOutcome.NotModified, ExpiresUtc: h.Time.GetUtcNow().UtcDateTime.AddHours(1)));
        var job = Job(h);

        await job.RunOnceAsync(CancellationToken.None);
        var open = (WeatherJobRun)Assert.Single(await RunsAsync(h));
        Assert.Equal((JobRunStatus.Running, 3, 1), (open.Status, open.PointsAttempted, open.PointsFailed));

        await job.RunOnceAsync(CancellationToken.None);

        var closed = (WeatherJobRun)Assert.Single(await RunsAsync(h));
        Assert.Equal((JobRunStatus.PartiallySucceeded, JobFailureCategory.ProviderRateLimited), (closed.Status, closed.FailureCategory));
        Assert.Equal((3, 1), (closed.ItemsProcessed, closed.ItemsFailed));
        Assert.Equal(WeatherFetchFailureKind.RateLimited, closed.FailureKind);
    }

    /// <summary>AC 30: an unconfigured job still heartbeats, and records no run.</summary>
    [Fact]
    public async Task An_unconfigured_job_heartbeats_and_records_nothing()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Home");
        h.Settings.Value = h.Settings.Value with { Contact = null };

        await Job(h).RunOnceAsync(CancellationToken.None);

        await using var context = h.NewContext();
        Assert.Equal(JobType.Weather, Assert.Single(await context.JobHeartbeats.ToListAsync()).JobType);
        Assert.Empty(await context.JobRuns.ToListAsync());
        Assert.Equal(0, h.Forecasts.Calls);
    }

    /// <summary>AC 18: a manual run sends at most the tracked cap, and nothing for a point whose forecast has not expired.</summary>
    [Fact]
    public async Task A_manual_run_is_capped_and_never_refetches_an_unexpired_forecast()
    {
        using var h = new WeatherTestHarness();
        await h.EnableAsync("Home");
        await h.EnableAsync("Cabin");
        await h.EnableAsync("Boat");
        h.Settings.Value = h.Settings.Value with { MaxTrackedPoints = 2 };
        h.Forecasts.Respond = _ => Task.FromResult(
            new ForecastFetchResult(WeatherFetchOutcome.NotModified, ExpiresUtc: h.Time.GetUtcNow().UtcDateTime.AddHours(1)));
        var job = Job(h);

        var first = await RequestAsync(h);
        await job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, h.Forecasts.Calls);
        var run = (WeatherJobRun)(await RunsAsync(h)).Single(r => r.JobRunId == first);
        Assert.Equal((JobRunStatus.Succeeded, JobRunTrigger.Manual, 2), (run.Status, run.Trigger, run.PointsAttempted));

        var second = await RequestAsync(h);
        await job.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, h.Forecasts.Calls);
        var idle = (WeatherJobRun)(await RunsAsync(h)).Single(r => r.JobRunId == second);
        Assert.Equal((JobRunStatus.Succeeded, 0, 2), (idle.Status, idle.PointsAttempted, idle.PointsSkippedNotExpired));
    }

    private static async Task<List<JobRun>> RunsAsync(WeatherTestHarness h)
    {
        await using var context = h.NewContext();
        return await context.JobRuns.AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync();
    }

    private static async Task<Guid> RequestAsync(WeatherTestHarness h)
    {
        await using var context = h.NewContext();
        var now = h.Time.GetUtcNow().UtcDateTime;
        var run = new WeatherJobRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running,
            StartedAtUtc = now, LastActivityAtUtc = now,
        };
        context.JobRuns.Add(run);
        await context.SaveChangesAsync();
        return run.JobRunId;
    }

    private static WeatherScheduleJob Job(WeatherTestHarness h)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => h.NewContext())
            .AddScoped(sp => h.Fetch(sp.GetRequiredService<OdysseyContext>()))
            .AddSingleton<IWeatherSettingsLookup>(h.Settings)
            .AddJobRunRecordingWith(h.Time)
            .BuildServiceProvider();
        return new WeatherScheduleJob(services.GetRequiredService<IServiceScopeFactory>(), h.Time, NullLogger<WeatherScheduleJob>.Instance);
    }
}
