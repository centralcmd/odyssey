using Scalar.AspNetCore;

namespace Odyssey.Api.ApiDocs;

/// <summary>
/// The ONLY place <see cref="ScalarOptions"/> is configured (issue #569 §7.2). Every Scalar feature that contacts a
/// third party is turned off here, in code, with no configuration input: this type deliberately takes no
/// configuration, options or environment, so nothing set at deploy time can turn one back on. A source lint in
/// <c>Odyssey.Api.Tests</c> enforces both halves.
/// </summary>
/// <remarks>
/// What each line closes, as found by inspecting the 3.1.1 bundle:
/// <list type="bullet">
/// <item>The bundle is served from the package itself; neither <c>CdnUrl</c> nor <c>BundleUrl</c> is set.</item>
/// <item>Default fonts load from fonts.scalar.com — off.</item>
/// <item>Agent Scalar (AI chat) is on without a key on localhost-like hosts and posts the whole document to
/// api.scalar.com — off.</item>
/// <item>MCP integration and telemetry — off.</item>
/// <item>No <c>ProxyUrl</c>: a proxy would carry every "try it" request, session cookie included.</item>
/// <item>The "open in API client" button navigates to client.scalar.com with the document URL — hidden.</item>
/// <item>The "Powered by Scalar" footer link has no option; it is hidden by CSS.</item>
/// <item>The share/upload action has no option; the docs CSP's <c>connect-src 'self'</c> blocks it
/// (<see cref="ApiDocsHeaders"/>).</item>
/// </list>
/// </remarks>
public static class ScalarLockdown
{
    public const string Title = "Odyssey API";

    internal const string PoweredByLinkCss = "a[href^=\"https://scalar.com/\"] { display: none !important; }";

    public static void Apply(ScalarOptions options)
    {
        options.Title = Title;

        // Relative, so behind nginx's /api/ prefix the page fetches /api/openapi/v1.json rather than falling
        // through to the SPA at /openapi/v1.json.
        options.OpenApiRoutePattern = "openapi/{documentName}.json";
        options.AddDocument(OpenApiDocumentRegistration.DocumentName);

        // "Try it" goes to the page's own origin and base path; the document carries no servers (see
        // OpenApiDocumentRegistration), so nothing reflected from a Host header can redirect it.
        options.DynamicBaseServerUrl = true;
        options.ProxyUrl = null;

        options.DisableDefaultFonts();
        options.DisableAgent();
        options.DisableMcp();
        options.DisableTelemetry();
        options.HideClientButton = true;
        options.ShowDeveloperTools = DeveloperToolsVisibility.Never;
        options.PersistentAuthentication = false;

        // A fresh nonce per request on every <script>, published on HttpContext.Items for the CSP.
        options.DynamicNonce = true;

        // The sidebar footer's "Powered by Scalar" link to scalar.com has no option. Hidden here so the page offers
        // no outbound navigation; were it clicked anyway, Referrer-Policy: no-referrer strips the docs URL.
        options.CustomCss = PoweredByLinkCss;
    }
}
