namespace Odyssey.Client.Services;

/// <summary>
/// The ONE 60-second clock every <c>OdsRelativeTime</c> on a page re-renders from (issue #473 §4.1, §10),
/// so a list of a hundred "12 min ago" labels runs one timer rather than a hundred. The timer exists only
/// while something is subscribed, and it ticks silently: a tick re-renders text that sits outside every
/// live region, so nothing is announced.
/// </summary>
public sealed class RelativeTimeTicker(TimeProvider time) : IDisposable
{
    /// <summary>The tick interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private event Action? Ticked;
    private ITimer? timer;
    private int subscribers;

    /// <summary>The clock the labels are measured against.</summary>
    public DateTimeOffset UtcNow => time.GetUtcNow();

    /// <summary>The number of live subscriptions (for tests).</summary>
    public int Subscribers
    {
        get
        {
            lock (gate)
            {
                return subscribers;
            }
        }
    }

    /// <summary>Subscribes <paramref name="onTick"/> to the shared tick; dispose the result to leave.</summary>
    public IDisposable Subscribe(Action onTick)
    {
        lock (gate)
        {
            Ticked += onTick;
            subscribers++;
            timer ??= time.CreateTimer(_ => Ticked?.Invoke(), null, Interval, Interval);
        }

        return new Subscription(this, onTick);
    }

    private void Unsubscribe(Action onTick)
    {
        lock (gate)
        {
            Ticked -= onTick;
            subscribers--;
            if (subscribers == 0)
            {
                timer?.Dispose();
                timer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
            Ticked = null;
            subscribers = 0;
        }
    }

    private sealed class Subscription(RelativeTimeTicker owner, Action onTick) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.Unsubscribe(onTick);
        }
    }
}
