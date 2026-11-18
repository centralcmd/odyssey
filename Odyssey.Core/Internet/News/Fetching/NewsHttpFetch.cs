using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>How one GET (with its redirect chain) ended.</summary>
public enum NewsHttpOutcome
{
    /// <summary>A final, non-redirect response whose body was read within the cap.</summary>
    Body = 0,

    NotModified = 1,

    /// <summary>A failure classified as <see cref="NewsHttpResult.ErrorKind"/>.</summary>
    Failed = 2,

    /// <summary>A hop's host was busy (§3.6): a transient skip, not a failure.</summary>
    Throttled = 3,
}

/// <summary>The result of <see cref="NewsHttpFetch.GetAsync"/>. No field carries a URL path or query: the message names the host only.</summary>
public sealed record NewsHttpResult
{
    public required NewsHttpOutcome Outcome { get; init; }

    public NewsFeedErrorKind ErrorKind { get; init; }

    public int? HttpStatus { get; init; }

    public string? ErrorMessage { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public byte[] Body { get; init; } = [];

    public Uri? FinalUrl { get; init; }

    public string? Charset { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public string? SuggestedUrl { get; init; }

    public bool RedirectedFromHttps { get; init; }
}

/// <summary>
/// One news GET through the egress client (issue #470 §3.4 step 3, §6.2, §7.9): manual redirects, at most five hops
/// actually requested and every hop re-checked by the policy; each hop takes its own host's throttle slot first; the
/// body read and decompressed under its cap. Every failure is classified to an error kind, never surfaced as text from
/// the response or the exception.
/// </summary>
public static class NewsHttpFetch
{
    /// <param name="timeout">The whole request chain's limit (30 s for a feed, 15 s for an image); its expiry is <c>Timeout</c>.</param>
    /// <param name="stoppingToken">The host stopping: rethrown as cancellation, never classified.</param>
    /// <param name="takeHost">
    /// Takes a hop's host slot: called for every request after the first (the claim took the first one), and returns
    /// false when the host stays busy.
    /// </param>
    public static async Task<NewsHttpResult> GetAsync(
        HttpClient client,
        Uri url,
        NewsEgressContext context,
        string? etag,
        string? lastModified,
        long maxBodyBytes,
        Func<string, CancellationToken, Task<bool>> takeHost,
        bool takeFirstHost,
        TimeSpan timeout,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(takeHost);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        limit.CancelAfter(timeout);
        var cancellationToken = limit.Token;
        var current = url;
        var state = NewsRedirectState.Start;
        var redirectedFromHttps = false;
        var first = true;
        while (true)
        {
            var host = NewsUrlKey.CanonicalHost(current) ?? context.FeedHost;
            if ((!first || takeFirstHost) && !await takeHost(host, stoppingToken))
            {
                return new NewsHttpResult { Outcome = NewsHttpOutcome.Throttled, SuggestedUrl = state.SuggestedUrl };
            }

            first = false;
            HttpResponseMessage response;
            try
            {
                using var request = NewsEgress.CreateRequest(current, context, etag, lastModified);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                return Classify(ex, host);
            }

            using (response)
            {
                var step = NewsRedirects.Next(current, response.StatusCode, response.Headers.Location, context, state);
                switch (step.Outcome)
                {
                    case NewsRedirectOutcome.Follow:
                        redirectedFromHttps |= current.Scheme == Uri.UriSchemeHttps && step.Next!.Scheme == Uri.UriSchemeHttp;
                        current = step.Next!;
                        context = step.Context;
                        state = step.State;
                        continue;
                    case NewsRedirectOutcome.TooManyRedirects:
                        return Failed(NewsFeedErrorKind.TooManyRedirects, host, (int)response.StatusCode, $"{host} redirected too many times.");
                    case NewsRedirectOutcome.InvalidLocation:
                        return Failed(NewsFeedErrorKind.HttpStatus, host, (int)response.StatusCode, $"{host} sent a redirect without a usable address.");
                    case NewsRedirectOutcome.DowngradeRefused:
                        return Failed(NewsFeedErrorKind.BlockedAddress, host, (int)response.StatusCode, $"{host} redirected from https to http.");
                }

                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new NewsHttpResult
                    {
                        Outcome = NewsHttpOutcome.NotModified, HttpStatus = status, FinalUrl = current, SuggestedUrl = state.SuggestedUrl,
                        RedirectedFromHttps = redirectedFromHttps, ETag = Header(response, "ETag"), LastModified = Header(response, "Last-Modified"),
                    };
                }

                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    return Failed(NewsFeedErrorKind.Gone, host, status, $"{host} says the feed is gone (HTTP 410).") with { SuggestedUrl = state.SuggestedUrl };
                }

                if (status is < 200 or > 299)
                {
                    return Failed(NewsFeedErrorKind.HttpStatus, host, status, $"{host} answered HTTP {status}.") with
                    {
                        RetryAfter = RetryAfterOf(response),
                        SuggestedUrl = state.SuggestedUrl,
                    };
                }

                NewsBodyResult body;
                try
                {
                    body = await NewsBodyReader.ReadAsync(response.Content, maxBodyBytes, cancellationToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    return Classify(ex, host);
                }

                return body.Outcome switch
                {
                    NewsBodyOutcome.Ok => new NewsHttpResult
                    {
                        Outcome = NewsHttpOutcome.Body,
                        HttpStatus = status,
                        Body = body.Body,
                        FinalUrl = current,
                        Charset = response.Content.Headers.ContentType?.CharSet,
                        ETag = Header(response, "ETag"),
                        LastModified = Header(response, "Last-Modified"),
                        SuggestedUrl = state.SuggestedUrl,
                        RedirectedFromHttps = redirectedFromHttps,
                    },
                    NewsBodyOutcome.TooLarge => Failed(NewsFeedErrorKind.TooLarge, host, status, $"{host} sent more than the size limit."),
                    NewsBodyOutcome.UnsupportedEncoding => Failed(NewsFeedErrorKind.Unsupported, host, status, $"{host} used an unsupported content encoding."),
                    _ => Failed(NewsFeedErrorKind.ParseError, host, status, $"{host} sent a corrupt compressed body."),
                };
            }
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()
            : null;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };

    private static NewsHttpResult Failed(NewsFeedErrorKind kind, string host, int? status, string message) => new()
    {
        Outcome = NewsHttpOutcome.Failed,
        ErrorKind = kind,
        HttpStatus = status,
        ErrorMessage = NewsText.Clamp(message, Dtos.NewsLimits.MaxErrorMessageLength),
    };

    /// <summary>A send or read exception as an error kind: the policy's refusal, a timeout, TLS, or the network.</summary>
    public static NewsHttpResult Classify(Exception exception, string host)
    {
        if (NewsEgress.RefusalOf(exception) != NewsEgressRefusal.None)
        {
            return Failed(NewsFeedErrorKind.BlockedAddress, host, null, $"{host} is not an address the news reader may reach.");
        }

        if (exception is OperationCanceledException)
        {
            return Failed(NewsFeedErrorKind.Timeout, host, null, $"{host} did not answer in time.");
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return Failed(NewsFeedErrorKind.Tls, host, null, $"The secure connection to {host} failed.");
            }

            if (current is TimeoutException)
            {
                return Failed(NewsFeedErrorKind.Timeout, host, null, $"{host} did not answer in time.");
            }
        }

        return exception is HttpRequestException or IOException or SocketException
            ? Failed(NewsFeedErrorKind.Network, host, null, $"{host} could not be reached.")
            : Failed(NewsFeedErrorKind.Network, host, null, $"Fetching from {host} failed.");
    }
}
