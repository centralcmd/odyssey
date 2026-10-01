using System.Security.Cryptography;
using System.Text;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Core.Legal;

namespace Odyssey.Api.Legal;

/// <inheritdoc cref="ILegalPseudonymizer"/>
/// <remarks>
/// <para>
/// Why a keyed digest rather than a random value: a random pseudonym would satisfy the anti-reuse
/// requirement (a future account, even one reusing the deleted id — which <c>DemoDataSeeder</c>'s
/// deterministic assignment can produce — must never inherit compliance history) but would also
/// permanently sever the attribution GDPR Art. 7(1) requires being able to demonstrate for a
/// <em>specific</em> disputing individual. An HMAC keeps both: the 64-hex-character output cannot
/// collide with a real <c>AspNetUsers.Id</c>, and it is deterministically re-derivable from a claimed
/// email plus the secret.
/// </para>
/// <para>
/// Why keyed rather than a plain hash: an unkeyed digest of an email is trivially reversible by
/// dictionary attack over a known user base, which would leave the "pseudonymized" record no more
/// private than the plaintext it replaced.
/// </para>
/// <para>
/// <strong>The key is never read from configuration</strong> (issue #445 Wave 4).
/// <c>Legal:PseudonymizationSecret</c> was retired in the same change, so an <c>Unreadable</c> row has
/// nothing to silently fall back to. That matters more here than anywhere else in the migration: a
/// pseudonym written under the wrong key is indistinguishable from a correct one and can never be
/// re-derived, so a fallback would corrupt the record rather than merely fail.
/// </para>
/// </remarks>
public sealed class LegalPseudonymizer(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment) : ILegalPseudonymizer
{
    public async Task<string> PseudonymizeAsync(string? subject, CancellationToken cancellationToken = default)
    {
        var secret = await ResolveSecretAsync(cancellationToken);
        var normalized = (subject ?? string.Empty).Trim().ToUpperInvariant();

        return Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// The three read states, resolved the way the rest of the store's consumers resolve them — with
    /// one environment-dependent branch that predates this migration and is kept verbatim: outside
    /// Production an unset key substitutes a fixed, deliberately obvious development value, so the
    /// dev/Compose stack's delete flow works out of the box.
    /// </summary>
    private async Task<string> ResolveSecretAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();

        var secret = await reader.GetAsync(SecretSettingKeys.LegalPseudonymizationSecret, cancellationToken);

        if (secret.State == SecretReadState.Found && secret.TryGetValue(out var stored))
        {
            return stored;
        }

        if (secret.State == SecretReadState.Unreadable)
        {
            // NEVER the development value, and never a substitute of any kind, in any environment. The
            // row exists; this server simply cannot open it, and deriving from anything else would write
            // pseudonyms that look correct and are permanently wrong.
            throw new InvalidOperationException(
                $"The {SecretSettingKeys.LegalPseudonymizationSecret} credential is stored but cannot be "
                + "decrypted on this server, so acceptance records cannot be pseudonymized. Restore the "
                + "Data Protection key ring, or clear the credential in System settings and enter it again.");
        }

        if (!environment.IsProduction())
        {
            return LegalOptions.DevelopmentPseudonymizationSecret;
        }

        throw new InvalidOperationException(
            $"The {SecretSettingKeys.LegalPseudonymizationSecret} credential is not set, so acceptance "
            + "records cannot be pseudonymized. Set it in System settings → Credentials.");
    }
}
