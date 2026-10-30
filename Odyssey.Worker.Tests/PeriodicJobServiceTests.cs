using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core.Jobs;
using Odyssey.Worker.Hosting;
using Xunit;

namespace Odyssey.Worker.Tests;

/// <summary>The host loop every worker job runs under: first run at once, one run per interval, a failed run is not the end.</summary>
public class PeriodicJobServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class RecordingJob(Func<int, CancellationToken, Task>? run = null) : IPeriodicJob
    {
        private readonly SemaphoreSlim ran = new(0);
        private int runs;

        public string Name => "recording";

        public TimeSpan Interval => TimeSpan.FromMinutes(1);

        public int Runs => Volatile.Read(ref runs);

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref runs);
            try
            {
                if (run is not null)
                {
                    await run(number, cancellationToken);
                }
            }
            finally
            {
                ran.Release();
            }
        }

        public async Task WaitForRunAsync() => Assert.True(await ran.WaitAsync(Wait), "the job did not run");
    }

    private static PeriodicJobService<RecordingJob> Service(RecordingJob job, ManualClock clock) =>
        new(job, clock, NullLogger<PeriodicJobService<RecordingJob>>.Instance);

    [Fact]
    public async Task The_first_run_is_immediate_and_the_next_waits_for_the_interval()
    {
        var clock = new ManualClock();
        var job = new RecordingJob();
        using var service = Service(job, clock);

        await service.StartAsync(CancellationToken.None);
        await job.WaitForRunAsync();
        Assert.Equal(1, job.Runs);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, job.Runs);

        clock.Advance(TimeSpan.FromSeconds(30));
        await job.WaitForRunAsync();
        Assert.Equal(2, job.Runs);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_the_next_run_goes_ahead()
    {
        var clock = new ManualClock();
        var job = new RecordingJob((number, _) => number == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        using var service = Service(job, clock);

        await service.StartAsync(CancellationToken.None);
        await job.WaitForRunAsync();

        clock.Advance(job.Interval);
        await job.WaitForRunAsync();
        Assert.Equal(2, job.Runs);
        Assert.False(service.ExecuteTask!.IsCompleted);

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>Each job has its own loop: one held in its run (the file store waiting on a failed check) holds no other.</summary>
    [Fact]
    public async Task A_job_held_in_its_run_does_not_hold_another()
    {
        var clock = new ManualClock();
        var held = new RecordingJob((_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var other = new RecordingJob();
        using var heldService = Service(held, clock);
        using var otherService = Service(other, clock);

        await heldService.StartAsync(CancellationToken.None);
        await otherService.StartAsync(CancellationToken.None);
        await other.WaitForRunAsync();
        clock.Advance(other.Interval);
        await other.WaitForRunAsync();

        Assert.Equal(2, other.Runs);
        Assert.Equal(1, held.Runs);
        await heldService.StopAsync(CancellationToken.None).WaitAsync(Wait);
        await otherService.StopAsync(CancellationToken.None).WaitAsync(Wait);
    }

    [Fact]
    public async Task Stopping_cancels_a_run_in_progress_and_ends_cleanly()
    {
        var clock = new ManualClock();
        var started = new TaskCompletionSource();
        var job = new RecordingJob(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var service = Service(job, clock);

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(Wait);
        await service.StopAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }
}
