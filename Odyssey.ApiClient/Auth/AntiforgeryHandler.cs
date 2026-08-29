namespace Odyssey.ApiClient.Auth;

/// <summary>
/// Attaches the API's antiforgery request token to unsafe requests so the server's
/// <c>AutoValidateAntiforgeryToken</c> filter accepts the write.
/// </summary>
/// <remarks>
/// This is the transport-portable half of what used to be the Blazor client's
/// <c>CookieCredentialsHandler</c>. The other half — opting the browser's <c>fetch</c> into sending
/// credentials cross-origin — is WASM-only and stays in <c>Odyssey.Client</c> as
/// <c>BrowserCredentialsHandler</c>. Consumers outside the browser get the same effect from
/// <c>new HttpClientHandler { UseCookies = true, CookieContainer = new() }</c>.
/// </remarks>
public sealed class AntiforgeryHandler(AntiforgeryTokenStore antiforgeryTokens) : DelegatingHandler
{
    public const string HeaderName = "X-XSRF-TOKEN";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Safe methods (incl. the token-fetch GET itself) are skipped, so resolving the token here
        // can't recurse.
        if (RequiresAntiforgeryToken(request.Method))
        {
            var token = await antiforgeryTokens.GetTokenAsync(cancellationToken);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Remove(HeaderName);
                request.Headers.Add(HeaderName, token);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool RequiresAntiforgeryToken(HttpMethod method) =>
        method == HttpMethod.Post
        || method == HttpMethod.Put
        || method == HttpMethod.Patch
        || method == HttpMethod.Delete;
}
