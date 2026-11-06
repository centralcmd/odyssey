using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.JobRuns;

/// <summary>Why a follow ended (issue #410 §3.5 "Stop conditions").</summary>
public enum FollowEnd
{
    /// <summary>The run left <c>Running</c>.</summary>
    Finished,

    /// <summary>The 3-minute cap passed with the run still running.</summary>
    Cap,

    /// <summary><c>404</c>: the run is gone.</summary>
    Gone,

    /// <summary><c>401</c>/<c>403</c>: never retried.</summary>
    Forbidden,

    /// <summary><c>429</c>.</summary>
    RateLimited,

    /// <summary>Three consecutive network or <c>5xx</c> failures.</summary>
    Errors,

    /// <summary>Stop following, leaving the page or signing out: the token was cancelled.</summary>
    Stopped,
}

/// <summary>A follow's end, and the last detail read when it finished.</summary>
public readonly record struct FollowResult(FollowEnd End, JobRunDetail? Last);

/// <summary>
/// Follows one manually started run (issue #410 §3.5, F6, FS4): one request at a time (each awaited before the next
/// is scheduled), every <see cref="Interval"/>, for at most <see cref="Cap"/> — so at most 36 requests a run. Nothing
/// else on the page polls.
/// </summary>
public static class JobRunFollower
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(3);

    public const int MaxConsecutiveErrors = 3;

    /// <param name="onTick">Each successful read — the page refreshes that run's detail entry and table row.</param>
    public static async Task<FollowResult> FollowAsync(
        IBackgroundJobsApiClient jobs, Guid runId, TimeProvider time, Func<JobRunDetail, Task> onTick, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var errors = 0;
        while (true)
        {
            try
            {
                await Task.Delay(Interval, time, ct);
            }
            catch (OperationCanceledException)
            {
                return new(FollowEnd.Stopped, null);
            }

            var read = await jobs.GetRunAsync(runId, ct);
            if (ct.IsCancellationRequested)
            {
                return new(FollowEnd.Stopped, null);
            }

            if (read.IsSuccess && read.Value is { } detail)
            {
                errors = 0;
                await onTick(detail);
                if (detail.Status != JobRunStatus.Running)
                {
                    return new(FollowEnd.Finished, detail);
                }
            }
            else
            {
                switch (read.Status)
                {
                    case HttpStatusCode.NotFound:
                        return new(FollowEnd.Gone, null);
                    case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                        return new(FollowEnd.Forbidden, null);
                    case HttpStatusCode.TooManyRequests:
                        return new(FollowEnd.RateLimited, null);
                    default:
                        if (++errors >= MaxConsecutiveErrors)
                        {
                            return new(FollowEnd.Errors, null);
                        }

                        break;
                }
            }

            if (time.GetUtcNow() - started >= Cap)
            {
                return new(FollowEnd.Cap, null);
            }
        }
    }
}
