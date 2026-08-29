using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Odyssey.Api.Email;

/// <summary>
/// In-memory fixed window per recipient address, hand-rolled rather than layered on
/// <c>PartitionedRateLimiter</c> so the partition count has a hard ceiling: a flood of distinct
/// addresses must not grow memory without bound, and idle-partition eviction alone is a soft
/// guarantee over an hour-long window. Registered as a singleton — <c>MapIdentityApi</c> resolves
/// <see cref="SmtpEmailSender"/> once from the root provider, so anything scoped would never be seen.
/// </summary>
/// <remarks>
/// Counters live in process and are lost on restart. That is accepted: a restart is not
/// attacker-triggerable, and running N API instances behind a load balancer multiplies the effective
/// ceiling by N — a distributed store is the follow-up if the deployment ever stops being single
/// instance (issue #393, §12).
/// </remarks>
public sealed class EmailSendThrottle(
    TimeProvider timeProvider,
    ILogger<EmailSendThrottle> logger) : IEmailSendThrottle
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often to retry pruning once the dictionary is full — a sweep is the only way back
    /// under the ceiling, but scanning every entry on every send would make a flood expensive.</summary>
    private static readonly TimeSpan FullPruneInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, RecipientWindow> windows = new(StringComparer.Ordinal);

    private long lastPruneTicks;

    /// <summary>
    /// Records a send and reports whether it may proceed.
    /// </summary>
    /// <remarks>
    /// <paramref name="maxTrackedRecipients"/> is the hard ceiling on tracked addresses. At ~100 bytes
    /// per entry, the shipped 20,000 bounds the throttle at ~2 MB. Reaching it means a flood is under
    /// way; new addresses then fail <strong>open</strong> rather than evicting live counters, because
    /// dropping a real user's password reset is worse than letting a flood through a limiter that is
    /// already the second line of defence behind the per-IP policy.
    ///
    /// <para>
    /// That fail-open direction is exactly why the setting behind it is <em>raise-only</em> (issue #434
    /// key 14): a smaller table fills sooner, after which the victim's untracked address is waved
    /// through unconditionally. Note also that changing the value <strong>evicts nothing</strong> —
    /// existing entries age out over up to a full window, so a change is not instantaneous in either
    /// direction.
    /// </para>
    /// </remarks>
    public bool TryAcquire(
        string emailAddress,
        int limit,
        int windowMinutes,
        int maxTrackedRecipients,
        ReadOnlyMemory<byte> recipientHashKey)
    {
        // Supplied by the caller from a single per-send snapshot (issue #421 Wave 2, extended by #434):
        // these are database-backed now, and this method's compare-and-increment sits inside a lock, so
        // it cannot await a read of its own.
        var window = TimeSpan.FromMinutes(windowMinutes);
        var now = timeProvider.GetUtcNow();

        PruneIfDue(now, window, maxTrackedRecipients);

        var key = Normalize(emailAddress);
        if (!windows.TryGetValue(key, out var entry))
        {
            if (windows.Count >= maxTrackedRecipients)
            {
                logger.LogWarning(
                    "Per-recipient email throttle is tracking its maximum of {MaxTrackedRecipients} addresses; "
                    + "allowing a send to an untracked recipient. This indicates a flood of distinct addresses.",
                    maxTrackedRecipients);
                return true;
            }

            entry = windows.GetOrAdd(key, _ => new RecipientWindow(now));
        }

        lock (entry)
        {
            if (now - entry.StartedAt >= window)
            {
                entry.StartedAt = now;
                entry.Count = 0;
            }

            if (entry.Count >= limit)
            {
                // Information, not Warning: hitting this is usually a user clicking "resend" too
                // eagerly, not an attack. The hash lets an operator correlate repeat offenders
                // without the log becoming a mailing list — see HashRecipient.
                logger.LogInformation(
                    "Per-recipient email throttle reached for recipient {RecipientHash}: {Limit} sends already "
                    + "made within {WindowMinutes} minutes. Skipping this send.",
                    HashRecipient(recipientHashKey.Span, key), limit, windowMinutes);
                return false;
            }

            entry.Count++;
            return true;
        }
    }

    /// <summary>
    /// Trimmed and lower-cased invariant, so <c>User@x.com</c> and <c>user@x.com </c> share a bucket.
    /// Without this the limit is bypassed by varying the case of a single character.
    /// </summary>
    public static string Normalize(string emailAddress) =>
        emailAddress.Trim().ToLowerInvariant();

    /// <summary>
    /// A truncated <b>keyed</b> digest of the normalized address, for log correlation only. An email
    /// address is PII under GDPR and these logs are shipped and retained like any other.
    /// </summary>
    /// <remarks>
    /// HMAC rather than a bare SHA-256: the space of email addresses is small enough to enumerate, so
    /// an unkeyed digest is reversible offline by anyone holding the logs — which is exactly the
    /// audience the hashing is meant to protect the addresses from.
    ///
    /// <para>
    /// <c>static</c>, and the key arrives as an argument (issue #445 Wave 3): the key is database-backed
    /// now, so this type no longer resolves one of its own. <see cref="IEmailRecipientHashKey"/> owns
    /// both the read and the per-process fallback that applies when no key is stored — in which case
    /// digests correlate within one instance's lifetime but not across restarts, exactly as before.
    /// </para>
    /// </remarks>
    public static string HashRecipient(ReadOnlySpan<byte> hashKey, string normalizedAddress) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(hashKey, Encoding.UTF8.GetBytes(normalizedAddress)).AsSpan(0, 8));

    /// <summary>
    /// Drops entries whose window has elapsed, at most once a minute (or immediately once the
    /// dictionary is full). Removal is value-matched, so it cannot delete an entry another thread
    /// has just replaced; a reset racing a prune loses at most one count, which is the same outcome
    /// the reset itself would have produced.
    /// </summary>
    private void PruneIfDue(DateTimeOffset now, TimeSpan window, int maxTrackedRecipients)
    {
        if (windows.IsEmpty)
        {
            return;
        }

        // The SECOND read site of the ceiling, and the reason TryAcquire takes it rather than reading it
        // itself: this sweep is the only way back under the ceiling, so parameterising only the capacity
        // check above would leave a raised setting recovering on the 60-second cadence instead of the
        // 1-second one — making the behaviour after a change worse than the risk statement admits.
        var minimumInterval = windows.Count >= maxTrackedRecipients ? FullPruneInterval : PruneInterval;
        var previous = Interlocked.Read(ref lastPruneTicks);
        if (now.UtcTicks - previous < minimumInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastPruneTicks, now.UtcTicks, previous) != previous)
        {
            return;
        }

        foreach (var pair in windows)
        {
            if (now - pair.Value.StartedAt >= window)
            {
                windows.TryRemove(pair);
            }
        }
    }

    private sealed class RecipientWindow(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; set; } = startedAt;

        public int Count { get; set; }
    }
}
