using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc;

namespace Odyssey.Api;

/// <summary>
/// The instance-wide half of the two-limiter export concurrency control (issue #343 §5): a singleton
/// <see cref="ConcurrencyLimiter"/> with 4 global permits, queue depth 0, shared across every request
/// regardless of which tagged action or which authenticated user it belongs to. Registered as a singleton
/// service so the same limiter instance backs every request (<see cref="ExportConcurrencyFilter"/> is
/// instantiated per-request by <c>[TypeFilter]</c>, but this type is resolved from DI as a singleton dependency).
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
/// MVC resource filter enforcing <see cref="GlobalExportConcurrencyLimiter"/>'s global ceiling AND the export
/// progress guard (issue #516 §3 component 8) — one filter for both, since both wrap the same "hold a permit for the
/// whole streamed response" scope. An <see cref="IAsyncResourceFilter"/>: it runs after authorization, before model
/// binding, and wraps result execution, so the permit is held across the whole streamed response and released in a
/// <c>finally</c> (via <c>using</c>) — including on an abort. Applied per action via
/// <c>[TypeFilter(typeof(ExportConcurrencyFilter))]</c> to every export and export summary.
/// <para>
/// The global permit is acquired <b>after</b> <c>ExportConcurrencyPolicy</c>'s per-user permit (2 per user, the
/// rate-limiter middleware runs before MVC) — see <see cref="ImportExportRateLimiting"/>'s remarks for why the order
/// matters. Rejection uses the same RFC 7807 429 shape as <see cref="IdentityRateLimiting.OnRejectedAsync"/>.
/// </para>
/// <para>
/// <b>The progress guard</b> replaced a fixed 10-minute wall-clock deadline, which made a multi-gigabyte export
/// unreachable for any client slower than about 17 MB/s. <c>Response.Body</c> is wrapped in a stream that counts the
/// bytes of each <i>completed</i> write — a write blocked by a client that is not reading is no progress — and the
/// connection is aborted when:
/// </para>
/// <list type="bullet">
/// <item>no first byte is written within <see cref="FirstByteLimit"/> (10 minutes), which covers planning;</item>
/// <item>after the first byte, a <see cref="Window"/> (5 minutes) ends with a write still waiting on the client and
/// fewer than <see cref="MinimumBytesPerWindow"/> (1 MiB) written in it — a floor of about 3.4 KiB/s that defeats a
/// trickling reader. A window that ends with no write pending is the server producing, not the client stalling, and
/// a response that finishes inside its window is never judged, so the floor is in effect
/// <c>min(1 MiB, bytes remaining)</c> and a slow reader of a small file is not aborted;</item>
/// <item>the request has run <see cref="Ceiling"/> (24 hours), a hard bound on how long a permit can be held.</item>
/// </list>
/// <para>
/// Writes are passed on in slices of at most <see cref="SliceBytes"/>, so the count advances as the client reads
/// rather than once per multi-megabyte write. Each abort logs exactly one <see cref="LogLevel.Warning"/>, naming the
/// rule, the path, the elapsed time, the bytes written and <c>X-Odyssey-Export-Rows</c> if it was already set.
/// </para>
/// </summary>
public sealed class ExportConcurrencyFilter(
    GlobalExportConcurrencyLimiter global, ILogger<ExportConcurrencyFilter> logger, TimeProvider? timeProvider = null)
    : IAsyncResourceFilter
{
    /// <summary>The wait allowed before the first byte, which covers the export's planning.</summary>
    internal static readonly TimeSpan FirstByteLimit = TimeSpan.FromMinutes(10);

    /// <summary>The window the throughput floor is measured over.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>The longest any guarded request may run.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromHours(24);

    /// <summary>How often the rules are evaluated.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    internal const long MinimumBytesPerWindow = 1024 * 1024;

    internal const int SliceBytes = 64 * 1024;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal static string EndpointName(HttpContext httpContext) => httpContext.GetEndpoint() switch
    {
        RouteEndpoint route when route.RoutePattern.RawText is { } template => template,
        { DisplayName: { } displayName } => displayName,
        _ => "(unknown endpoint)",
    };

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        using var lease = global.Limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            context.Result = RejectionResult();
            return;
        }

        var httpContext = context.HttpContext;
        var originalFeature = httpContext.Features.Get<IHttpResponseBodyFeature>()!;
        var counting = new ProgressCountingStream(originalFeature, clock);
        var guard = new ProgressGuard(counting, clock, clock.GetUtcNow(), (rule, elapsed) =>
        {
            var rows = httpContext.Response.Headers.TryGetValue("X-Odyssey-Export-Rows", out var value)
                ? value.ToString()
                : "(not yet available)";

            // Unconditional — never filtered/sampled — because the guard's thresholds are tuned against this data,
            // and repeated aborts must be distinguishable from users cancelling downloads mid-stream.
            // The endpoint is named by its server-defined route template, never by the request path, which the caller
            // controls and could use to forge log lines.
            logger.LogWarning(
                "Export progress guard ({Rule}) aborted {Endpoint} after {ElapsedMs} ms having written {Bytes} bytes. "
                + "X-Odyssey-Export-Rows: {Rows}.",
                rule, EndpointName(httpContext), (long)elapsed.TotalMilliseconds, counting.Written, rows);

            httpContext.Abort();
        });

        // The prior feature is kept behind ours, so starting the response and sending a file still reach the server.
        var countingFeature = new StreamResponseBodyFeature(counting, originalFeature);
        httpContext.Features.Set<IHttpResponseBodyFeature>(countingFeature);
        using var timer = clock.CreateTimer(_ => guard.Check(), null, CheckInterval, CheckInterval);
        try
        {
            await next();
            if (!httpContext.RequestAborted.IsCancellationRequested)
            {
                // Pushes anything a result wrote through the feature's PipeWriter into the counting stream.
                await countingFeature.CompleteAsync();
            }
        }
        finally
        {
            guard.Complete();
            httpContext.Features.Set(originalFeature);
        }
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

    /// <summary>The three abort rules, evaluated on a timer against the counting stream.</summary>
    internal sealed class ProgressGuard(
        ProgressCountingStream counting, TimeProvider clock, DateTimeOffset started, Action<string, TimeSpan> abort)
    {
        private readonly Lock gate = new();
        private DateTimeOffset windowStart;
        private long windowStartBytes;
        private bool done;

        public void Check()
        {
            lock (gate)
            {
                if (done)
                {
                    return;
                }

                var now = clock.GetUtcNow();
                var elapsed = now - started;
                string? rule = null;
                if (elapsed >= Ceiling)
                {
                    rule = "24-hour ceiling";
                }
                else if (counting.FirstWriteAt is not { } first)
                {
                    if (elapsed >= FirstByteLimit)
                    {
                        rule = "no first byte in 10 minutes";
                    }
                }
                else
                {
                    if (windowStart < first)
                    {
                        windowStart = first;
                        windowStartBytes = 0;
                    }

                    if (now - windowStart >= Window)
                    {
                        var inWindow = counting.Written - windowStartBytes;
                        if (counting.WritePending && inWindow < MinimumBytesPerWindow)
                        {
                            rule = "under 1 MiB in 5 minutes";
                        }
                        else
                        {
                            windowStart = now;
                            windowStartBytes = counting.Written;
                        }
                    }
                }

                if (rule is not null)
                {
                    done = true;
                    abort(rule, elapsed);
                }
            }
        }

        public void Complete()
        {
            lock (gate)
            {
                done = true;
            }
        }
    }

    /// <summary>
    /// A write-through wrapper over the response body that records completed writes. Reads and seeks are not
    /// supported, as on the response body itself. The server's stream is fetched at the first write, and a flush
    /// before it is dropped, so nothing here starts the response before the export writes its first byte.
    /// </summary>
    internal sealed class ProgressCountingStream(IHttpResponseBodyFeature server, TimeProvider clock) : Stream
    {
        private Stream? innerStream;

        private long written;
        private int pending;

        private Stream Inner => innerStream ??= server.Stream;
        private long firstWriteTicks = -1;

        public long Written => Interlocked.Read(ref written);

        public bool WritePending => Volatile.Read(ref pending) > 0;

        public DateTimeOffset? FirstWriteAt
        {
            get
            {
                var ticks = Interlocked.Read(ref firstWriteTicks);
                return ticks < 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref pending);
            try
            {
                for (var offset = 0; offset < buffer.Length; offset += SliceBytes)
                {
                    var slice = buffer.Slice(offset, Math.Min(SliceBytes, buffer.Length - offset));
                    await Inner.WriteAsync(slice, cancellationToken);
                    Completed(slice.Length);
                }
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref pending);
            try
            {
                Inner.Write(buffer, offset, count);
                Completed(count);
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        }

        // A flush before the first write is not passed on: flushing the server's stream starts the response, which would stop
        // an error raised before the first byte from becoming a ProblemDetails response.
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            innerStream is null ? Task.CompletedTask : innerStream.FlushAsync(cancellationToken);

        public override void Flush() => innerStream?.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Completed(int count)
        {
            if (count <= 0)
            {
                return;
            }

            Interlocked.CompareExchange(ref firstWriteTicks, clock.GetUtcNow().UtcTicks, -1);
            Interlocked.Add(ref written, count);
        }
    }
}
