using System.Security.Cryptography;
using System.Text;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Core.Email;

namespace Odyssey.Api.Email;

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
