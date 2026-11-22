using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Parsing;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>
/// One claimed feed, end to end (issue #470 §3.3–§3.4): the conditional GET, the parse, the ingest and the completion,
/// under the 90 s deadline and with the lease renewed every 60 s while the deadline has not passed. The completion is
/// the fetch's single final fenced save; the conditional clears of the two cross-boundary flags follow it in the same
/// transaction, so a refresh or URL change made during the fetch survives (AC 20, AC 77).
/// </summary>
public sealed class NewsFeedFetcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    NewsHostThrottleGate throttle,
    NewsIngestor ingestor,
    INewsFetchStateFlags flags,
    TimeProvider time,
    ILogger<NewsFeedFetcher> logger)
{
    /// <summary>A fetch that has not returned this long after its deadline is taken for hung in synchronous code.</summary>
    public static readonly TimeSpan HangGrace = TimeSpan.FromSeconds(5);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Fetches, ingests and completes. Returns the result, or <c>null</c> when the work hung past its deadline: the
    /// fetch is then abandoned without completing, its lease expires and the hang counts as a crash event (AC 52).
    /// </summary>
    public async Task<NewsFetchResult?> RunAsync(
        NewsClaim claim, NewsSettings settings, IReadOnlyList<NewsPrivateHostGrant> grants, int priorItemFailureParses,
        CancellationToken stoppingToken)
    {
        using var session = new NewsFetchSession(claim);
        var deadline = claim.ClaimTime + NewsJobTiming.FetchDeadline;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewing = RenewAsync(session, deadline, renewal.Token);

        NewsFetchResult result;
        try
        {
            var task = FetchAndIngestAsync(session, settings, grants, work.Token);
            try
            {
                result = await task.WaitAsync(NewsJobTiming.FetchDeadline + HangGrace, time, stoppingToken);
            }
            catch (TimeoutException)
            {
                // Cooperative cancellation first; a task that still does not return is hung in synchronous code.
                await work.CancelAsync();
                try
                {
                    result = await task.WaitAsync(HangGrace, time, stoppingToken);
                }
                catch (TimeoutException)
                {
                    logger.LogError("Fetching feed {FeedId} hung past its deadline; it is abandoned and its lease left to expire.", claim.FeedId);
                    return null;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            result = new NewsFetchResult { Kind = NewsFetchResultKind.Stopped };
        }
        finally
        {
            await renewal.CancelAsync();
            try
            {
                await renewing;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (result.ItemFailures > 0 && result.Kind == NewsFetchResultKind.Success && priorItemFailureParses < NewsJobTiming.ItemFailureReparses)
        {
            // A deterministic item failure is retried on at most the next 3 full parses: the validators are cleared.
            result = result with { TransientSkip = true };
        }

        await CompleteAsync(session, settings, result);
        return result;
    }

    private async Task RenewAsync(NewsFetchSession session, DateTime deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(NewsJobTiming.LeaseRenewal, time, cancellationToken);
            if (Now >= deadline || session.Aborted is not null)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var renewed = await session.WithFenceAsync(context, async state =>
            {
                state.LeaseExpiresAtUtc = Now + NewsJobTiming.Lease;
                await context.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
            if (renewed != NewsFenceOutcome.Ok)
            {
                logger.LogWarning("Feed {FeedId} lost its lease while fetching ({Outcome}); the fetch stops writing.", session.FeedId, renewed);
                return;
            }
        }
    }

    private async Task<NewsFetchResult> FetchAndIngestAsync(
        NewsFetchSession session, NewsSettings settings, IReadOnlyList<NewsPrivateHostGrant> grants, CancellationToken cancellationToken)
    {
        var claim = session.Claim;
        if (!Uri.TryCreate(claim.Url, UriKind.Absolute, out var url) || !NewsUrl.IsWebUrl(url) || NewsUrlKey.CanonicalHost(url) is not { } feedHost)
        {
            return new NewsFetchResult
            {
                Kind = NewsFetchResultKind.Failure,
                ErrorKind = NewsFeedErrorKind.BlockedAddress,
                ErrorMessage = "The feed address is not an http(s) address.",
            };
        }

        var context = new NewsEgressContext(feedHost, NewsEgressPurpose.Feed, grants);
        var trustValidators = !claim.ValidatorsResetPending;
        var http = await NewsHttpFetch.GetAsync(
            httpClients.CreateClient(NewsEgress.HttpClientName),
            url,
            context,
            trustValidators ? claim.ETag : null,
            trustValidators ? claim.LastModified : null,
            NewsBodyReader.MaxFeedBodyBytes,
            throttle.TakeWithinSpacingAsync,
            takeFirstHost: false,
            NewsEgress.FeedTimeout,
            cancellationToken);

        var common = new NewsFetchResult
        {
            Kind = NewsFetchResultKind.Failure,
            HttpStatus = http.HttpStatus,
            SuggestedUrl = http.SuggestedUrl,
            RedirectedFromHttps = http.RedirectedFromHttps,
            ETag = NewsValidators.SanitizeETag(http.ETag),
            LastModified = NewsValidators.SanitizeLastModified(http.LastModified),
        };

        switch (http.Outcome)
        {
            case NewsHttpOutcome.Throttled:
                return common with { Kind = NewsFetchResultKind.Throttled };
            case NewsHttpOutcome.Failed:
                return common with { ErrorKind = http.ErrorKind, ErrorMessage = http.ErrorMessage, RetryAfter = http.RetryAfter };
            case NewsHttpOutcome.NotModified:
                return common with { Kind = NewsFetchResultKind.NotModified, BodySha256 = claim.LastBodySha256 };
        }

        // A body identical to the last one is a 304 (§3.4 step 3, AC 11): not parsed, nothing written.
        var bodyHash = SHA256.HashData(http.Body);
        if (trustValidators && claim.LastBodySha256 is { } last && last.AsSpan().SequenceEqual(bodyHash))
        {
            return common with { Kind = NewsFetchResultKind.NotModified, BodySha256 = bodyHash };
        }

        // The parse is synchronous CPU work on untrusted input: off the caller's thread, so a hang is observable.
        var parse = await Task.Run(() => NewsFeedParser.Parse(http.Body, http.FinalUrl, http.Charset), cancellationToken);
        if (parse.Outcome != NewsFeedParseOutcome.Ok || parse.Feed is not { } feed)
        {
            return common with
            {
                ErrorKind = parse.ErrorKind,
                ErrorMessage = parse.ErrorKind == NewsFeedErrorKind.NotAFeed ? $"{feedHost} did not send a feed." : $"The feed from {feedHost} could not be read.",
            };
        }

        if (feed.ItemLimitReached)
        {
            logger.LogInformation("Feed {FeedId} has more items than the reader reads; only the first {Count} were read.", claim.FeedId, feed.ItemsRead);
        }

        var summary = await Task.Run(
            () => ingestor.IngestAsync(session, feed, settings, claim.ClaimTime + NewsEgress.FeedTimeout + NewsJobTiming.IngestBudget, cancellationToken),
            cancellationToken);
        if (summary.Aborted is not null)
        {
            return common with { Kind = NewsFetchResultKind.Aborted };
        }

        return common with
        {
            Kind = NewsFetchResultKind.Success,
            BodySha256 = bodyHash,
            Feed = feed,
            TransientSkip = summary.TransientSkip,
            StorageRefused = summary.StorageRefused,
            ItemFailures = summary.Failures,
            ItemsStored = summary.Stored,
            ItemsRevised = summary.Revised,
        };
    }

    /// <summary>The completion (§3.4 step 6): the fetch's single final fenced save, then the conditional clears.</summary>
    private async Task CompleteAsync(NewsFetchSession session, NewsSettings settings, NewsFetchResult result)
    {
        var claim = session.Claim;
        if (session.Aborted == NewsFenceOutcome.LeaseLost)
        {
            // Another writer holds the row; there is nothing this fetch may still write.
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var aborted = result.Kind == NewsFetchResultKind.Aborted || session.Aborted == NewsFenceOutcome.DeletionRequested;
            var outcome = await NewsTransactions.RunAsync(context, async () =>
            {
                var otherUrlHashes = result.SuggestedUrl is null || aborted
                    ? []
                    : await context.NewsFeeds.AsNoTracking().Where(f => f.NewsFeedId != claim.FeedId).Select(f => f.UrlKeyHash).ToListAsync(CancellationToken.None);
                var textBytes = await context.NewsStorageTotals.AsNoTracking()
                    .Where(t => t.Id == NewsStorageTotal.SingletonId)
                    .Select(t => t.TextAndSourceBytes)
                    .FirstOrDefaultAsync(CancellationToken.None);

                var fenced = await session.WithFenceAsync(context, async state =>
                {
                    Apply(state, claim, settings, result, aborted, otherUrlHashes, textBytes);
                    await context.SaveChangesAsync(CancellationToken.None);
                }, CancellationToken.None, allowPendingDeletion: true);

                if (fenced == NewsFenceOutcome.Ok && !aborted)
                {
                    // After the fenced save, in its transaction: a flag re-set during the fetch survives (§3.3).
                    await flags.ClearRefreshAsync(context, claim.FeedId, claim.ClaimTime, CancellationToken.None);
                    await flags.ClearValidatorsResetAsync(context, claim.FeedId, claim.ClaimTime, CancellationToken.None);
                }

                return fenced;
            }, CancellationToken.None);

            if (outcome != NewsFenceOutcome.Ok)
            {
                logger.LogWarning("Feed {FeedId} lost its lease before its fetch completed; the completion is dropped.", claim.FeedId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The lease then expires and the next claim retries; the crash accounting treats it as one event.
            logger.LogError("Completing the fetch of feed {FeedId} failed ({Failure}).", claim.FeedId, ex.GetType().Name);
        }
    }

    private void Apply(
        NewsFeedFetchState state, NewsClaim claim, NewsSettings settings, NewsFetchResult result, bool aborted,
        IReadOnlyList<byte[]> otherUrlHashes, long textAndSourceBytes)
    {
        var now = Now;
        state.LeaseOwner = null;
        state.LeaseExpiresAtUtc = null;
        if (aborted)
        {
            return;
        }

        // Graceful stop puts back the claim's crash suspicion; every other completion resets it.
        state.CrashSuspectCount = result.Kind == NewsFetchResultKind.Stopped
            ? (byte)Math.Max(0, state.CrashSuspectCount - 1)
            : (byte)0;
        switch (result.Kind)
        {
            case NewsFetchResultKind.Stopped:
            case NewsFetchResultKind.Throttled:
                state.NextFetchAtUtc = now + NewsJobTiming.ShortRetry;
                break;

            case NewsFetchResultKind.Failure:
                state.LastErrorAtUtc = now;
                state.LastErrorKind = result.ErrorKind;
                state.LastErrorMessage = result.ErrorMessage is null ? null : NewsText.Clamp(result.ErrorMessage, NewsLimits.MaxErrorMessageLength);
                state.LastHttpStatus = result.HttpStatus;
                state.ConsecutiveFailures++;
                var backoff = result.RetryAfter is { } retryAfter
                    ? NewsFetchRules.RetryAfter(retryAfter, claim.Interval)
                    : NewsFetchRules.Backoff(claim.Interval, state.ConsecutiveFailures, (Random.Shared.NextDouble() * 2) - 1);
                state.NextFetchAtUtc = now + (result.ErrorKind == NewsFeedErrorKind.Gone ? NewsJobTiming.MaxBackoff : backoff);
                break;

            default:
                state.LastSuccessAtUtc = now;
                state.LastHttpStatus = result.HttpStatus;
                state.LastErrorKind = NewsFeedErrorKind.None;
                state.LastErrorMessage = null;
                state.ConsecutiveFailures = 0;
                state.RedirectedFromHttps = result.RedirectedFromHttps;
                state.NextFetchAtUtc = now + claim.Interval;
                if (result.Feed is { } feed)
                {
                    state.Title = feed.Title is null ? null : NewsText.Clamp(feed.Title, NewsLimits.MaxFeedTitleLength);
                    state.SiteUrl = feed.SiteUrl is { Length: <= NewsLimits.MaxUrlLength } site ? site : null;
                    state.Description = feed.Description is null ? null : NewsText.Clamp(feed.Description, NewsLimits.MaxFeedDescriptionLength);
                    state.Language = feed.Language is { Length: <= NewsLimits.MaxLanguageLength } language ? language : null;
                    state.Format = feed.Format;
                    state.FeedTtlMinutes = feed.TtlMinutes;
                }

                if (result.TransientSkip)
                {
                    // The next poll re-parses (§3.4 step 6): a cap, the quota, the headroom, a swept reservation.
                    state.ETag = null;
                    state.LastModified = null;
                    state.LastBodySha256 = null;
                }
                else
                {
                    state.ETag = result.ETag ?? (result.Kind == NewsFetchResultKind.NotModified ? state.ETag : null);
                    state.LastModified = result.LastModified ?? (result.Kind == NewsFetchResultKind.NotModified ? state.LastModified : null);
                    state.LastBodySha256 = result.BodySha256;
                }

                break;
        }

        RecordSuggestion(state, claim, result.SuggestedUrl, otherUrlHashes);

        var today = DateOnly.FromDateTime(now);
        NewsFetchRules.RollDay(state, today);
        NewsFetchRules.ApplyQuotaReason(
            state,
            NewsTextQuota.IsReached(textAndSourceBytes, settings.TextQuotaBytes),
            NewsDailyCap.IsReached(state, today, settings.FeedMaxArticlesPerDay),
            now);
    }

    /// <summary>
    /// A same-host 301/308 is recorded, never applied (§3.3): only an API <c>PUT</c> writes <c>Url</c>. A suggestion that
    /// is already another feed's address is dropped and logged.
    /// </summary>
    private void RecordSuggestion(NewsFeedFetchState state, NewsClaim claim, string? suggestion, IReadOnlyList<byte[]> otherUrlHashes)
    {
        if (suggestion is null || string.Equals(suggestion, claim.Url, StringComparison.Ordinal)
            || string.Equals(suggestion, state.SuggestedUrl, StringComparison.Ordinal) || suggestion.Length > NewsLimits.MaxUrlLength)
        {
            return;
        }

        if (NewsUrlKey.UrlKey(suggestion) is not { } key)
        {
            return;
        }

        var hash = NewsUrlKey.Hash(key);
        if (otherUrlHashes.Any(other => other.AsSpan().SequenceEqual(hash)))
        {
            logger.LogInformation("Feed {FeedId} redirects permanently to an address another feed already has; the suggestion is dropped.", claim.FeedId);
            return;
        }

        state.SuggestedUrl = suggestion;
        logger.LogInformation("Feed {FeedId} redirects permanently on its own host ({Host}); the new address is suggested to the user.",
            claim.FeedId, Uri.TryCreate(suggestion, UriKind.Absolute, out var uri) ? NewsUrlKey.CanonicalHost(uri) : null);
    }
}
