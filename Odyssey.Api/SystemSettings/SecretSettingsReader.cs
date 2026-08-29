using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Context.Secrets;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// The consumption-side reader (issue #444 §5). Lives here rather than beside its interface because
/// it consults <see cref="SecretSettingsRegistry"/>, which is where the environment filter lives — a
/// <c>DiagnosticsSelfTest</c> row carried into Production by a database restore from staging must be
/// <em>inert</em>, not merely unreachable through the API.
///
/// <para>
/// Live per call, never cached (§5 option C1). Every plausible consumer is on a cold path measured in
/// hundreds of milliseconds of network I/O, against which one primary-key lookup and one unprotect are
/// noise — and a rotated credential must bind on the next use rather than after a TTL.
/// </para>
/// </summary>
internal sealed class SecretSettingsReader(
    OdysseyContext context,
    ISecretProtector protector,
    SecretSettingsRegistry registry,
    ILogger<SecretSettingsReader> logger) : ISecretSettingsReader
{
    public async Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (registry.Find(key) is not { } descriptor)
        {
            // Unregistered — including filtered out by the environment. NotSet, not Unreadable: there
            // is no credential here to be degraded about.
            return SecretResult.NotSet;
        }

        var row = await context.SystemSettingSecrets.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Key == descriptor.Key, cancellationToken);

        if (row is null)
        {
            return SecretResult.NotSet;
        }

        if (!string.Equals(
                row.ProtectionScheme, SystemSettingSecret.CurrentProtectionScheme, StringComparison.Ordinal))
        {
            logger.LogError(
                "Secret setting {Key} is stored under protection scheme {Scheme}, which this build cannot read.",
                descriptor.Key, row.ProtectionScheme);
            return SecretResult.Unreadable;
        }

        if (protector.Unprotect(descriptor.Key, row.Ciphertext) is not { } plaintext)
        {
            // The key name, never the ciphertext and never the underlying exception's own message —
            // some cryptographic providers embed payload fragments in theirs.
            logger.LogError(
                "Secret setting {Key} could not be decrypted; the Data Protection key ring may have "
                + "been replaced or lost.", descriptor.Key);
            return SecretResult.Unreadable;
        }

        return SecretResult.Found(plaintext);
    }
}
