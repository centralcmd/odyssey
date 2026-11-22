using Microsoft.Net.Http.Headers;
using Scalar.AspNetCore;

namespace Odyssey.Api.ApiDocs;

/// <summary>
/// The response headers of the API documentation routes (issue #569 §7.3). The API sends no CSP of its own, so
/// on its own port (:5188) the Scalar page would otherwise run with none; behind nginx the browser enforces this
/// policy together with the client's.
/// </summary>
/// <remarks>
/// Set from a <c>Response.OnStarting</c> callback registered BEFORE the endpoint runs: Scalar writes the HTML and
/// generates its nonce in one handler, so a header added after it would be missing silently. Only an HTML
/// response gets the full set; the bundled assets get <c>nosniff</c> alone, so they stay cacheable.
/// </remarks>
public static class ApiDocsHeaders
{
    public const string ScalarPathPrefix = "/scalar";

    public const string OpenApiPathPrefix = "/openapi";

    /// <summary>The policy; <c>{0}</c> is the script source list.</summary>
    internal const string ContentSecurityPolicyFormat =
        "default-src 'none'; script-src {0}; style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
        "img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; " +
        "frame-ancestors 'none'; object-src 'none'";

    public static IApplicationBuilder UseApiDocsHeaders(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiDocsHeaders));
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // Scalar serves /scalar/v1/ too, but its script tags are relative ("scalar.js"), so from a trailing-slash
            // URL they resolve under /scalar/v1/ and the page never loads. Redirect to the slash-less page with a
            // RELATIVE Location: behind nginx the API sees /scalar/v1/ while the browser is at /api/scalar/v1/, and an
            // absolute path would drop the /api prefix.
            if (path.StartsWithSegments(ScalarPathPrefix, out var rest)
                && rest.Value is { Length: > 1 } tail
                && tail.EndsWith('/')
                && tail.IndexOf('/', 1) == tail.Length - 1)
            {
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                // The segment arrives decoded; re-encode it so a %3F or %23 in it cannot become a query or a fragment.
                context.Response.Headers.Location = "../" + Uri.EscapeDataString(tail[1..^1]) + context.Request.QueryString;
                return;
            }

            if (path.StartsWithSegments(ScalarPathPrefix) || path.StartsWithSegments(OpenApiPathPrefix))
            {
                context.Response.OnStarting(() =>
                {
                    Apply(context, logger);
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }

    internal static void Apply(HttpContext context, ILogger logger)
    {
        var headers = context.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";

        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        // Fail closed: without a nonce the page's scripts are refused and it does not render, but the response is
        // never left without a policy.
        var scriptSources = "'self'";
        if (context.Items.TryGetValue(ScalarOptions.NonceHttpContextItemKey, out var value)
            && value is string nonce && nonce.Length > 0)
        {
            scriptSources = $"'self' 'nonce-{nonce}'";
        }
        else
        {
            logger.LogWarning("The API documentation page carried no script nonce; its CSP admits no inline script.");
        }

        headers[HeaderNames.ContentSecurityPolicy] = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, ContentSecurityPolicyFormat, scriptSources);
        headers[HeaderNames.CacheControl] = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
    }
}
