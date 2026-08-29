using System.Net;
using System.Text.Json;

namespace Odyssey.Client.Auth;

/// <summary>
/// Turns the API's "this account owes a password change" <c>403</c> into one redirect to the forced-reset
/// gate (issue #406 §7), from anywhere in the app.
/// </summary>
/// <remarks>
/// <para>
/// Living in the shared <see cref="HttpClient"/> pipeline covers every typed client by construction —
/// including background writers like <c>PageStateService</c> — rather than making ~200 call sites each
/// check for it. It sits in <c>Odyssey.Client</c>, not the library, for the same reason
/// <c>LegalComplianceHandler</c> does: deciding that a status becomes a navigation is a presentation
/// decision, and <c>Odyssey.ApiClient</c> returns results rather than acting on them.
/// </para>
/// <para>
/// <b>Two mechanics here are not incidental.</b> An ordinary permission-denied <c>403</c> is common in this
/// app (a Guest reaching an endpoint the client didn't hide), so the body has to be inspected to tell one
/// from the other — and <c>OdysseyApi</c> parses that same body itself, once, after every handler has
/// returned. So:
/// </para>
/// <list type="number">
/// <item><description>
/// Return immediately unless the status is <c>403</c>. A success response is never speculatively read —
/// file and photo downloads stream with <c>ResponseHeadersRead</c> precisely so they are never fully
/// materialised, and buffering one here would both waste the memory and break the download.
/// </description></item>
/// <item><description>
/// Read the <b>bytes</b>, not the stream. <c>ReadProblemAsync</c> — the obvious reuse — goes through
/// <c>ReadFromJsonAsync</c>, which disposes the content's cached read stream when it finishes; the
/// caller's own parse would then throw <c>ObjectDisposedException</c> and <em>every</em> <c>403</c>
/// app-wide would surface "Cannot access a closed Stream" instead of its real detail.
/// <c>ReadAsByteArrayAsync</c> buffers and returns a copy, leaving the response exactly as the caller
/// expects to find it.
/// </description></item>
/// </list>
/// </remarks>
public sealed class PasswordChangeRequiredHandler(PasswordChangeRequiredNotifier notifier) : DelegatingHandler
{
    /// <summary>The gate page a refused request routes to.</summary>
    public const string GatePath = "/change-password-required";

    /// <summary>The machine-readable marker the API's middleware puts on its 403 problem document.</summary>
    public const string ProblemCode = "password_change_required";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return response;
        }

        if (await IsPasswordChangeRequiredAsync(response, cancellationToken))
        {
            notifier.NotifyPasswordChangeRequired();
        }

        return response;
    }

    private static async Task<bool> IsPasswordChangeRequiredAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // Buffers the content and hands back a copy, so the caller's own parse still sees an intact,
            // undisposed response — see the class remarks.
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (payload.Length == 0)
            {
                return false;
            }

            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), ProblemCode, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // A 403 whose body isn't a problem document (a proxy's HTML error page, say) is simply not
            // this one. Never let inspecting a failure turn into a second, more confusing failure.
            return false;
        }
    }
}
