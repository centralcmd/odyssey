using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Core.Finance;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// Attaches the file-analysis provider credential to each outbound request (issue #445 Wave 1).
///
/// <para>
/// <strong>Why a handler and not a header.</strong> Before this, the key was applied inside the
/// synchronous <c>AddHttpClient</c> configure callback as a <c>DefaultRequestHeaders</c> entry
/// evaluated ONCE at client construction. That callback cannot <c>await</c> a scoped
/// <c>OdysseyContext</c>, so a database-backed value could not reach it at all — and even if it
/// could, a header fixed at construction could never follow a rotation. Per-send is the only shape
/// that delivers "a replacement binds on the next request, with no restart".
/// </para>
///
/// <para>
/// <strong>Registered OUTSIDE the resilience pipeline</strong>, so an unreadable credential fails once
/// instead of being retried twice at the far end of a circuit breaker. A credential fault is not
/// transient, and retrying it only delays the job's recorded failure.
/// </para>
///
/// <para>
/// <strong>It never falls back to configuration.</strong> There is nothing to fall back to —
/// <c>FileAnalysis:ApiKey</c> was retired from <c>FileAnalysisOptions</c> in the same change, so the
/// property a fallback would have read no longer exists. <c>Unreadable</c> throws; it is never
/// silently treated as <c>NotSet</c>, because the administrator who rotated the key would otherwise
/// see analyses continue to fail with a generic provider error.
/// </para>
///
/// <para>
/// <strong>Redirects.</strong> .NET strips only <c>Authorization</c> across origins, so a custom
/// <c>x-api-key</c> would survive one — which is why the client's primary handler sets
/// <c>AllowAutoRedirect = false</c> and <c>ClaudeFileAnalysisProvider</c> turns a <c>3xx</c> into a
/// provider error instead of following it. This handler therefore attaches the key exactly once, to
/// the request it is given, and to no request it has not seen; it additionally strips any
/// <c>x-api-key</c> already present so a re-sent request cannot accumulate a second, stale value.
/// </para>
/// </summary>
public sealed class FileAnalysisApiKeyHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<FileAnalysisApiKeyHandler> logger) : DelegatingHandler
{
    internal const string HeaderName = "x-api-key";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A scope per send: this handler is pooled for the client's handler lifetime, so it must not
        // capture the scoped reader (or the OdysseyContext behind it) in a field.
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();

        var secret = await reader.GetAsync(SecretSettingKeys.FileAnalysisApiKey, cancellationToken);

        // Removed unconditionally, before the branch: a request the resilience pipeline re-sends must
        // not carry two values, and the "no key" paths must not inherit one from an earlier attempt.
        request.Headers.Remove(HeaderName);

        switch (secret.State)
        {
            case SecretReadState.Found when secret.TryGetValue(out var apiKey):
                request.Headers.TryAddWithoutValidation(HeaderName, apiKey);
                break;

            case SecretReadState.Unreadable:
                // Fail CLOSED, and say which of the two conditions this is. Never the configured value:
                // there is none, and reinstating one would mean sending with the credential the
                // administrator believed they had replaced.
                logger.LogError(
                    "The file-analysis provider credential is stored but could not be decrypted; the request "
                    + "was not sent. Clear the credential in System settings and enter it again.");
                throw new FileAnalysisCredentialException(
                    "The analysis provider credential is stored but cannot be decrypted on this server, so "
                    + "the request was not sent.");

            default:
                // NotSet — today's behaviour with an empty key: the request goes out unauthenticated,
                // the provider rejects it, and the job is recorded failed. An absent row is a healthy
                // "not configured", so it is not an error here.
                logger.LogWarning(
                    "No file-analysis provider credential is configured; the request will be sent without "
                    + "{HeaderName} and the provider is expected to reject it.", HeaderName);
                break;
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
