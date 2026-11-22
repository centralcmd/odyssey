namespace Odyssey.Core.Imaging;

/// <summary>How a guarded render ended: finished with a value, or abandoned to its thread after the watchdog fired.</summary>
public readonly record struct GuardedRun<T>(bool Finished, T? Value, Task<T>? Abandoned);

/// <summary>
/// The shared wall-clock watchdog for native decodes (issue #556 arch B4): the work runs on a dedicated background
/// thread, and when the watchdog fires first the thread is abandoned — native code cannot be cancelled — and handed
/// back so the caller can pause while it lives and decide its own escalation. Used by the photo thumbnail job and by
/// the podcast artwork phase; each keeps its own incident scope, so one feed's artwork never stops photo thumbnails.
/// </summary>
public static class ThumbnailRenderGuard
{
    /// <summary>
    /// Runs <paramref name="work"/> under <paramref name="watchdog"/> on <paramref name="time"/>. The watchdog is armed
    /// BEFORE the thread starts: arming it after would let the clock move between the work starting and the timer
    /// existing, and a fake clock would never fire it at all.
    /// </summary>
    public static async Task<GuardedRun<T>> RunAsync<T>(Func<T> work, TimeSpan watchdog, TimeProvider time, string threadName)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(time);
        using var watchdogCancel = new CancellationTokenSource();
        var timer = Task.Delay(watchdog, time, watchdogCancel.Token);
        var run = RunOnThread(work, threadName);
        if (await Task.WhenAny(run, timer) == run)
        {
            await watchdogCancel.CancelAsync();
            return new GuardedRun<T>(true, await run, null);
        }

        return new GuardedRun<T>(false, default, run);
    }

    /// <summary>Runs <paramref name="work"/> on its own background thread, which cannot hold the process open.</summary>
    public static Task<T> RunOnThread<T>(Func<T> work, string threadName)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        thread.Start();
        return completion.Task;
    }
}
