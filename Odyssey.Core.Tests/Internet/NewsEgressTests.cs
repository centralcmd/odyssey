using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests.Internet;

/// <summary>
/// The egress policy (issue #470 §7.9; AC 21, AC 23): address classification with unwrapping, the hard-deny set, the
/// allow-list grant confined to the feed's own host, ports, redirects, the connect-time check (rebinding, pooling,
/// proxy) and the decompressed body cap.
/// </summary>
public class NewsEgressTests
{
    private const string FeedHost = "feed.internal";

    private static NewsEgressContext Context(string feedHost = FeedHost, NewsEgressPurpose purpose = NewsEgressPurpose.Feed, string grants = "") =>
        new(feedHost, purpose, NewsPrivateHostAllowListRule.Parse(grants));

    // ── AC 21: classification ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:2::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:0a00:0001::1")]
    [InlineData("2001:0000:4136:e378:8000:63bf:f5ff:fffe")] // Teredo: client 10.0.0.1 (inverted)
    [InlineData("172.16.5.4")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b:1::a00:1")]
    public void AC21_blocked_by_default_and_opened_only_by_a_grant_for_the_feeds_own_host(string literal)
    {
        var address = IPAddress.Parse(literal);
        Assert.Equal(NewsAddressClass.Private, NewsAddressClassifier.Classify(address));

        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgressPolicy.CheckAddress(address, FeedHost, Context()));
        Assert.Equal(NewsEgressRefusal.None, NewsEgressPolicy.CheckAddress(address, FeedHost, Context(grants: FeedHost)));

        // The same grant does nothing for another feed, nor for this host when it is not the feed's own.
        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgressPolicy.CheckAddress(address, FeedHost, Context(feedHost: "other.example", grants: FeedHost)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("192.0.0.192")]
    [InlineData("fd00:ec2::254")]
    [InlineData("100.100.100.200")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("::ffff:0.0.0.0")]
    public void AC21_hard_denied_even_when_allow_listed(string literal)
    {
        var address = IPAddress.Parse(literal);
        Assert.Equal(NewsAddressClass.HardDenied, NewsAddressClassifier.Classify(address));
        Assert.Equal(NewsEgressRefusal.HardDeniedAddress, NewsEgressPolicy.CheckAddress(address, FeedHost, Context(grants: FeedHost)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:0808:0808::1")]
    public void Global_unicast_is_allowed(string literal) =>
        Assert.Equal(NewsAddressClass.Public, NewsAddressClassifier.Classify(IPAddress.Parse(literal)));

    [Fact]
    public void AC21_an_ip_literal_url_is_checked_before_any_connection()
    {
        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgressPolicy.CheckUrl(new Uri("http://10.0.0.1/feed"), Context(feedHost: "10.0.0.1")));
        Assert.Equal(NewsEgressRefusal.None, NewsEgressPolicy.CheckUrl(new Uri("http://10.0.0.1/feed"), Context(feedHost: "10.0.0.1", grants: "10.0.0.1")));
        Assert.Equal(NewsEgressRefusal.HardDeniedAddress, NewsEgressPolicy.CheckUrl(new Uri("http://[fe80::1]/"), Context(feedHost: "fe80::1", grants: "[fe80::1]")));
        Assert.Throws<NewsEgressRefusedException>(() => NewsEgress.CreateRequest(new Uri("http://169.254.169.254/latest/meta-data"), Context(feedHost: "169.254.169.254")));
    }

    // ── Ports and schemes ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ports_are_80_and_443_unless_the_feeds_grant_names_one()
    {
        var url = new Uri($"https://{FeedHost}:8443/feed");
        Assert.Equal(NewsEgressRefusal.Port, NewsEgressPolicy.CheckUrl(url, Context()));
        Assert.Equal(NewsEgressRefusal.Port, NewsEgressPolicy.CheckUrl(url, Context(grants: FeedHost)));
        Assert.Equal(NewsEgressRefusal.None, NewsEgressPolicy.CheckUrl(url, Context(grants: $"{FeedHost}:8443")));
        Assert.Equal(NewsEgressRefusal.Port, NewsEgressPolicy.CheckUrl(new Uri("https://public.example:8443/"), Context(grants: $"{FeedHost}:8443")));
        Assert.Equal(NewsEgressRefusal.None, NewsEgressPolicy.CheckUrl(new Uri("http://public.example/"), Context()));
    }

    [Theory]
    [InlineData("ftp://public.example/feed")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pw@public.example/")]
    public void Only_plain_http_and_https_are_requested(string url) =>
        Assert.Equal(NewsEgressRefusal.NotAWebUrl, NewsEgressPolicy.CheckUrl(new Uri(url), Context()));

    // ── Redirects (AC 21, AC 53) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC21_the_6th_redirect_is_TooManyRedirects()
    {
        var context = Context(feedHost: "a.example");
        var state = NewsRedirectState.Start;
        var current = new Uri("https://a.example/0");
        for (var hop = 1; hop <= NewsRedirects.MaxRedirects; hop++)
        {
            var step = NewsRedirects.Next(current, HttpStatusCode.Found, new Uri($"/{hop}", UriKind.Relative), context, state);
            Assert.Equal(NewsRedirectOutcome.Follow, step.Outcome);
            (current, state, context) = (step.Next!, step.State, step.Context);
        }

        Assert.Equal(NewsRedirectOutcome.TooManyRedirects, NewsRedirects.Next(current, HttpStatusCode.Found, new Uri("/6", UriKind.Relative), context, state).Outcome);
    }

    [Fact]
    public void AC21_AC53_a_same_host_permanent_http_to_https_redirect_records_a_suggested_url()
    {
        var step = NewsRedirects.Next(new Uri("http://a.example/feed"), HttpStatusCode.MovedPermanently, new Uri("https://a.example/feed.xml"), Context(feedHost: "a.example"), NewsRedirectState.Start);

        Assert.Equal(NewsRedirectOutcome.Follow, step.Outcome);
        Assert.Equal("https://a.example/feed.xml", step.State.SuggestedUrl);
        Assert.True(step.Context.ChainOnFeedHost);
    }

    [Theory]
    [InlineData("https://www.a.example/feed")]
    [InlineData("https://other.example/feed")]
    public void AC21_a_cross_host_permanent_redirect_including_a_subdomain_records_nothing(string target)
    {
        var step = NewsRedirects.Next(new Uri("https://a.example/feed"), HttpStatusCode.PermanentRedirect, new Uri(target), Context(feedHost: "a.example", grants: "a.example"), NewsRedirectState.Start);

        Assert.Equal(NewsRedirectOutcome.Follow, step.Outcome);
        Assert.Null(step.State.SuggestedUrl);
        Assert.False(step.Context.ChainOnFeedHost);
    }

    [Fact]
    public void A_temporary_redirect_is_followed_but_never_suggested()
    {
        var step = NewsRedirects.Next(new Uri("https://a.example/feed"), HttpStatusCode.TemporaryRedirect, new Uri("https://a.example/new"), Context(feedHost: "a.example"), NewsRedirectState.Start);
        Assert.Equal(NewsRedirectOutcome.Follow, step.Outcome);
        Assert.Null(step.State.SuggestedUrl);
    }

    [Fact]
    public void AC21_an_allow_listed_private_host_is_refused_for_a_redirect_coming_from_another_host()
    {
        var context = Context(feedHost: "public.example", grants: FeedHost);
        var step = NewsRedirects.Next(new Uri("https://public.example/feed"), HttpStatusCode.Found, new Uri($"http://{FeedHost}/feed"), context, NewsRedirectState.Start);

        Assert.Equal(NewsRedirectOutcome.Follow, step.Outcome);
        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgressPolicy.CheckAddress(IPAddress.Loopback, FeedHost, step.Context));

        // And once a chain leaves the feed's own host, coming back does not restore the grant.
        var own = Context(grants: FeedHost);
        var away = NewsRedirects.Next(new Uri($"https://{FeedHost}/a"), HttpStatusCode.Found, new Uri("https://public.example/b"), own, NewsRedirectState.Start);
        var back = NewsRedirects.Next(away.Next!, HttpStatusCode.Found, new Uri($"https://{FeedHost}/c"), away.Context, away.State);
        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgressPolicy.CheckAddress(IPAddress.Loopback, FeedHost, back.Context));
    }

    [Fact]
    public void AC21_an_allow_listed_private_host_is_refused_for_an_image_referenced_by_another_feed()
    {
        var image = Context(feedHost: "other-feed.example", purpose: NewsEgressPurpose.Image, grants: FeedHost);
        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgressPolicy.CheckAddress(IPAddress.Loopback, FeedHost, image));
        Assert.Equal(NewsEgressRefusal.None, NewsEgressPolicy.CheckAddress(IPAddress.Loopback, FeedHost, image with { FeedHost = FeedHost }));
    }

    [Fact]
    public void AC21_an_https_to_http_redirect_is_refused_for_an_image_and_allowed_for_a_feed()
    {
        var from = new Uri("https://a.example/img.png");
        var to = new Uri("http://a.example/img.png");
        Assert.Equal(NewsRedirectOutcome.DowngradeRefused, NewsRedirects.Next(from, HttpStatusCode.Found, to, Context(feedHost: "a.example", purpose: NewsEgressPurpose.Image), NewsRedirectState.Start).Outcome);
        Assert.Equal(NewsRedirectOutcome.Follow, NewsRedirects.Next(from, HttpStatusCode.Found, to, Context(feedHost: "a.example"), NewsRedirectState.Start).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://a.example/x")]
    [InlineData("https://u:p@a.example/x")]
    public void An_unusable_location_is_InvalidLocation(string? location) =>
        Assert.Equal(
            NewsRedirectOutcome.InvalidLocation,
            NewsRedirects.Next(new Uri("https://a.example/"), HttpStatusCode.Found, location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute), Context(feedHost: "a.example"), NewsRedirectState.Start).Outcome);

    [Fact]
    public void A_non_redirect_status_is_not_a_redirect() =>
        Assert.Equal(NewsRedirectOutcome.NotARedirect, NewsRedirects.Next(new Uri("https://a.example/"), HttpStatusCode.OK, null, Context(feedHost: "a.example"), NewsRedirectState.Start).Outcome);

    // ── The connect-time check over a real socket ───────────────────────────────────────────────────────

    private sealed class FakeResolver(Func<int, IPAddress[]> answer) : INewsDnsResolver
    {
        public int Calls { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(answer(++Calls));
    }

    /// <summary>A minimal HTTP/1.1 server on loopback: one canned response per connection.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        public LoopbackServer(string body = "<rss/>")
        {
            listener.Start();
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    Connections++;
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var request = new StringBuilder();
                        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var read = await stream.ReadAsync(buffer);
                            if (read == 0)
                            {
                                break;
                            }

                            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }

                        LastRequest = request.ToString();
                        var payload = Encoding.UTF8.GetBytes(body);
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(payload);
                    }
                }
            });
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Connections { get; private set; }

        public string LastRequest { get; private set; } = string.Empty;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await loop;
            stop.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMessageInvoker client, Uri url, NewsEgressContext context)
    {
        using var request = NewsEgress.CreateRequest(url, context, etag: "\"v1\"", lastModified: "Mon, 16 Nov 2026 09:30:00 GMT");
        return await client.SendAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task AC1_AC21_an_allow_listed_loopback_feed_is_fetched_and_without_the_grant_it_is_refused()
    {
        await using var server = new LoopbackServer();
        var resolver = new FakeResolver(_ => [IPAddress.Loopback]);
        using var client = new HttpMessageInvoker(NewsEgress.CreateHandler(new NewsConnectGuard(resolver)));
        var url = new Uri($"http://{FeedHost}:{server.Port}/feed.xml");

        using var ok = await SendAsync(client, url, Context(grants: $"{FeedHost}:{server.Port}"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("User-Agent: " + NewsEgress.UserAgent, server.LastRequest, StringComparison.Ordinal);
        Assert.Contains("If-None-Match: \"v1\"", server.LastRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", server.LastRequest, StringComparison.OrdinalIgnoreCase);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(client, url, Context(feedHost: "another-feed.example", grants: $"{FeedHost}:{server.Port}")));
        Assert.Equal(NewsEgressRefusal.Port, NewsEgress.RefusalOf(refused));

        var noGrant = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(client, new Uri($"http://{FeedHost}/feed.xml"), Context()));
        Assert.Equal(NewsEgressRefusal.PrivateAddress, NewsEgress.RefusalOf(noGrant));
    }

    [Fact]
    public async Task AC21_dns_rebinding_resolves_once_and_connects_only_to_the_vetted_address()
    {
        await using var server = new LoopbackServer();

        // A second lookup would answer a hard-denied address: it must never be made for this connection.
        var resolver = new FakeResolver(call => call == 1 ? [IPAddress.Loopback] : [IPAddress.Parse("169.254.169.254")]);
        var guard = new NewsConnectGuard(resolver);
        using var client = new HttpMessageInvoker(NewsEgress.CreateHandler(guard));

        using var response = await SendAsync(client, new Uri($"http://{FeedHost}:{server.Port}/"), Context(grants: $"{FeedHost}:{server.Port}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task One_blocked_address_among_the_answers_refuses_the_host()
    {
        var guard = new NewsConnectGuard(new FakeResolver(_ => [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.1.2.3")]));
        var refused = await Assert.ThrowsAsync<NewsEgressRefusedException>(() => guard.VetAsync("mixed.example", 443, Context(feedHost: "mixed.example"), CancellationToken.None));
        Assert.Equal(NewsEgressRefusal.PrivateAddress, refused.Refusal);

        var none = await Assert.ThrowsAsync<NewsEgressRefusedException>(() => new NewsConnectGuard(new FakeResolver(_ => [])).VetAsync("void.example", 443, Context(feedHost: "void.example"), CancellationToken.None));
        Assert.Equal(NewsEgressRefusal.NoAddress, none.Refusal);
    }

    [Fact]
    public async Task Connections_are_never_reused_so_every_request_is_vetted_with_its_own_context()
    {
        await using var server = new LoopbackServer();
        var resolver = new FakeResolver(_ => [IPAddress.Loopback]);
        using var client = new HttpMessageInvoker(NewsEgress.CreateHandler(new NewsConnectGuard(resolver)));
        var url = new Uri($"http://{FeedHost}:{server.Port}/");

        using (await SendAsync(client, url, Context(grants: $"{FeedHost}:{server.Port}")))
        {
        }

        using (await SendAsync(client, url, Context(grants: $"{FeedHost}:{server.Port}")))
        {
        }

        Assert.Equal(2, resolver.Calls);
        Assert.Equal(2, server.Connections);

        // A request for an image of another feed, to the same host and port, gets its own vetting and is refused.
        var other = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(client, url, Context(feedHost: "else.example", purpose: NewsEgressPurpose.Image, grants: $"{FeedHost}:{server.Port}")));
        Assert.NotEqual(NewsEgressRefusal.None, NewsEgress.RefusalOf(other));
    }

    [Fact]
    public async Task A_request_without_an_egress_context_is_refused()
    {
        using var client = new HttpMessageInvoker(NewsEgress.CreateHandler(new NewsConnectGuard(new FakeResolver(_ => [IPAddress.Loopback]))));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://plain.example/");
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(request, CancellationToken.None));
        Assert.NotEqual(NewsEgressRefusal.None, NewsEgress.RefusalOf(refused));
    }

    [Fact]
    public void AC21_the_handler_uses_no_proxy_follows_no_redirect_and_keeps_no_cookies()
    {
        using var handler = NewsEgress.CreateHandler(new NewsConnectGuard(new SystemNewsDnsResolver()));

        // With UseProxy off, HTTPS_PROXY/HTTP_PROXY are never consulted: the connect-time check sees the real target.
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void AddNewsReader_registers_the_egress_client()
    {
        var services = new ServiceCollection();
        // What every host already has: the context, the cache and the store's options (the settings lookup needs them).
        services.AddDbContext<Odyssey.Context.OdysseyContext>(o => o.UseInMemoryDatabase($"news-reader-{Guid.NewGuid():N}"));
        services.AddMemoryCache();
        services.AddOptions<Odyssey.Core.Finance.FileStorageOptions>();
        services.AddLogging();
        services.AddNewsReader();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(NewsEgress.HttpClientName);
        Assert.Equal(NewsEgress.FeedTimeout, client.Timeout);
        Assert.NotNull(provider.GetRequiredService<NewsConnectGuard>());
        using var scope = provider.CreateScope();
        Assert.IsType<NewsSettingsLookup>(scope.ServiceProvider.GetRequiredService<INewsSettingsLookup>());
    }

    // ── Body cap (AC 21: gzip bomb) ─────────────────────────────────────────────────────────────────────

    private static ByteArrayContent Gzipped(int bytes)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var written = 0; written < bytes; written += zeros.Length)
            {
                gzip.Write(zeros, 0, Math.Min(zeros.Length, bytes - written));
            }
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    [Fact]
    public async Task AC21_a_gzip_bomb_is_aborted_at_10_MiB()
    {
        using var bomb = Gzipped(64 * 1024 * 1024);
        var result = await NewsBodyReader.ReadAsync(bomb, NewsBodyReader.MaxFeedBodyBytes, CancellationToken.None);
        Assert.Equal(NewsBodyOutcome.TooLarge, result.Outcome);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task A_body_at_the_cap_is_read_and_one_byte_more_is_TooLarge()
    {
        const int cap = 4096;
        Assert.Equal(cap, (await NewsBodyReader.ReadAsync(new ByteArrayContent(new byte[cap]), cap, CancellationToken.None)).Body.Length);
        Assert.Equal(NewsBodyOutcome.TooLarge, (await NewsBodyReader.ReadAsync(new ByteArrayContent(new byte[cap + 1]), cap, CancellationToken.None)).Outcome);
        using var zipped = Gzipped(cap);
        Assert.Equal(cap, (await NewsBodyReader.ReadAsync(zipped, cap, CancellationToken.None)).Body.Length);
    }

    [Fact]
    public async Task An_unknown_or_corrupt_encoding_is_reported()
    {
        var unknown = new ByteArrayContent([1, 2, 3]);
        unknown.Headers.ContentEncoding.Add("compress");
        Assert.Equal(NewsBodyOutcome.UnsupportedEncoding, (await NewsBodyReader.ReadAsync(unknown, 100, CancellationToken.None)).Outcome);

        var corrupt = new ByteArrayContent([0x1F, 0x8B, 0xFF, 0xFF, 0xFF]);
        corrupt.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(NewsBodyOutcome.CorruptEncoding, (await NewsBodyReader.ReadAsync(corrupt, 100, CancellationToken.None)).Outcome);
    }

    // ── AC 23: validators ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"abc\"\r\nX-Injected: 1")]
    [InlineData("\"abc\"\n")]
    [InlineData("\"été\"")]
    [InlineData("\"a\u0000b\"")]
    [InlineData("")]
    public void AC23_an_etag_with_crlf_controls_or_non_ascii_is_dropped(string etag) => Assert.Null(NewsValidators.SanitizeETag(etag));

    [Fact]
    public void AC23_valid_validators_are_kept_and_invalid_ones_are_never_sent()
    {
        Assert.Equal("W/\"abc-123\"", NewsValidators.SanitizeETag(" W/\"abc-123\" "));
        Assert.Equal("Mon, 16 Nov 2026 09:30:00 GMT", NewsValidators.SanitizeLastModified("Mon, 16 Nov 2026 09:30:00 GMT"));
        Assert.Null(NewsValidators.SanitizeLastModified("Mon, 16 Nov 2026 09:30:00 GMT\r\nX: y"));
        Assert.Null(NewsValidators.SanitizeLastModified("not a date"));

        using var request = NewsEgress.CreateRequest(new Uri("https://public.example/"), Context(feedHost: "public.example"), etag: "\"x\"\r\nEvil: 1", lastModified: "garbage");
        Assert.False(request.Headers.Contains("If-None-Match"));
        Assert.False(request.Headers.Contains("If-Modified-Since"));
    }
}
