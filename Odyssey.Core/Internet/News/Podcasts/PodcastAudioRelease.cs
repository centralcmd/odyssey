using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>
/// The one rule for letting go of stored audio (issue #556 §3.2): auto-delete, a manual remove, a type change and the
/// purge all release through it. The file gets the ordinary deletion grace (a graced <c>StageRelease</c>), the row
/// returns to <c>None</c> with the episode, its chapters and <c>LastPlayedAtUtc</c> untouched, and the fence rotates so
/// any writer holding the old one loses. The caller saves, then moves the returned bytes with
/// <see cref="PodcastStorageDelta.AudioReleased"/> in the same transaction.
/// </summary>
public static class PodcastAudioRelease
{
    /// <summary>Stages the release of <paramref name="row"/>'s stored file; returns the bytes released (0 when none).</summary>
    public static async Task<long> StageAsync(IObjectStore store, PodcastEpisodeAudio row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        long released = 0;
        if (row.StoredObjectId is { } id)
        {
            await store.StageReleaseAsync(id, immediate: false, cancellationToken);
            released = row.SizeBytes ?? 0;
        }

        Clear(row);
        return released;
    }

    /// <summary>Back to <c>None</c>, nothing stored, nothing pending; the fence rotated.</summary>
    public static void Clear(PodcastEpisodeAudio row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.State = PodcastAudioState.None;
        row.StoredObjectId = null;
        row.ContentType = null;
        row.SizeBytes = null;
        row.Sha256 = null;
        row.StoredAtUtc = null;
        row.FailureKind = null;
        row.DeferredReason = null;
        row.RequestedBy = null;
        row.RequestedAtUtc = null;
        row.NextAttemptAtUtc = null;
        row.Attempts = 0;
        row.AudioFence = Guid.NewGuid();
    }
}
