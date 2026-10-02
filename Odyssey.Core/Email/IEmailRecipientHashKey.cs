namespace Odyssey.Core.Email;

/// <summary>
/// Resolves the HMAC key behind the throttle's recipient digests (issue #445 Wave 3).
///
/// <para>
/// <strong>Why this is not read inside <see cref="EmailSendThrottle"/>.</strong> That type's
/// compare-and-increment runs inside a <c>lock</c>, where <c>await</c> is a compile error, and it is a
/// singleton resolved from the root provider — so it can neither await a scoped
/// <c>OdysseyContext</c> nor hold one. The codebase already answers this exact shape for the
/// throttle's numeric limits: the caller reads one snapshot per send and passes it in. The key follows
/// the limits.
/// </para>
///
/// <para>
/// <strong>The per-process fallback lives here, not at the call site.</strong> A fallback generated per
/// call would make every digest unique and silently destroy the correlation the digests exist for. One
/// key per process is exactly today's behaviour.
/// </para>
/// </summary>
public interface IEmailRecipientHashKey
{
    /// <summary>
    /// The key to hash this send's recipient with. Never throws and never returns empty: a missing or
    /// unreadable row resolves to the per-process key, because a throttle that stops working because a
    /// LOG key cannot be read would trade a mailbombing control for a logging one.
    /// </summary>
    Task<ReadOnlyMemory<byte>> ResolveAsync(CancellationToken cancellationToken = default);
}
