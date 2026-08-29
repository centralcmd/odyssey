using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Odyssey.ApiClient.Auth;

/// <summary>
/// Lazily fetches and caches the API's antiforgery request token (from
/// <c>GET api/antiforgery/token</c>, which also sets the paired secret cookie) so
/// <see cref="AntiforgeryHandler"/> can echo it in the <c>X-XSRF-TOKEN</c> header on writes.
/// The <see cref="HttpClient"/> is resolved lazily (not constructor-injected) to avoid a DI cycle —
/// the client's pipeline depends on the handler, which depends on this store.
/// </summary>
public sealed class AntiforgeryTokenStore(IServiceProvider services)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (token is not null)
        {
            return token;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (token is null)
            {
                var http = services.GetRequiredService<HttpClient>();
                var response = await http.GetFromJsonAsync<AntiforgeryToken>(
                    "api/antiforgery/token", cancellationToken);
                token = response?.Token;
            }
        }
        catch
        {
            // Best-effort: leave the token null and let the write surface its own failure rather
            // than throwing from the request pipeline.
        }
        finally
        {
            gate.Release();
        }

        return token;
    }

    /// <summary>Drops the cached token so the next write re-fetches (e.g. after logout).</summary>
    public void Invalidate() => token = null;

    private sealed record AntiforgeryToken(string Token);
}
