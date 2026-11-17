using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace Odyssey.Api;

/// <summary>
/// The single OPML-import permit (issue #508 §3, B2): a singleton 1-permit <see cref="ConcurrencyLimiter"/> with queue
/// depth 0, in the <see cref="GlobalExportConcurrencyLimiter"/> pattern. Per API process — Odyssey runs one API replica,
/// and <c>docs/deployment.md</c> says the feed-limit bound is per replica if that ever changes.
/// </summary>
public sealed class NewsOpmlImportLimiter
{
    public ConcurrencyLimiter Limiter { get; } = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 1,
        QueueLimit = 0,
    });
}

/// <summary>
/// One OPML import at a time per process (issue #508 §5.2). A resource filter, so it runs after authorization and
/// BEFORE model binding reads the multipart body, behind the shared <c>ImportConcurrencyPolicy</c> permit the rate
/// limiter took first. A refusal is a <c>429</c> that ends the request, which releases the shared permit too. The
/// permit is released when the lease is disposed, which covers an aborted upload and an exception (implementation
/// notes, item 2).
/// </summary>
public sealed class NewsOpmlImportConcurrencyFilter(NewsOpmlImportLimiter limiter) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        using var lease = limiter.Limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
                Detail = "An OPML import is already running. Please wait for it to finish before starting another.",
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
                ContentTypes = { "application/problem+json" },
            };
            return;
        }

        await next();
    }
}
