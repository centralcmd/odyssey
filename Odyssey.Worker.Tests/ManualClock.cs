namespace Odyssey.Worker.Tests;

/// <summary>A clock whose timers fire only when the test advances it, so no test waits on real time.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private readonly Lock gate = new();

    public DateTimeOffset Now { get; private set; } = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (gate)
        {
            Now += by;
            due = [.. timers];
        }

        foreach (var timer in due)
        {
            timer.FireUpTo(Now);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? next;
        private TimeSpan period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            next = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            this.period = period;
            return true;
        }

        public void FireUpTo(DateTimeOffset now)
        {
            while (next is { } due && due <= now)
            {
                next = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : due + period;
                callback(state);
            }
        }

        public void Dispose() => next = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
