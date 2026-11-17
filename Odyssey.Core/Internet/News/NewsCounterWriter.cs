using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.Internet.News;

/// <summary>
/// The relative-increment seam for <see cref="NewsFeedCounter"/> and <see cref="NewsStorageTotal"/> (issue #470 §3.5).
/// Each call runs on the caller's context, so on MariaDB it joins the transaction of the article save it belongs to.
/// Lock order: feed counters by ascending feed id first, the storage totals LAST, as the final statement of the
/// transaction, so the hot row is locked for one statement.
/// </summary>
public interface INewsCounterWriter
{
    /// <summary>Adds to a feed's article and unread counts.</summary>
    Task AddToFeedAsync(OdysseyContext context, Guid feedId, int articles, int unread, CancellationToken cancellationToken);

    /// <summary>Adds to the storage totals (either delta may be negative, for a released file).</summary>
    Task AddToStorageAsync(OdysseyContext context, long imageBytes, long textAndSourceBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Relational: <c>UPDATE … SET X = X + @d</c>, so concurrent writers never lose an increment and no row is read first.
/// EF InMemory: a tracked increment (the existing lookup-seam pattern), which is what lets the fast tiers exercise the
/// counters at all; that provider runs one writer at a time. Never <c>ExecuteUpdate</c> (AC 44).
/// </summary>
public sealed class NewsCounterWriter : INewsCounterWriter
{
    public async Task AddToFeedAsync(OdysseyContext context, Guid feedId, int articles, int unread, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (articles == 0 && unread == 0)
        {
            return;
        }

        if (context.Database.IsRelational())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedCounters` SET `ArticleCount` = `ArticleCount` + {articles}, `UnreadCount` = `UnreadCount` + {unread} WHERE `NewsFeedId` = {feedId}",
                cancellationToken);
            return;
        }

        var row = await context.NewsFeedCounters.FirstOrDefaultAsync(c => c.NewsFeedId == feedId, cancellationToken);
        if (row is null)
        {
            return;
        }

        row.ArticleCount += articles;
        row.UnreadCount += unread;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddToStorageAsync(OdysseyContext context, long imageBytes, long textAndSourceBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (imageBytes == 0 && textAndSourceBytes == 0)
        {
            return;
        }

        if (context.Database.IsRelational())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsStorageTotals` SET `ImageBytes` = `ImageBytes` + {imageBytes}, `TextAndSourceBytes` = `TextAndSourceBytes` + {textAndSourceBytes} WHERE `Id` = {NewsStorageTotal.SingletonId}",
                cancellationToken);
            return;
        }

        var row = await context.NewsStorageTotals.FirstOrDefaultAsync(t => t.Id == NewsStorageTotal.SingletonId, cancellationToken);
        if (row is null)
        {
            // The migration seeds the row; an InMemory store created without HasData gets it here.
            row = new NewsStorageTotal { Id = NewsStorageTotal.SingletonId };
            context.NewsStorageTotals.Add(row);
        }

        row.ImageBytes += imageBytes;
        row.TextAndSourceBytes += textAndSourceBytes;
        await context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// The API's (and the manual run's) writes to the worker-owned fetch-state row, and the worker's conditional clears
/// (issue #470 §3.3): each a conditional single-column update that does NOT check <c>FetchFence</c>, so none can fail a
/// running fetch and a running fetch cannot fail them. Relational: a raw conditional <c>UPDATE</c>; EF InMemory: a
/// tracked write behind the same predicate. Each returns whether a row changed.
/// </summary>
public interface INewsFetchStateFlags
{
    /// <summary>Refresh: <c>SET RefreshRequestedAtUtc = @now WHERE NewsFeedId = @id</c>.</summary>
    Task<bool> RequestRefreshAsync(OdysseyContext context, Guid feedId, DateTime now, CancellationToken cancellationToken);

    /// <summary>URL change: <c>SET ValidatorsResetRequestedAtUtc = @now WHERE NewsFeedId = @id</c>.</summary>
    Task<bool> RequestValidatorsResetAsync(OdysseyContext context, Guid feedId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Release: clears the quarantine and the crash count, only while quarantined.</summary>
    Task<bool> ReleaseQuarantineAsync(OdysseyContext context, Guid feedId, CancellationToken cancellationToken);

    /// <summary>Accepting or overriding a suggestion: clears it only while it still equals <paramref name="seen"/>.</summary>
    Task<bool> ClearSuggestionAsync(OdysseyContext context, Guid feedId, string seen, CancellationToken cancellationToken);

    /// <summary>The worker's completion: clears a refresh requested no later than the claim, so a later one survives.</summary>
    Task<bool> ClearRefreshAsync(OdysseyContext context, Guid feedId, DateTime claimTime, CancellationToken cancellationToken);

    /// <summary>The worker's completion: clears the reset flag AND the validators, for a reset requested no later than the claim.</summary>
    Task<bool> ClearValidatorsResetAsync(OdysseyContext context, Guid feedId, DateTime claimTime, CancellationToken cancellationToken);
}

/// <inheritdoc cref="INewsFetchStateFlags" />
public sealed class NewsFetchStateFlags : INewsFetchStateFlags
{
    public Task<bool> RequestRefreshAsync(OdysseyContext context, Guid feedId, DateTime now, CancellationToken cancellationToken) =>
        WriteAsync(
            context,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `RefreshRequestedAtUtc` = {now} WHERE `NewsFeedId` = {feedId}", cancellationToken),
            feedId,
            _ => true,
            state => state.RefreshRequestedAtUtc = now,
            cancellationToken);

    public Task<bool> RequestValidatorsResetAsync(OdysseyContext context, Guid feedId, DateTime now, CancellationToken cancellationToken) =>
        WriteAsync(
            context,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `ValidatorsResetRequestedAtUtc` = {now} WHERE `NewsFeedId` = {feedId}", cancellationToken),
            feedId,
            _ => true,
            state => state.ValidatorsResetRequestedAtUtc = now,
            cancellationToken);

    public Task<bool> ReleaseQuarantineAsync(OdysseyContext context, Guid feedId, CancellationToken cancellationToken) =>
        WriteAsync(
            context,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `QuarantinedAtUtc` = NULL, `CrashSuspectCount` = 0 WHERE `NewsFeedId` = {feedId} AND `QuarantinedAtUtc` IS NOT NULL",
                cancellationToken),
            feedId,
            state => state.QuarantinedAtUtc is not null,
            state =>
            {
                state.QuarantinedAtUtc = null;
                state.CrashSuspectCount = 0;
            },
            cancellationToken);

    public Task<bool> ClearSuggestionAsync(OdysseyContext context, Guid feedId, string seen, CancellationToken cancellationToken) =>
        WriteAsync(
            context,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `SuggestedUrl` = NULL WHERE `NewsFeedId` = {feedId} AND `SuggestedUrl` = {seen}", cancellationToken),
            feedId,
            state => string.Equals(state.SuggestedUrl, seen, StringComparison.Ordinal),
            state => state.SuggestedUrl = null,
            cancellationToken);

    public Task<bool> ClearRefreshAsync(OdysseyContext context, Guid feedId, DateTime claimTime, CancellationToken cancellationToken) =>
        WriteAsync(
            context,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `RefreshRequestedAtUtc` = NULL WHERE `NewsFeedId` = {feedId} AND `RefreshRequestedAtUtc` <= {claimTime}",
                cancellationToken),
            feedId,
            state => state.RefreshRequestedAtUtc <= claimTime,
            state => state.RefreshRequestedAtUtc = null,
            cancellationToken);

    public Task<bool> ClearValidatorsResetAsync(OdysseyContext context, Guid feedId, DateTime claimTime, CancellationToken cancellationToken) =>
        WriteAsync(
            context,
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE `NewsFeedFetchStates` SET `ValidatorsResetRequestedAtUtc` = NULL, `ETag` = NULL, `LastModified` = NULL, `LastBodySha256` = NULL WHERE `NewsFeedId` = {feedId} AND `ValidatorsResetRequestedAtUtc` <= {claimTime}",
                cancellationToken),
            feedId,
            state => state.ValidatorsResetRequestedAtUtc <= claimTime,
            state =>
            {
                state.ValidatorsResetRequestedAtUtc = null;
                state.ETag = null;
                state.LastModified = null;
                state.LastBodySha256 = null;
            },
            cancellationToken);

    private static async Task<bool> WriteAsync(
        OdysseyContext context,
        Func<Task<int>> relational,
        Guid feedId,
        Func<NewsFeedFetchState, bool> predicate,
        Action<NewsFeedFetchState> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.IsRelational())
        {
            return await relational() > 0;
        }

        var state = await context.NewsFeedFetchStates.FirstOrDefaultAsync(s => s.NewsFeedId == feedId, cancellationToken);
        if (state is null || !predicate(state))
        {
            return false;
        }

        apply(state);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
