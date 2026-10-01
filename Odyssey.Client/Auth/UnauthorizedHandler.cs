using System.Net;

namespace Odyssey.Client.Auth;

/// <summary>
/// Turns a <c>401</c> on a domain call into one signal that the session is gone (issue #250), so the
/// shell can send the user to sign in instead of leaving every page showing "Unable to load …" with a
/// Retry that can never succeed.
/// </summary>
/// <remarks>
/// <para>
/// Only requests under <c>api/</c> count. The Identity and auth surfaces sit outside it and use
/// <c>401</c> as an ordinary answer rather than a symptom: <c>POST /login</c> returns it for
/// <c>RequiresTwoFactor</c> and a bad password, and <c>GET manage/info</c> is the probe
/// <see cref="CookieAuthenticationStateProvider"/> uses to <em>learn</em> that nobody is signed in.
/// Signalling on those would bounce a user off the sign-in page mid-way through signing in.
/// </para>
/// <para>
/// The path is read relative to the API base address because that base differs by host — the API's own
/// origin in a Debug run, the client's same-origin <c>/api/</c> proxy under Compose — and the request URI
/// a handler sees is already absolute.
/// </para>
/// </remarks>
public sealed class UnauthorizedHandler(SessionExpiredNotifier notifier, Uri apiBaseAddress) : DelegatingHandler
{
    private const string DomainPrefix = "api/";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && IsDomainRequest(request.RequestUri))
        {
            notifier.NotifySessionExpired();
        }

        return response;
    }

    internal bool IsDomainRequest(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return false;
        }

        var absolute = requestUri.IsAbsoluteUri ? requestUri : new Uri(apiBaseAddress, requestUri);
        if (!apiBaseAddress.IsBaseOf(absolute))
        {
            return false;
        }

        var relative = absolute.AbsolutePath[apiBaseAddress.AbsolutePath.Length..];
        return relative.StartsWith(DomainPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
