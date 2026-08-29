namespace Odyssey.Context.Secrets;

/// <summary>
/// The consumption side of the encrypted secret store (issue #444 §5). Consumers arrive in the
/// per-secret follow-up issues; this issue ships the seam and the three-state contract.
///
/// <para>
/// <strong>Never cached.</strong> Secrets are read on cold paths — an SMTP connect, an AI client
/// construction — and a rotated credential must bind on the next use rather than after a TTL. That is
/// the same argument <see cref="SystemSettingsReader"/> already makes for the perimeter settings, and
/// it is the whole point of moving a credential into the UI.
/// </para>
///
/// <para>
/// <strong>Reachability constrains the follow-ups.</strong> The reader is only useful to a consumer
/// that can <c>await</c> a scoped <c>OdysseyContext</c> at the moment it needs the value. A value
/// captured once at client-construction time (a <c>DefaultRequestHeaders</c> entry, say) could not
/// pick up a rotation anyway, so such a consumer must first move to a per-request seam — a
/// <c>DelegatingHandler</c>, or the <c>IServiceScopeFactory</c> pattern <c>SmtpEmailSender</c>
/// already uses.
/// </para>
/// </summary>
public interface ISecretSettingsReader
{
    /// <summary>
    /// Reads one secret live. An unregistered key — including a key filtered out of the registry by
    /// the environment — reports <see cref="SecretResult.NotSet"/>, so a row carried into Production
    /// by a restore from staging is inert rather than merely unreachable through the API.
    /// </summary>
    Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default);
}
