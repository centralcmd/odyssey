using Microsoft.AspNetCore.Http.Features;

namespace Odyssey.Api;

/// <summary>
/// Marks a controller whose every response — successes, errors, authorization refusals and idempotent repeats — carries
/// <c>Cache-Control: no-store</c> (issue #602 §5 "Caching"): drafts, source text and file names are never kept by a
/// browser or a proxy. Read from endpoint metadata by <see cref="NoStoreMiddleware"/>, which runs before authorization
/// so a <c>403</c> carries it too.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class NoStoreAttribute : Attribute;

public sealed class NoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<NoStoreAttribute>() is not null)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
