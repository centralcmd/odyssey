using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc;

namespace Odyssey.Api;

/// <summary>
/// The instance-wide half of the two-limiter export concurrency control (issue #343 §5): a singleton
/// <see cref="ConcurrencyLimiter"/> with 4 global permits, queue depth 0, shared across every request
/// regardless of which of the five tagged actions or which authenticated user it belongs to. Registered
/// as a singleton service so the same limiter instance backs every request
/// (<see cref="ExportConcurrencyFilter"/> is instantiated per-request by <c>[TypeFilter]</c>, but this
/// type is resolved from DI as a singleton dependency).
/// </summary>
public sealed class GlobalExportConcurrencyLimiter
{
    public ConcurrencyLimiter Limiter { get; } = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 4,
        QueueLimit = 0,
    });
}

/// <summary>
/// MVC resource filter enforcing <see cref="GlobalExportConcurrencyLimiter"/>'s global ceiling AND the
/// hard wall-clock export deadline (issue #343 §5/§12) — one filter for both, since both wrap the same
/// "hold a permit for the whole streamed response" scope. An <see cref="IAsyncResourceFilter"/>, not a
/// rate-limiter policy or a minimal-API endpoint filter: it runs after authorization, before model
/// binding, and wraps result execution, so the permit is held across the whole streamed response and
/// released in a <c>finally</c> (via <c>using</c>) — including on the deadline abort below. Applied
/// per-action via <c>[TypeFilter(typeof(ExportConcurrencyFilter))]</c> because <c>MapControllers()</c>
/// exposes no narrower scoping mechanism (an endpoint filter would apply to every controller action in
/// the app, not just the five bulk-export ones — and isn't reachable from MVC actions in the first
/// place, see §5).
/// <para>
/// The global permit is acquired <b>after</b> <c>ExportConcurrencyPolicy</c>'s per-user rate-limiter
/// permit (the rate-limiter middleware runs before MVC) — see <see cref="ImportExportRateLimiting"/>'s
/// remarks for why the acquisition order matters. Rejection uses the same RFC 7807 429 shape as
/// <see cref="IdentityRateLimiting.OnRejectedAsync"/> so the two 429s a client can receive from one
/// export endpoint are identical in body shape.
/// </para>
/// <para>
/// The deadline is measured from this filter's entry — effectively request start, since it runs
/// immediately after authorization/authentication and before anything else in the action pipeline —
/// not from the first byte written, so the pre-stream row count and the chunked fetch's snapshot
/// transaction are inside the budget, not free time outside it (sec W2). On expiry the connection is
/// aborted (which also cancels the <see cref="HttpContext.RequestAborted"/> token every downstream
/// service call observes) and exactly one <see cref="LogLevel.Warning"/> is logged, unconditionally,
/// naming the request path, elapsed time, and the export's row count if the response had already
/// reached the point of announcing it via <c>X-Odyssey-Export-Rows</c>.
/// </para>
/// </summary>
public sealed class ExportConcurrencyFilter(GlobalExportConcurrencyLimiter global, ILogger<ExportConcurrencyFilter> logger)
    : IAsyncResourceFilter
{
    /// <summary>
    /// An internal/operational constant under §6's boundary rule, not a business-volume cap an operator
    /// reasons about — so it is not a <c>/settings</c> field alongside the sixteen this feature makes
    /// configurable (issue #343 §12, sec W3).
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        using var lease = global.Limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            context.Result = RejectionResult();
            return;
        }

        var httpContext = context.HttpContext;
        var stopwatch = Stopwatch.StartNew();
        using var deadlineCts = new CancellationTokenSource(Deadline);

        await using var registration = deadlineCts.Token.Register(() =>
        {
            var rows = httpContext.Response.Headers.TryGetValue("X-Odyssey-Export-Rows", out var value)
                ? value.ToString()
                : "(not yet available)";

            // Unconditional — never filtered/sampled — because the deadline value is tuned against
            // this data, and repeated aborts must be distinguishable from users cancelling downloads
            // mid-stream (issue #343 §11).
            logger.LogWarning(
                "Export deadline ({DeadlineMinutes} min) elapsed for {Path} after {ElapsedMs} ms; " +
                "aborting the response. X-Odyssey-Export-Rows: {Rows}.",
                Deadline.TotalMinutes, httpContext.Request.Path, stopwatch.ElapsedMilliseconds, rows);

            httpContext.Abort();
        });

        await next();
    }

    private static ObjectResult RejectionResult()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = ReasonPhrases.GetReasonPhrase(StatusCodes.Status429TooManyRequests),
            Detail = "Too many concurrent exports. Please wait a moment before trying again.",
        };

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentTypes = { "application/problem+json" },
        };
    }
}
