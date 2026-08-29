using System.Security.Cryptography;
using System.Text;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;

namespace Odyssey.Api.Email;

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

/// <inheritdoc cref="IEmailRecipientHashKey"/>
public sealed class EmailRecipientHashKey(
    IServiceScopeFactory scopeFactory,
    ILogger<EmailRecipientHashKey> logger) : IEmailRecipientHashKey
{
    /// <summary>
    /// One per process, generated eagerly. Digests then correlate within an instance's lifetime but not
    /// across restarts or between instances — the documented consequence of leaving the key unset, and
    /// unchanged by this migration.
    /// </summary>
    private readonly byte[] processKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The last state that was logged, so a steady state is reported once rather than on every send.
    /// The three states are distinguishable in the log, which is the point of AC 11: an operator must
    /// be able to tell "no key configured" (healthy) from "the key is stored and cannot be read"
    /// (a fault they caused and can fix), and a silent fallback would make a rotation look successful
    /// while correlation had quietly broken.
    /// </summary>
    private int lastLoggedState;

    public async Task<ReadOnlyMemory<byte>> ResolveAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();

        var secret = await reader.GetAsync(SecretSettingKeys.EmailRecipientHashKey, cancellationToken);

        switch (secret.State)
        {
            case SecretReadState.Found when secret.TryGetValue(out var configured):
                LogOnce(secret.State, () => logger.LogInformation(
                    "Per-recipient throttle digests are keyed by the stored recipient hash key."));
                return Encoding.UTF8.GetBytes(configured);

            case SecretReadState.Unreadable:
                // ERROR, and deliberately not the same line as NotSet. Both fall back to the process
                // key, so the only thing distinguishing a healthy unset deployment from a broken key
                // ring is this message.
                LogOnce(secret.State, () => logger.LogError(
                    "The stored recipient hash key could not be decrypted; per-recipient throttle logs are "
                    + "falling back to a per-process key, so recipient digests no longer correlate with "
                    + "those written before the key was stored. Clear the credential in System settings "
                    + "and enter it again."));
                return processKey;

            default:
                // Byte-identical to the behaviour before the migration, message included.
                LogOnce(secret.State, () => logger.LogInformation(
                    "No recipient hash key configured; per-recipient throttle logs use a per-process hash key, "
                    + "so recipient digests cannot be correlated across restarts or between instances."));
                return processKey;
        }
    }

    private void LogOnce(SecretReadState state, Action write)
    {
        if (Interlocked.Exchange(ref lastLoggedState, (int)state) != (int)state)
        {
            write();
        }
    }
}
