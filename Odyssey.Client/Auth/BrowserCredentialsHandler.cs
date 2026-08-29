using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Odyssey.Client.Auth;

/// <summary>
/// Opts every request into sending the auth cookie, which the browser's <c>fetch</c> does not do by
/// default when the API is on a different origin than the app.
/// </summary>
/// <remarks>
/// The WASM-only half of the old <c>CookieCredentialsHandler</c>; the portable antiforgery half now
/// lives in <c>Odyssey.ApiClient.Auth.AntiforgeryHandler</c>, which this handler chains into. Kept
/// here because <c>SetBrowserRequestCredentials</c> exists only in the WebAssembly package and has no
/// meaning off-browser.
/// </remarks>
public sealed class BrowserCredentialsHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return base.SendAsync(request, cancellationToken);
    }
}
