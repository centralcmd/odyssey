using System.Net;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>Why a streamed body stopped before its end (issue #556 §3.5). Every reason is transient.</summary>
public enum NewsStreamAbort
{
    /// <summary>No byte arrived within <see cref="NewsStreamFetch.StallTimeout"/>.</summary>
    Stall = 1,

    /// <summary>A 2-minute window after the first minute averaged under <see cref="NewsStreamFetch.FloorBytesPerSecond"/>.</summary>
    TooSlow = 2,

    /// <summary>The whole download passed its total deadline.</summary>
    Deadline = 3,
}

/// <summary>A streamed body that stopped for one of the <see cref="NewsStreamAbort"/> reasons; carries no URL.</summary>
public sealed class NewsStreamAbortedException(NewsStreamAbort reason)
    : IOException($"The download stopped: {reason}.")
{
    public NewsStreamAbort Reason { get; } = reason;
}

/// <summary>An opened stream: the response headers were good and the body is ready to read.</summary>
public sealed class NewsOpenedStream : IAsyncDisposable
{
    private readonly HttpResponseMessage response;
    private readonly CancellationTokenSource deadline;

    internal NewsOpenedStream(HttpResponseMessage response, Stream body, CancellationTokenSource deadline, long? contentLength, string finalHost)
    {
        this.response = response;
        this.deadline = deadline;
        Body = body;
        ContentLength = contentLength;
        FinalHost = finalHost;
    }

    /// <summary>The body, enforcing the stall timeout, the throughput floor and the total deadline on every read.</summary>
    public Stream Body { get; }

    public long? ContentLength { get; }

    /// <summary>The canonical host of the final URL (for logs); never the URL itself.</summary>
    public string FinalHost { get; }

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        response.Dispose();
        deadline.Dispose();
    }
}

/// <summary>The result of <see cref="NewsStreamFetch.OpenAsync"/>: an opened stream, or a classified failure.</summary>
public sealed record NewsStreamResult
{
    public required NewsHttpOutcome Outcome { get; init; }

    public NewsFeedErrorKind ErrorKind { get; init; }

    public int? HttpStatus { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    /// <summary>A <c>Content-Encoding</c> other than <c>identity</c> was sent (§3.5: refused).</summary>
    public bool EncodingRefused { get; init; }

    public NewsOpenedStream? Stream { get; init; }
}

/// <summary>
/// The streaming egress entry point (issue #556 §3.5), for podcast audio only. Same connect-time check, policy and
/// per-hop throttle as <see cref="NewsHttpFetch.GetAsync"/>, but: a caller-supplied redirect cap (10 for audio),
/// <c>Accept-Encoding: identity</c> enforced, no conditional validators, and the response handed back unread. The body
/// is bounded by time rather than by a buffer: response headers within 30 s, 60 s without a byte, a 32 KiB/s floor
/// over each 2-minute window after the first minute, and a total deadline of
/// <c>min(60 min, 2 min + expected ÷ 32 KiB/s)</c>, where <c>expected</c> is the <c>Content-Length</c> or the caller's
/// fallback (the effective episode cap).
/// </summary>
public static class NewsStreamFetch
{
    public const long FloorBytesPerSecond = 32 * 1024;

    public static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan FloorGrace = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan FloorWindow = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DeadlineBase = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DeadlineMax = TimeSpan.FromMinutes(60);

    /// <summary><c>min(60 min, 2 min + expected ÷ 32 KiB/s)</c>.</summary>
    public static TimeSpan TotalDeadline(long expectedBytes)
    {
        var seconds = Math.Max(0, expectedBytes) / (double)FloorBytesPerSecond;
        return seconds >= DeadlineMax.TotalSeconds ? DeadlineMax : TimeSpan.FromSeconds(Math.Min(DeadlineMax.TotalSeconds, DeadlineBase.TotalSeconds + seconds));
    }

    public static async Task<NewsStreamResult> OpenAsync(
        HttpClient client,
        Uri url,
        NewsEgressContext context,
        int maxRedirects,
        long fallbackExpectedBytes,
        Func<string, CancellationToken, Task<bool>> takeHost,
        bool takeFirstHost,
        TimeProvider time,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(takeHost);
        ArgumentNullException.ThrowIfNull(time);
        var started = time.GetTimestamp();
        var current = url;
        var state = NewsRedirectState.Start;
        var first = true;
        while (true)
        {
            var host = NewsUrlKey.CanonicalHost(current) ?? context.FeedHost;
            if ((!first || takeFirstHost) && !await takeHost(host, stoppingToken))
            {
                return new NewsStreamResult { Outcome = NewsHttpOutcome.Throttled };
            }

            first = false;
            HttpResponseMessage response;
            using (var headers = new CancellationTokenSource(HeadersTimeout, time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, headers.Token))
            {
                try
                {
                    using var request = NewsEgress.CreateRequest(current, context, identityEncoding: true);
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    return From(NewsHttpFetch.Classify(ex, host));
                }
            }

            var keep = false;
            try
            {
                var step = NewsRedirects.Next(current, response.StatusCode, response.Headers.Location, context, state, maxRedirects);
                switch (step.Outcome)
                {
                    case NewsRedirectOutcome.Follow:
                        current = step.Next!;
                        context = step.Context;
                        state = step.State;
                        continue;
                    case NewsRedirectOutcome.TooManyRedirects:
                        return Failed(NewsFeedErrorKind.TooManyRedirects, response);
                    case NewsRedirectOutcome.InvalidLocation:
                        return Failed(NewsFeedErrorKind.HttpStatus, response);
                    case NewsRedirectOutcome.DowngradeRefused:
                        return Failed(NewsFeedErrorKind.BlockedAddress, response);
                }

                var status = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                {
                    return Failed(NewsFeedErrorKind.Gone, response);
                }

                if (status is < 200 or > 299)
                {
                    return Failed(NewsFeedErrorKind.HttpStatus, response) with { RetryAfter = RetryAfterOf(response, time) };
                }

                if (response.Content.Headers.ContentEncoding.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
                {
                    return Failed(NewsFeedErrorKind.Unsupported, response) with { EncodingRefused = true };
                }

                var length = response.Content.Headers.ContentLength;
                var deadline = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
                var remaining = TotalDeadline(length ?? fallbackExpectedBytes) - time.GetElapsedTime(started);
                deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                Stream raw;
                try
                {
                    raw = await response.Content.ReadAsStreamAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    deadline.Dispose();
                    return From(NewsHttpFetch.Classify(ex, host));
                }

                keep = true;
                return new NewsStreamResult
                {
                    Outcome = NewsHttpOutcome.Body,
                    HttpStatus = status,
                    Stream = new NewsOpenedStream(
                        response, new PacedStream(raw, time, deadline.Token, stoppingToken), deadline, length, host),
                };
            }
            finally
            {
                if (!keep)
                {
                    response.Dispose();
                }
            }
        }
    }

    private static NewsStreamResult From(NewsHttpResult failure) => new()
    {
        Outcome = NewsHttpOutcome.Failed,
        ErrorKind = failure.ErrorKind,
        HttpStatus = failure.HttpStatus,
    };

    private static NewsStreamResult Failed(NewsFeedErrorKind kind, HttpResponseMessage response) => new()
    {
        Outcome = NewsHttpOutcome.Failed,
        ErrorKind = kind,
        HttpStatus = (int)response.StatusCode,
    };

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response, TimeProvider time) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - time.GetUtcNow() is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };

    /// <summary>
    /// A read-only wrapper enforcing the stall timeout, the throughput floor and the total deadline. A breach throws
    /// <see cref="NewsStreamAbortedException"/>; the host stopping surfaces as ordinary cancellation.
    /// </summary>
    internal sealed class PacedStream(Stream inner, TimeProvider time, CancellationToken deadline, CancellationToken stopping) : Stream
    {
        private readonly long started = time.GetTimestamp();
        private long windowStart = -1;
        private long windowBytes;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (deadline.IsCancellationRequested)
            {
                throw new NewsStreamAbortedException(NewsStreamAbort.Deadline);
            }

            using var stall = new CancellationTokenSource(StallTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping, deadline, stall.Token);
            int read;
            try
            {
                read = await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                throw new NewsStreamAbortedException(deadline.IsCancellationRequested ? NewsStreamAbort.Deadline : NewsStreamAbort.Stall);
            }

            Account(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read asynchronously.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>The floor: windows start once the first minute has passed; each must average the floor.</summary>
        private void Account(int read)
        {
            var now = time.GetTimestamp();
            if (time.GetElapsedTime(started, now) < FloorGrace)
            {
                return;
            }

            if (windowStart < 0)
            {
                windowStart = now;
                windowBytes = read;
                return;
            }

            windowBytes += read;
            var elapsed = time.GetElapsedTime(windowStart, now);
            if (elapsed < FloorWindow)
            {
                return;
            }

            if (windowBytes < FloorBytesPerSecond * elapsed.TotalSeconds)
            {
                throw new NewsStreamAbortedException(NewsStreamAbort.TooSlow);
            }

            windowStart = now;
            windowBytes = 0;
        }
    }
}
