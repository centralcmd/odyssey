using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Odyssey.Core.Internet.News.Fetching;

/// <summary>Resolves a host name. Injected so the connect-time check is testable, rebinding included.</summary>
public interface INewsDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The system resolver.</summary>
public sealed class SystemNewsDnsResolver : INewsDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Thrown by the connect-time check when the policy refuses a connection. <c>SocketsHttpHandler</c> wraps it in an
/// <see cref="HttpRequestException"/>; <see cref="NewsEgress.RefusalOf"/> finds it again.
/// </summary>
public sealed class NewsEgressRefusedException(NewsEgressRefusal refusal)
    : Exception($"The news egress policy refused the connection ({refusal}).")
{
    public NewsEgressRefusal Refusal { get; } = refusal;
}

/// <summary>
/// The connect-time check (§7.9): resolves the host ONCE per connection, applies the address policy to every address
/// it returns (one blocked address refuses the host), and connects only to a vetted address — so a DNS answer that
/// changes between the check and the connect (rebinding) cannot redirect the socket. The request's own
/// <see cref="NewsEgressContext"/> decides; a request without one is refused (fail closed).
/// </summary>
public sealed class NewsConnectGuard(INewsDnsResolver resolver)
{
    /// <summary>The <c>SocketsHttpHandler.ConnectCallback</c>.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.InitialRequestMessage.Options.TryGetValue(NewsEgress.ContextKey, out var context))
        {
            throw new NewsEgressRefusedException(NewsEgressRefusal.NotAWebUrl);
        }

        var host = connection.DnsEndPoint.Host.Trim('[', ']').ToLowerInvariant();
        var port = connection.DnsEndPoint.Port;
        var addresses = await VetAsync(host, port, context, cancellationToken);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Resolves and vets a host: every address must pass, or the host is refused.</summary>
    public async Task<IPAddress[]> VetAsync(string host, int port, NewsEgressContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var portRefusal = NewsEgressPolicy.CheckPort(host, port, context);
        if (portRefusal != NewsEgressRefusal.None)
        {
            throw new NewsEgressRefusedException(portRefusal);
        }

        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await resolver.ResolveAsync(host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new NewsEgressRefusedException(NewsEgressRefusal.NoAddress);
        }

        foreach (var address in addresses)
        {
            var refusal = NewsEgressPolicy.CheckAddress(address, host, context);
            if (refusal != NewsEgressRefusal.None)
            {
                throw new NewsEgressRefusedException(refusal);
            }
        }

        return addresses;
    }
}

/// <summary>
/// The news egress client's construction and its request conventions (issue #470 §3.1 item 4, §7.5, §7.9): no proxy,
/// no automatic redirects, no cookies, no connection reuse, the connect-time check, a fixed anonymous User-Agent.
/// </summary>
public static class NewsEgress
{
    /// <summary>The named <c>HttpClient</c> registered by <c>AddNewsReader</c>.</summary>
    public const string HttpClientName = "news-egress";

    /// <summary>Fixed and anonymous (§7.5): no host, version, contact or deployment detail.</summary>
    public const string UserAgent = "OdysseyNewsReader/1.0";

    /// <summary>The network part of one fetch (§3.4 step 3).</summary>
    public static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>One image request (§6.2).</summary>
    public static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(15);

    public static readonly HttpRequestOptionsKey<NewsEgressContext> ContextKey = new("Odyssey.NewsEgressContext");

    /// <summary>
    /// The primary handler. <see cref="SocketsHttpHandler.UseProxy"/> is off, so <c>HTTPS_PROXY</c> is never consulted
    /// and the check always sees the real target (AC 21). Connections are never pooled: a pooled connection would
    /// carry one request's vetting to another request with a different context.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(NewsConnectGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 64,
            MaxResponseDrainSize = 0,
            ConnectCallback = guard.ConnectAsync,
        };
    }

    /// <summary>
    /// A GET carrying the egress context and the conditional validators, after the URL-level check. The caller sends
    /// it with <see cref="HttpCompletionOption.ResponseHeadersRead"/> and reads the body through
    /// <see cref="NewsBodyReader"/>.
    /// </summary>
    public static HttpRequestMessage CreateRequest(Uri url, NewsEgressContext context, string? etag = null, string? lastModified = null)
    {
        var refusal = NewsEgressPolicy.CheckUrl(url, context);
        if (refusal != NewsEgressRefusal.None)
        {
            throw new NewsEgressRefusedException(refusal);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Options.Set(ContextKey, context);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.ConnectionClose = true;
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        if (NewsValidators.SanitizeETag(etag) is { } validEtag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", validEtag);
        }

        if (NewsValidators.SanitizeLastModified(lastModified) is { } validLastModified)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", validLastModified);
        }

        return request;
    }

    /// <summary>The policy refusal behind a failed send, when that is what failed it.</summary>
    public static NewsEgressRefusal RefusalOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NewsEgressRefusedException refused)
            {
                return refused.Refusal;
            }
        }

        return NewsEgressRefusal.None;
    }
}
