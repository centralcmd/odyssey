using System.Net;
using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Auth;

/// <summary>
/// Turns a <c>451</c> from anywhere in the app into a redirect to the acceptance interstitial
/// (issue #354 §5).
/// </summary>
/// <remarks>
/// <para>
/// This — not <c>MainLayout</c>'s gate check — is what actually surfaces a <em>mid-session</em>
/// compliance flip. <c>MainLayout</c> resolves the claim once per full page load and does not re-fire
/// on in-app navigation, so a user whose session is revalidated after an admin publishes a new ToS
/// would otherwise just watch calls fail with no explanation. Living in the shared
/// <see cref="HttpClient"/> pipeline means every typed client is covered by construction, including
/// background writers like <c>PageStateService</c>, rather than each page having to handle the status
/// itself.
/// </para>
/// <para>
/// It carries <c>returnUrl</c> so an interruption returns the user to the page they were on rather
/// than dumping them on their default destination.
/// </para>
/// <para>
/// The self-reference guard is deliberately its own check rather than a reliance on the server's
/// allowlist happening to cover everything the interstitial calls: if it didn't, this handler would
/// redirect <c>/accept-terms</c> to itself on every request and spin.
/// </para>
/// </remarks>
public sealed class LegalComplianceHandler(NavigationManager navigation) : DelegatingHandler
{
    /// <summary>The interstitial's route — also the path this handler must never redirect away from.</summary>
    public const string InterstitialPath = "/accept-terms";

    /// <summary>The machine-readable marker the API's gate middleware puts on its 451 problem document.</summary>
    public const string ProblemCode = "LEGAL_ACCEPTANCE_REQUIRED";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnavailableForLegalReasons)
        {
            Redirect();
        }

        return response;
    }

    private void Redirect()
    {
        var current = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        if (IsInterstitial(current))
        {
            return;
        }

        navigation.NavigateTo($"{InterstitialPath}?returnUrl={Uri.EscapeDataString(current)}");
    }

    private static bool IsInterstitial(string relativeUri)
    {
        var path = relativeUri.Split('?', 2)[0].TrimEnd('/');
        return path.Equals(InterstitialPath, StringComparison.OrdinalIgnoreCase);
    }
}
