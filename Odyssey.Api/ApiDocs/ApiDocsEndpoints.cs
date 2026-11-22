using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;

namespace Odyssey.Api.ApiDocs;

/// <summary>
/// Maps the API documentation — the OpenAPI document at <c>/openapi/v1.json</c> and the Scalar reference at
/// <c>/scalar/v1</c> — with ONE access rule for both (issue #569 §7.1).
/// </summary>
/// <remarks>
/// Under Swashbuckle the docs were middleware ahead of authentication, which is why they were anonymous. They
/// are endpoints now, so the global fallback policy would answer 401; the rule is stated here explicitly instead,
/// and the fallback policy itself is never touched.
/// </remarks>
public static class ApiDocsEndpoints
{
    public static void MapApiDocs(this WebApplication app, bool requireLogin)
    {
        ApplyAccessRule(app.MapOpenApi("/openapi/{documentName}.json"), requireLogin);
        ApplyAccessRule(app.MapScalarApiReference(ApiDocsHeaders.ScalarPathPrefix, ScalarLockdown.Apply), requireLogin);
    }

    private static void ApplyAccessRule(IEndpointConventionBuilder endpoints, bool requireLogin)
    {
        if (requireLogin)
        {
            // The default policy: any authenticated user, any role. Scalar marks its bundled assets
            // [AllowAnonymous], which would beat this, so that marker is stripped: with a login required, nothing
            // under /scalar answers an anonymous caller.
            endpoints.RequireAuthorization();
            endpoints.Finally(endpoint =>
            {
                foreach (var allowAnonymous in endpoint.Metadata.OfType<IAllowAnonymous>().ToList())
                {
                    endpoint.Metadata.Remove(allowAnonymous);
                }
            });
        }
        else
        {
            endpoints.AllowAnonymous();
        }
    }
}
