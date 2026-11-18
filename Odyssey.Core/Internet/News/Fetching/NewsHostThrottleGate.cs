using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>
/// The per-host spacing shared by every worker replica (issue #470 §3.6): a request takes its host only when the
/// host's <see cref="NewsHostThrottle.NextAllowedAtUtc"/> has passed, and moves it one spacing ahead in the
/// same save (<see cref="DefaultSpacing"/>, 2 s). The column is the row's own concurrency token, so of two replicas taking one host at once exactly one
/// save succeeds (AC 22). Feeds, images and every redirect hop take their own host's slot.
/// </summary>
public sealed class NewsHostThrottleGate(IServiceScopeFactory scopes, TimeProvider time)
{
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(2);

    /// <summary>The spacing; settable only so a test with a manual clock can follow a same-host redirect.</summary>
    public TimeSpan Spacing { get; init; } = DefaultSpacing;

    /// <summary>The throttle key of a canonical host: SHA-256 of the lower-case host.</summary>
    public static byte[] KeyOf(string host) => NewsUrlKey.Hash(host.ToLowerInvariant());

    /// <summary>
    /// Takes the host's slot, or says when it frees: <c>null</c> when taken, else the time the host is next allowed
    /// (now, when another replica won a race for it).
    /// </summary>
    public async Task<DateTime?> TryTakeAsync(string host, CancellationToken cancellationToken)
    {
        var key = KeyOf(host);
        var now = time.GetUtcNow().UtcDateTime;
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        // FindAsync: the key is a byte array, compared structurally by the identity map on every provider.
        var row = await context.NewsHostThrottle.FindAsync([key], cancellationToken);
        if (row is null)
        {
            context.NewsHostThrottle.Add(new NewsHostThrottle { HostKeyHash = key, NextAllowedAtUtc = now + Spacing });
        }
        else if (row.NextAllowedAtUtc > now)
        {
            return row.NextAllowedAtUtc;
        }
        else
        {
            row.NextAllowedAtUtc = now + Spacing;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException)
        {
            // A concurrency failure (another replica moved the row) or a duplicate key (it inserted it first).
            return now;
        }
    }

    /// <summary>
    /// Takes the slot, waiting at most one spacing for it: the wait covers a host this process has just requested
    /// itself (a same-host redirect), never a longer hold. Returns false when the host stays busy.
    /// </summary>
    public async Task<bool> TakeWithinSpacingAsync(string host, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var busyUntil = await TryTakeAsync(host, cancellationToken);
            if (busyUntil is null)
            {
                return true;
            }

            var wait = busyUntil.Value - time.GetUtcNow().UtcDateTime;
            if (attempt > 0 || wait > Spacing)
            {
                return false;
            }

            await Task.Delay(wait > TimeSpan.Zero ? wait + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(50), time, cancellationToken);
        }

        return false;
    }
}
