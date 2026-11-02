using System.Net;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Odyssey.Testing;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The client container's self-hosted basemap surface against the REAL nginx image (issue #384 AC 15): the
/// pinned <c>nginx-unprivileged</c> from the client Dockerfile, the rendered site template and its start checks,
/// the committed fixture archive, and a stub upstream standing in for the API's access probe that sends the
/// real probe's headers (<c>no-store</c> and a <c>Set-Cookie</c>), so the cache rules are tested as deployed.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BasemapNginxFixture : IAsyncLifetime
{
    public const string Good = ".AspNetCore.Identity.Application=chunks-2; .AspNetCore.Identity.ApplicationC1=good; .AspNetCore.Identity.ApplicationC2=rest";

    private static readonly string Root = FindRoot();

    private INetwork? network;
    private IContainer? stub;
    private IContainer? nginx;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    public string Html { get; } = Directory.CreateTempSubdirectory("odyssey-basemap-html-").FullName;

    public string Work { get; } = Directory.CreateTempSubdirectory("odyssey-basemap-work-").FullName;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The client image's pinned base, read from its Dockerfile so the two cannot drift.</summary>
    public static string Image => Regex.Match(
        File.ReadAllText(Path.Combine(Root, "Odyssey.Client", "Dockerfile")),
        @"(?m)^FROM (?<image>nginxinc/nginx-unprivileged\S+) AS final").Groups["image"].Value;

    public async Task InitializeAsync()
    {
        TestTierGate.IsRequired(TestTierGate.Integration);
        try
        {
            PrepareHtml();
            network = new NetworkBuilder().Build();
            await network.CreateAsync();

            var stubConf = Path.Combine(Work, "stub.conf");
            await File.WriteAllTextAsync(stubConf, """
                log_format probe 'PROBE host=$http_host xfp=$http_x_forwarded_proto cookie=$http_cookie';
                server {
                    listen 8080;
                    access_log /dev/stdout probe;
                    location = /api/map-styles/basemap-access {
                        if ($http_cookie ~ "ApplicationC1=good") {
                            add_header Cache-Control "no-store" always;
                            add_header Set-Cookie "refreshed=1; Path=/" always;
                            return 204;
                        }
                        return 401;
                    }
                }
                """);
            stub = new ContainerBuilder(Image)
                .WithNetwork(network)
                .WithNetworkAliases("api")
                .WithBindMount(stubConf, "/etc/nginx/conf.d/default.conf", AccessMode.ReadOnly)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("start worker process"))
                .Build();
            await stub.StartAsync();

            nginx = Client(Html)
                .WithPortBinding(8080, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/")))
                .Build();
            await nginx.StartAsync();
            BaseAddress = new Uri($"http://{nginx.Hostname}:{nginx.GetMappedPublicPort(8080)}");
            Available = true;
        }
        catch (Exception ex) when (MariaDbFixture.ClassifyStartupFailure(ex) == TierProbe.PrerequisiteMissing)
        {
            SkipReason = TestTierGate.Resolve(TestTierGate.Integration, TierProbe.PrerequisiteMissing,
                $"nginx Testcontainer unavailable, Docker is not reachable: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw TestTierGate.Fault(TestTierGate.Integration, $"nginx Testcontainer failed to start: {ex.Message}", ex);
        }
    }

    /// <summary>The client container as the image runs it: the template, the start checks, an empty CSP origin list.</summary>
    public ContainerBuilder Client(string html, string? cacheParent = null)
    {
        var builder = new ContainerBuilder(Image)
            .WithNetwork(network!)
            .WithEnvironment("MAP_ALLOWED_ORIGINS", string.Empty)
            .WithEnvironment("NGINX_ENVSUBST_FILTER", "^MAP_ALLOWED_ORIGINS$")
            .WithBindMount(Path.Combine(Root, "Odyssey.Client", "nginx", "templates"), "/etc/nginx/templates", AccessMode.ReadOnly)
            .WithBindMount(html, "/usr/share/nginx/html", AccessMode.ReadOnly);
        foreach (var script in Directory.GetFiles(Path.Combine(Root, "Odyssey.Client", "nginx", "docker-entrypoint.d")))
            builder = builder.WithBindMount(script, "/docker-entrypoint.d/" + Path.GetFileName(script), AccessMode.ReadOnly);
        if (cacheParent is not null)
            builder = builder.WithBindMount(cacheParent, "/var/cache/nginx", AccessMode.ReadOnly);
        return builder;
    }

    public async Task<int> ProbeCallsAsync()
    {
        // nginx writes the access line as the request completes; give the log a moment to reach Docker.
        await Task.Delay(200);
        var (stdout, _) = await stub!.GetLogsAsync();
        return Regex.Matches(stdout, @"\bPROBE host=").Count;
    }

    public async Task<string> ProbeLogAsync() => (await stub!.GetLogsAsync()).Stdout;

    private void PrepareHtml()
    {
        File.WriteAllText(Path.Combine(Html, "index.html"), "<!doctype html><title>spa</title>");
        Directory.CreateDirectory(Path.Combine(Html, "basemap"));
        File.Copy(Path.Combine(Root, "TestShared", "Basemap", "basemap.pmtiles"), Path.Combine(Html, "basemap", "basemap.pmtiles"));
        var fonts = Path.Combine(Html, "maps", "self-hosted", "fonts", "Noto Sans Regular");
        Directory.CreateDirectory(fonts);
        var shipped = Path.Combine(Root, "Odyssey.Client", "wwwroot", "maps", "self-hosted", "fonts");
        File.Copy(Path.Combine(shipped, "empty.pbf"), Path.Combine(Html, "maps", "self-hosted", "fonts", "empty.pbf"));
        File.Copy(Path.Combine(shipped, "Noto Sans Regular", "0-255.pbf"), Path.Combine(fonts, "0-255.pbf"));
        Directory.CreateDirectory(Path.Combine(Html, "maps", "self-hosted", "sprites"));
        foreach (var path in Directory.EnumerateFileSystemEntries(Html, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(path, Directory.Exists(path) ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100);
        File.SetUnixFileMode(Html, (UnixFileMode)0b111_101_101);
    }

    public async Task DisposeAsync()
    {
        if (nginx is not null) await nginx.DisposeAsync();
        if (stub is not null) await stub.DisposeAsync();
        if (network is not null) await network.DisposeAsync();
        Directory.Delete(Html, recursive: true);
        Directory.Delete(Work, recursive: true);
    }

    private static string FindRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar)))
        {
            if (File.Exists(Path.Combine(d, "Odyssey.sln")))
                return d;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}

[SupportedOSPlatform("linux")]
public sealed class BasemapNginxTests(BasemapNginxFixture fixture) : IClassFixture<BasemapNginxFixture>
{
    private const string File = "/basemap/basemap.pmtiles";

    private HttpClient Client() => new(new HttpClientHandler { UseProxy = false, UseCookies = false }) { BaseAddress = fixture.BaseAddress };

    private static HttpRequestMessage Ranged(string? range, string? cookie = BasemapNginxFixture.Good, string? forwardedFor = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, File);
        if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (forwardedFor is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return request;
    }

    [SkippableFact]
    public async Task An_authorized_range_is_a_206_with_the_exact_bytes_and_every_server_header()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        using var response = await client.SendAsync(Ranged("bytes=0-126", forwardedFor: "198.51.100.1"));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var expected = (await System.IO.File.ReadAllBytesAsync(Path.Combine(fixture.Html, "basemap", "basemap.pmtiles")))[..127];
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("default-src 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [SkippableFact]
    public async Task The_access_check_is_cached_per_complete_cookie_header_and_forwards_host_and_scheme()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();
        var session = BasemapNginxFixture.Good.Replace("C2=rest", "C2=" + Guid.NewGuid().ToString("N"), StringComparison.Ordinal);

        var before = await fixture.ProbeCallsAsync();
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(Ranged("bytes=0-9", session, "198.51.100.2"))).StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(Ranged("bytes=10-19", session, "198.51.100.2"))).StatusCode);
        Assert.Equal(before + 1, await fixture.ProbeCallsAsync());

        // Padding the header is a different key: a miss, one more probe call, still authorized.
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(Ranged("bytes=0-9", session + "; pad=1", "198.51.100.2"))).StatusCode);
        Assert.Equal(before + 2, await fixture.ProbeCallsAsync());

        var log = await fixture.ProbeLogAsync();
        Assert.Contains("xfp=http ", log, StringComparison.Ordinal);
        Assert.DoesNotContain("host= ", log, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_forged_base_cookie_never_rides_a_cached_session()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(Ranged("bytes=0-9", forwardedFor: "198.51.100.3"))).StatusCode);

        var before = await fixture.ProbeCallsAsync();
        var forged = await client.SendAsync(Ranged("bytes=0-9", ".AspNetCore.Identity.Application=chunks-2", "198.51.100.3"));
        var otherSession = await client.SendAsync(Ranged("bytes=0-9", ".AspNetCore.Identity.Application=chunks-2; .AspNetCore.Identity.ApplicationC1=bad; .AspNetCore.Identity.ApplicationC2=rest", "198.51.100.3"));

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherSession.StatusCode);
        Assert.Equal(before + 2, await fixture.ProbeCallsAsync());
    }

    [SkippableFact]
    public async Task No_identity_cookie_is_never_cached()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        var before = await fixture.ProbeCallsAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Ranged("bytes=0-9", cookie: null, "198.51.100.4"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Ranged("bytes=0-9", cookie: null, "198.51.100.4"))).StatusCode);

        Assert.Equal(before + 2, await fixture.ProbeCallsAsync());
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("bytes=0-")]
    [InlineData("bytes=-5")]
    [InlineData("items=0-5")]
    [InlineData("bytes=0-1,4-5")]
    public async Task Every_range_form_but_one_closed_range_is_a_416_that_never_reaches_the_api(string? range)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        var before = await fixture.ProbeCallsAsync();
        using var response = await client.SendAsync(Ranged(range, forwardedFor: "198.51.100.5"));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.NotEqual("multipart/byteranges", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(before, await fixture.ProbeCallsAsync());
    }

    [SkippableFact]
    public async Task Head_is_refused()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(Ranged("bytes=0-9", method: HttpMethod.Head))).StatusCode);
    }

    [SkippableFact]
    public async Task Each_real_client_has_its_own_burst()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 160; i++)
            statuses.Add((await client.SendAsync(Ranged("bytes=0-9", forwardedFor: "203.0.113.7, 192.0.2.10"))).StatusCode);

        Assert.Contains((HttpStatusCode)429, statuses);
        // The last X-Forwarded-For element is the client: a different one is untouched by the first's limit.
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(Ranged("bytes=0-9", forwardedFor: "203.0.113.7, 192.0.2.11"))).StatusCode);
    }

    [SkippableTheory]
    [InlineData("/basemap")]
    [InlineData("/basemap/other.js")]
    [InlineData("/basemap/")]
    [InlineData("/maps/self-hosted/sprites/missing.png")]
    public async Task Nothing_else_is_reachable_and_nothing_falls_back_to_the_spa(string path)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<title>spa</title>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_shipped_glyph_range_is_served_and_any_other_is_empty()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var client = Client();

        var shipped = await client.GetAsync("/maps/self-hosted/fonts/Noto%20Sans%20Regular/0-255.pbf");
        var other = await client.GetAsync("/maps/self-hosted/fonts/Noto%20Sans%20Regular/19968-20223.pbf");

        Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
        Assert.NotEmpty(await shipped.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Empty(await other.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task A_symlink_swapped_in_after_start_is_never_followed()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var target = Path.Combine(fixture.Html, "basemap", "basemap.pmtiles");
        var moved = Path.Combine(fixture.Html, "basemap", ".real.pmtiles");
        System.IO.File.Move(target, moved);
        System.IO.File.CreateSymbolicLink(target, "../index.html");
        try
        {
            using var client = Client();
            using var response = await client.SendAsync(Ranged("bytes=0-9", forwardedFor: "198.51.100.6"));

            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, response.StatusCode.ToString());
        }
        finally
        {
            System.IO.File.Delete(target);
            System.IO.File.Move(moved, target);
        }
    }

    [SkippableFact]
    public async Task A_missing_file_is_a_404_not_the_spa()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var target = Path.Combine(fixture.Html, "basemap", "basemap.pmtiles");
        var moved = target + ".away";
        System.IO.File.Move(target, moved);
        try
        {
            using var client = Client();
            using var response = await client.SendAsync(Ranged("bytes=0-9", forwardedFor: "198.51.100.8"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("<title>spa</title>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.Move(moved, target);
        }
    }

    [SkippableTheory]
    [InlineData("unreadable")]
    [InlineData("cache-read-only")]
    public async Task The_start_check_stops_the_container(string shape)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        Skip.If(Environment.UserName != "root" && shape == "unreadable", "Making a file unreadable to UID 101 needs a root-owned file.");
        var html = Directory.CreateTempSubdirectory("odyssey-basemap-bad-").FullName;
        var cache = Directory.CreateTempSubdirectory("odyssey-basemap-cache-").FullName;
        try
        {
            System.IO.File.WriteAllText(Path.Combine(html, "index.html"), "spa");
            Directory.CreateDirectory(Path.Combine(html, "basemap"));
            var file = Path.Combine(html, "basemap", "basemap.pmtiles");
            System.IO.File.Copy(Path.Combine(fixture.Html, "basemap", "basemap.pmtiles"), file);
            System.IO.File.SetUnixFileMode(html, (UnixFileMode)0b111_101_101);
            System.IO.File.SetUnixFileMode(Path.Combine(html, "basemap"), (UnixFileMode)0b111_101_101);
            System.IO.File.SetUnixFileMode(file, shape == "unreadable" ? (UnixFileMode)0b110_000_000 : (UnixFileMode)0b110_100_100);

            await using var container = fixture.Client(html, shape == "cache-read-only" ? cache : null)
                .WithEntrypoint("/bin/sh")
                .WithCommand("/docker-entrypoint.d/16-check-basemap-readable.sh")
                .Build();
            await container.StartAsync();
            var exit = await container.GetExitCodeAsync();
            var (_, stderr) = await container.GetLogsAsync();

            Assert.NotEqual(0, exit);
            Assert.Contains(shape == "unreadable" ? "basemap-unreadable" : "basemap-cache-unwritable", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(html, recursive: true);
            Directory.Delete(cache, recursive: true);
        }
    }
}
