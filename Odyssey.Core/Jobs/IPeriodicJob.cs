namespace Odyssey.Core.Jobs;

/// <summary>
/// A background job that <c>Odyssey.Worker</c> runs on a fixed interval. The job owns its logic and its DI
/// scopes; the host owns the timer, the immediate first run, error isolation and shutdown. A job is
/// registered as a singleton, so state on the instance (a log throttle, say) survives from run to run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every worker replica runs every job.</b> A job that must not run twice at once coordinates through
/// the database — a lease or a concurrency token on the row it works on — never through process state.
/// </para>
/// <para>
/// <b>A run never overlaps itself.</b> The host waits for <see cref="RunOnceAsync"/> before the next tick,
/// and ticks missed while a run was busy are skipped rather than queued.
/// </para>
/// </remarks>
public interface IPeriodicJob
{
    /// <summary>A stable name for the host's log lines.</summary>
    string Name { get; }

    /// <summary>The time from the start of one run to the start of the next.</summary>
    TimeSpan Interval { get; }

    /// <summary>One run. An exception is logged by the host and the next run goes ahead.</summary>
    Task RunOnceAsync(CancellationToken cancellationToken);
}
