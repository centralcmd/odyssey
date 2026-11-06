namespace Odyssey.Client.Pages.JobRuns;

/// <summary>What a polite message is, which decides what an assertive one may drop.</summary>
public enum AnnouncementKind
{
    /// <summary>A run outcome or a follow line — never dropped.</summary>
    Outcome,

    /// <summary>"Background jobs refreshed", "{n} jobs need attention" — dropped by an assertive message.</summary>
    Refresh,

    /// <summary>"{n} runs" — coalesced to the latest, and dropped by an assertive message.</summary>
    Count,
}

/// <summary>
/// The Background jobs page's announcement policy (issue #410 §3.7, §6.4; X2, X19, X-N4, round 3 A2/A3). Polite
/// messages go through a FIFO, at least <see cref="Spacing"/> apart. A count message is held until
/// <see cref="Spacing"/> after the last one, and only the latest is kept, so toggling inside an open dropdown is not
/// narrated. An assertive message is never queued: it is spoken at once and drops only pending count and refresh
/// messages; queued run outcomes stay. The two speakers are the page's two <c>OdsLiveAnnouncer</c>s.
/// </summary>
public sealed class JobRunAnnouncements(Func<string, Task> polite, Func<string, Task> assertive, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1500);

    private readonly object gate = new();
    private readonly List<(string Text, AnnouncementKind Kind)> queue = [];
    private ITimer? gap;
    private ITimer? countHold;
    private string? heldCount;
    private bool speaking;
    private bool disposed;

    /// <summary>Test seam: the polite messages waiting, oldest first (a held count is not yet one).</summary>
    internal IReadOnlyList<string> Pending
    {
        get { lock (gate) return [.. queue.Select(m => m.Text)]; }
    }

    public void Say(string text, AnnouncementKind kind = AnnouncementKind.Outcome)
    {
        lock (gate)
        {
            if (disposed)
                return;
            queue.Add((text, kind));
        }

        Pump();
    }

    /// <summary>"{n} runs": replaces any count still held or queued, and is released <see cref="Spacing"/> after this call.</summary>
    public void SayCount(string text)
    {
        lock (gate)
        {
            if (disposed)
                return;
            heldCount = text;
            countHold?.Dispose();
            countHold = time.CreateTimer(_ => ReleaseCount(), null, Spacing, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Spoken at once; drops the held count and every queued count and refresh message, never an outcome.</summary>
    public void Alert(string text)
    {
        lock (gate)
        {
            if (disposed)
                return;
            heldCount = null;
            countHold?.Dispose();
            countHold = null;
            queue.RemoveAll(m => m.Kind != AnnouncementKind.Outcome);
        }

        _ = assertive(text);
    }

    private void ReleaseCount()
    {
        lock (gate)
        {
            if (disposed || heldCount is not { } text)
                return;
            heldCount = null;
            queue.RemoveAll(m => m.Kind == AnnouncementKind.Count);
            queue.Add((text, AnnouncementKind.Count));
        }

        Pump();
    }

    private void Pump()
    {
        string text;
        lock (gate)
        {
            if (disposed || speaking || queue.Count == 0)
                return;
            text = queue[0].Text;
            queue.RemoveAt(0);
            speaking = true;
            gap?.Dispose();
            gap = time.CreateTimer(_ => EndGap(), null, Spacing, Timeout.InfiniteTimeSpan);
        }

        _ = polite(text);
    }

    private void EndGap()
    {
        lock (gate)
            speaking = false;
        Pump();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            queue.Clear();
            gap?.Dispose();
            countHold?.Dispose();
        }
    }
}
