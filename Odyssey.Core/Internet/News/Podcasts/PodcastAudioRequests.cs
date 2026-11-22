using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>
/// The API's writes to the worker-owned <see cref="PodcastEpisodeAudio"/> row (issue #556 §3.3): each a conditional
/// single-statement update on the current state that rotates <see cref="PodcastEpisodeAudio.AudioFence"/>, so a worker
/// holding the old fence loses its next save — never a tracked read-modify-write against the fence. Relational: a raw
/// conditional <c>UPDATE</c>; EF InMemory: a tracked write behind the same predicate (the
/// <see cref="INewsFetchStateFlags"/> pattern). Each returns how many rows changed.
/// </summary>
public interface IPodcastAudioRequests
{
    /// <summary>
    /// Queues a user download: <c>None</c> or <c>Failed</c> → <c>Queued</c>, attempts reset, due now — only while the row
    /// is outside its re-request cooldown (<see cref="PodcastLimits.UserRequeueCooldown"/>).
    /// </summary>
    Task<int> RequestAsync(OdysseyContext context, Guid episodeId, DateTime now, CancellationToken cancellationToken);

    /// <summary>A user cancel or clear: <c>Queued</c>, <c>Deferred</c> or <c>Failed</c> → <c>None</c>, stamping the cooldown.</summary>
    Task<int> CancelAsync(OdysseyContext context, Guid episodeId, DateTime now, CancellationToken cancellationToken);

    /// <summary>A feed left podcasts (§5.1 #2): every <c>Queued</c>, <c>Deferred</c> or <c>Failed</c> row of it → <c>None</c>.</summary>
    Task<int> CancelPendingForFeedAsync(OdysseyContext context, Guid feedId, CancellationToken cancellationToken);
}

public sealed class PodcastAudioRequests : IPodcastAudioRequests
{
    private const byte None = (byte)PodcastAudioState.None;
    private const byte Queued = (byte)PodcastAudioState.Queued;
    private const byte Failed = (byte)PodcastAudioState.Failed;
    private const byte Deferred = (byte)PodcastAudioState.Deferred;
    private const byte User = (byte)PodcastAudioRequestedBy.User;

    public async Task<int> RequestAsync(OdysseyContext context, Guid episodeId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cooldownStart = now - PodcastLimits.UserRequeueCooldown;
        if (context.Database.IsRelational())
        {
            return await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE `PodcastEpisodeAudio`
                SET `State` = {Queued}, `RequestedBy` = {User}, `RequestedAtUtc` = {now}, `LastUserRequestAtUtc` = {now},
                    `Attempts` = 0, `NextAttemptAtUtc` = {now}, `FailureKind` = NULL, `DeferredReason` = NULL,
                    `AudioFence` = {Guid.NewGuid()}
                WHERE `NewsArticleId` = {episodeId} AND `State` IN ({None}, {Failed})
                  AND (`LastUserRequestAtUtc` IS NULL OR `LastUserRequestAtUtc` <= {cooldownStart})
                """,
                cancellationToken);
        }

        var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
            a => a.NewsArticleId == episodeId
                && (a.State == PodcastAudioState.None || a.State == PodcastAudioState.Failed)
                && (a.LastUserRequestAtUtc == null || a.LastUserRequestAtUtc <= cooldownStart),
            cancellationToken);
        if (row is null)
        {
            return 0;
        }

        row.State = PodcastAudioState.Queued;
        row.RequestedBy = PodcastAudioRequestedBy.User;
        row.RequestedAtUtc = now;
        row.LastUserRequestAtUtc = now;
        row.Attempts = 0;
        row.NextAttemptAtUtc = now;
        row.FailureKind = null;
        row.DeferredReason = null;
        row.AudioFence = Guid.NewGuid();
        await context.SaveChangesAsync(cancellationToken);
        return 1;
    }

    public async Task<int> CancelAsync(OdysseyContext context, Guid episodeId, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.IsRelational())
        {
            return await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE `PodcastEpisodeAudio`
                SET `State` = {None}, `RequestedBy` = NULL, `RequestedAtUtc` = NULL, `NextAttemptAtUtc` = NULL,
                    `FailureKind` = NULL, `DeferredReason` = NULL, `LastUserRequestAtUtc` = {now}, `AudioFence` = {Guid.NewGuid()}
                WHERE `NewsArticleId` = {episodeId} AND `State` IN ({Queued}, {Deferred}, {Failed})
                """,
                cancellationToken);
        }

        var row = await context.PodcastEpisodeAudio.FirstOrDefaultAsync(
            a => a.NewsArticleId == episodeId
                && (a.State == PodcastAudioState.Queued || a.State == PodcastAudioState.Deferred || a.State == PodcastAudioState.Failed),
            cancellationToken);
        if (row is null)
        {
            return 0;
        }

        Reset(row);
        row.LastUserRequestAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        return 1;
    }

    public async Task<int> CancelPendingForFeedAsync(OdysseyContext context, Guid feedId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database.IsRelational())
        {
            // One fence for the whole set is enough: each worker holds the fence of the row it claimed, and every row
            // here gets a value it does not hold.
            return await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE `PodcastEpisodeAudio`
                SET `State` = {None}, `RequestedBy` = NULL, `RequestedAtUtc` = NULL, `NextAttemptAtUtc` = NULL,
                    `FailureKind` = NULL, `DeferredReason` = NULL, `AudioFence` = {Guid.NewGuid()}
                WHERE `NewsFeedId` = {feedId} AND `State` IN ({Queued}, {Deferred}, {Failed})
                """,
                cancellationToken);
        }

        var rows = await context.PodcastEpisodeAudio
            .Where(a => a.NewsFeedId == feedId
                && (a.State == PodcastAudioState.Queued || a.State == PodcastAudioState.Deferred || a.State == PodcastAudioState.Failed))
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            Reset(row);
        }

        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private static void Reset(PodcastEpisodeAudio row)
    {
        row.State = PodcastAudioState.None;
        row.RequestedBy = null;
        row.RequestedAtUtc = null;
        row.NextAttemptAtUtc = null;
        row.FailureKind = null;
        row.DeferredReason = null;
        row.AudioFence = Guid.NewGuid();
    }
}
