namespace Odyssey.Api.Email;

/// <summary>
/// Caps how often transactional mail may be sent to one address (issue #393). The per-IP limiter in
/// <c>IdentityRateLimiting</c> cannot do this: the recipient lives in the request body, which would
/// have to be buffered and parsed ahead of model binding, and an attacker rotating IPs slips it
/// entirely. <see cref="SmtpEmailSender"/> is handed the address as a parameter, which makes it the
/// natural enforcement point.
/// </summary>
public interface IEmailSendThrottle
{
    /// <summary>
    /// Records a send to <paramref name="emailAddress"/> and reports whether it may proceed.
    /// Returning <c>false</c> means the message must be dropped — never turned into a distinct HTTP
    /// response, which would tell the caller that this address has recently received mail (i.e. that
    /// it is registered).
    /// </summary>
    /// <param name="limit">Messages allowed to one address per <paramref name="windowMinutes"/>.</param>
    /// <param name="windowMinutes">Length of the fixed window, in minutes.</param>
    /// <param name="maxTrackedRecipients">
    /// Addresses tracked at once. <strong>Required, not defaulted</strong> (issue #434 key 14): the
    /// implementation reads this value at TWO points — the fail-open capacity check and the choice of
    /// prune cadence — and making the parameter mandatory is what stops a future third call site
    /// quietly keeping a constant. The setting is <em>raise-only</em>, because the throttle fails open
    /// at capacity: a smaller table weakens the control instead of tightening it.
    /// </param>
    /// <param name="recipientHashKey">
    /// The HMAC key for the recipient digests written to the log (issue #445 Wave 3). A parameter for
    /// the same reason the limits are: it is database-backed now, and the locked region below cannot
    /// await a read of its own. Resolved by <see cref="IEmailRecipientHashKey"/>, which owns the
    /// per-process fallback — so it is never empty, and no caller can accidentally key one send
    /// differently from the next.
    /// </param>
    /// <remarks>
    /// The limits are parameters rather than something this service reads for itself (issue #421
    /// Wave 2). They now live in the database, and the compare-and-increment below runs inside a
    /// <c>lock</c> — where <c>await</c> is a compile error. Passing them in keeps the locked region
    /// synchronous and unchanged, and means one send cannot observe two different limits across a
    /// concurrent admin write. The caller reads one snapshot and uses it throughout.
    /// </remarks>
    bool TryAcquire(
        string emailAddress,
        int limit,
        int windowMinutes,
        int maxTrackedRecipients,
        ReadOnlyMemory<byte> recipientHashKey);
}
