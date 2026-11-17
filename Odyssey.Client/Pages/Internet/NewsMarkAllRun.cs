using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>How a Mark all read run ended (issue #473 §3.2.9).</summary>
public enum NewsMarkAllEnd
{
    /// <summary>The last request said <c>hasMore: false</c>.</summary>
    Finished,

    /// <summary>Cancel or Escape stopped it after the request in flight, or a second <c>news-busy</c> did.</summary>
    Stopped,

    /// <summary>A later request failed: some articles were marked.</summary>
    PartialFailure,

    /// <summary>The first request failed; nothing was marked.</summary>
    Failed,

    /// <summary>A <c>422</c> on the scope or <c>asOfUtc</c> before anything was marked: the list is out of date.</summary>
    OutOfDate,
}

/// <summary>The outcome of a run: how it ended, the total of every <c>markedCount</c>, and the last problem.</summary>
public sealed record NewsMarkAllResult(NewsMarkAllEnd End, int Total, ApiProblem? Problem = null);

/// <summary>
/// One Mark all read run (issue #473 §3.2.9, K9). Each <c>POST /api/news-articles/mark-read</c> marks at most
/// <see cref="Odyssey.Dtos.NewsLimits.MaxMarkReadPerRequest"/> articles and says <c>hasMore</c>; while it does, the
/// same call is repeated with the same filters and the same <c>asOfUtc</c>. <see cref="Stop"/> ends the run after the
/// request in flight. A <c>news-busy</c> partway through waits out <c>Retry-After</c> and repeats the call once; a
/// second one stops the run. The running total is reported at most every <see cref="ProgressInterval"/>.
/// </summary>
public sealed class NewsMarkAllRun(INewsApiClient news, TimeProvider time)
{
    /// <summary>The "Marking… N so far" line moves at most this often.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    public bool StopRequested { get; private set; }

    public void Stop() => StopRequested = true;

    public async Task<NewsMarkAllResult> RunAsync(NewsArticleListFilter filter, DateTime asOfUtc, Func<int, Task> progress)
    {
        var total = 0;
        var calls = 0;
        var busyRetried = false;
        var lastShown = time.GetUtcNow();
        while (true)
        {
            var result = await news.MarkReadAsync(filter, asOfUtc);
            if (!result.IsSuccess || result.Value is not { } step)
            {
                var busy = NewsFeedback.IsBusy(result.Problem);
                if (busy && calls > 0 && !busyRetried && !StopRequested)
                {
                    busyRetried = true;
                    await Task.Delay(result.RetryAfter ?? NewsLoadProblem.DefaultBusyWait, time);
                    continue;
                }

                if (calls == 0)
                {
                    return NewsFeedback.Is(result.Problem, NewsErrorCodes.MarkReadScopeInvalid)
                        ? new NewsMarkAllResult(NewsMarkAllEnd.OutOfDate, 0, result.Problem)
                        : new NewsMarkAllResult(NewsMarkAllEnd.Failed, 0, result.Problem);
                }

                return new NewsMarkAllResult(busy ? NewsMarkAllEnd.Stopped : NewsMarkAllEnd.PartialFailure, total, result.Problem);
            }

            calls++;
            busyRetried = false;
            total += step.MarkedCount;
            if (!step.HasMore)
            {
                return new NewsMarkAllResult(NewsMarkAllEnd.Finished, total);
            }

            if (StopRequested)
            {
                return new NewsMarkAllResult(NewsMarkAllEnd.Stopped, total);
            }

            if (time.GetUtcNow() - lastShown >= ProgressInterval || calls == 1)
            {
                lastShown = time.GetUtcNow();
                await progress(total);
            }
        }
    }
}
