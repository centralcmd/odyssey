using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Ai;

/// <summary>
/// Attaches the Anthropic API key to one request (the stored <c>FileAnalysisApiKey</c> secret, shared by
/// every Claude flow). A scope per send: handlers are pooled, so the scoped secret reader is never
/// captured. Returns the read state; the caller decides how a missing or unreadable key fails.
/// </summary>
internal static class ClaudeApiKey
{
    public const string HeaderName = "x-api-key";

    public static async Task<SecretReadState> ApplyAsync(
        HttpRequestMessage request, IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();
        var secret = await reader.GetAsync(SecretSettingKeys.FileAnalysisApiKey, cancellationToken);

        // Removed unconditionally: a re-sent request must not carry two values.
        request.Headers.Remove(HeaderName);
        if (secret.State == SecretReadState.Found && secret.TryGetValue(out var apiKey))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, apiKey);
            return SecretReadState.Found;
        }

        return secret.State == SecretReadState.Unreadable ? SecretReadState.Unreadable : SecretReadState.NotSet;
    }
}

/// <summary>No usable API key: the request was not sent. Not transient, so it sits outside the resilience pipeline.</summary>
public sealed class AiCredentialUnavailableException(bool unreadable)
    : Exception(unreadable
        ? "The AI provider credential is stored but cannot be decrypted on this server, so the request was not sent."
        : "No AI provider credential is configured on this server, so the request was not sent.")
{
    public bool Unreadable { get; } = unreadable;
}

/// <summary>
/// The key handler for a Claude flow other than statement analysis (which keeps its own exception type).
/// Fails closed on a missing or unreadable key with <see cref="AiCredentialUnavailableException"/>.
/// </summary>
public sealed class ClaudeApiKeyHandler(IServiceScopeFactory scopeFactory, ILogger<ClaudeApiKeyHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = await ClaudeApiKey.ApplyAsync(request, scopeFactory, cancellationToken);
        if (state != SecretReadState.Found)
        {
            logger.LogWarning("The AI provider credential is {State}; the request was not sent.", state);
            throw new AiCredentialUnavailableException(state == SecretReadState.Unreadable);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
