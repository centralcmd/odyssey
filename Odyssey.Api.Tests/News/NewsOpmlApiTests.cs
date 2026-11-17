using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Internet.News;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Internet;
using Xunit;
using static Odyssey.Api.Tests.News.NewsApiTestSupport;

namespace Odyssey.Api.Tests.News;

/// <summary>
/// The OPML endpoints of issue #508 over HTTP (§5, amendments v4–v7). Test names carry the AC they pin; the service
/// rules are covered in <c>Odyssey.Core.Tests</c> and the MariaDB-only ones in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public sealed class NewsOpmlApiTests
{
    private const string Opml = "/api/news-feeds/opml";
    private const string FilteredExport = "/api/news-feeds/opml/export";

    private static readonly string[] ImportClaims = [PermissionClaims.NewsRead, PermissionClaims.NewsFeedsCreate, PermissionClaims.NewsTagsCreate];

    private static string Document(params string[] urls) =>
        "<opml version=\"2.0\"><body>" + string.Concat(urls.Select(u => $"<outline text=\"T\" xmlUrl=\"{u}\" category=\"/Imported\"/>")) + "</body></opml>";

    private static MultipartFormDataContent Upload(string content, string fileName = "feeds.opml", string? contentType = "text/x-opml") =>
        Upload(Encoding.UTF8.GetBytes(content), fileName, contentType);

    private static MultipartFormDataContent Upload(byte[] content, string fileName = "feeds.opml", string? contentType = "text/x-opml")
    {
        var part = new ByteArrayContent(content);
        if (contentType is not null)
        {
            part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    // ── Export ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC1_The_export_is_200_with_the_download_headers_and_an_opml_2_document()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        await factory.SeedFeedAsync(url: "https://one.example/rss");
        await factory.SeedFeedAsync(url: "https://two.example/rss");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Opml);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/x-opml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Matches(@"^odyssey-news-feeds-\d{4}-\d{2}-\d{2}\.opml$", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("2", response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        Assert.Equal(("opml", "2.0"), (root.Name.LocalName, (string?)root.Attribute("version")));
        Assert.Equal("urn:odyssey:opml:1", (string?)root.Attribute(XNamespace.Xmlns + "odyssey"));
    }

    [Fact]
    public async Task AC5_A_database_fault_in_the_export_query_is_503_news_busy_never_a_truncated_200()
    {
        var fault = new NewsQueryFault();
        await using var factory = new OdysseyApiFactory(AllNewsClaims, configureServices: s => s.ConfigureDbContext<OdysseyContext>(o => o.AddInterceptors(fault)));
        await factory.SeedFeedAsync();
        using var client = factory.CreateClient();
        fault.Armed = true;

        var response = await client.GetAsync(Opml);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(NewsErrorCodes.Busy, (await response.ProblemAsync()).Code());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Headers.Contains("X-Odyssey-Export-Rows"));
        Assert.DoesNotContain("<opml", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC39_AC40_The_filtered_export_writes_the_named_feeds_and_their_tags_once()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        var tagged = await factory.SeedFeedAsync(url: "https://a.example/rss");
        var b = await factory.SeedFeedAsync(url: "https://b.example/rss");
        var c = await factory.SeedFeedAsync(url: "https://c.example/rss");
        var other = await factory.SeedFeedAsync(url: "https://other.example/rss");
        var pending = await factory.SeedFeedAsync(url: "https://pending.example/rss", pendingDeletion: true);
        await factory.TagFeedAsync(tagged, await factory.SeedTagAsync("Mine"));
        await factory.TagFeedAsync(other, await factory.SeedTagAsync("NotMine"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(FilteredExport, new NewsOpmlExportRequest { FeedIds = [tagged, b, c, tagged, pending, Guid.NewGuid()] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("3", response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element("body")!;
        Assert.Equal(["Mine"], body.Elements("outline").Where(o => o.Attribute("xmlUrl") is null).Select(o => (string)o.Attribute("text")!));
        Assert.Equal(3, body.Descendants("outline").Count(o => o.Attribute("xmlUrl") is not null));
    }

    public static TheoryData<string> BadExportBodies() => new()
    {
        { "{}" },
        { "{\"feedIds\":[]}" },
        { "{\"feedIds\":null}" },
        { "{\"feedIds\":[\"not-a-guid\"]}" },
        { "{\"feedIds\":[" + string.Join(',', Enumerable.Repeat("\"" + Guid.NewGuid() + "\"", 1001)) + "]}" },
    };

    [Theory]
    [MemberData(nameof(BadExportBodies))]
    public async Task AC41_A_missing_empty_invalid_or_over_long_list_is_400(string json)
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(FilteredExport, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_filtered_export_carries_its_request_body_cap()
    {
        // TestServer does not enforce [RequestSizeLimit] (Kestrel does), so the cap is pinned on the endpoint's metadata:
        // without it a well-formed body of any size would be read.
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        using var _ = factory.CreateClient();

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "api/news-feeds/opml/export"
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"));

        Assert.Equal(NewsOpmlLimits.MaxExportRequestBytes, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize);
        Assert.Equal(131_072, NewsOpmlLimits.MaxExportRequestBytes);
    }

    [Fact]
    public async Task AC41_A_list_of_only_unknown_ids_is_200_with_an_empty_body_and_no_rows()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        await factory.SeedFeedAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(FilteredExport, new NewsOpmlExportRequest { FeedIds = [Guid.NewGuid()] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        Assert.Empty(XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!.Element("body")!.Elements());
    }

    [Fact]
    public async Task AC42_The_filtered_export_needs_news_feeds_update_and_json()
    {
        await using var reader = new OdysseyApiFactory([PermissionClaims.NewsRead, PermissionClaims.NewsFeedsCreate]);
        using var readerClient = reader.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PostAsJsonAsync(FilteredExport, new NewsOpmlExportRequest { FeedIds = [Guid.NewGuid()] })).StatusCode);

        await using var owner = new OdysseyApiFactory(AllNewsClaims);
        using var ownerClient = owner.CreateClient();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await ownerClient.PostAsync(FilteredExport, new StringContent("feedIds", Encoding.UTF8, "text/plain"))).StatusCode);
    }

    [Fact]
    public async Task AC42_Both_exports_share_the_global_export_limiter()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        using var client = factory.CreateClient();
        var global = factory.Services.GetRequiredService<GlobalExportConcurrencyLimiter>();
        var leases = Enumerable.Range(0, 4).Select(_ => global.Limiter.AttemptAcquire()).ToList();
        try
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(Opml)).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(FilteredExport, new NewsOpmlExportRequest { FeedIds = [Guid.NewGuid()] })).StatusCode);
        }
        finally
        {
            leases.ForEach(l => l.Dispose());
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Opml)).StatusCode);
    }

    // ── Authorization ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC7_Export_needs_news_feeds_update_and_import_news_feeds_create()
    {
        await using var noUpdate = new OdysseyApiFactory([PermissionClaims.NewsRead, PermissionClaims.NewsFeedsCreate]);
        using var noUpdateClient = noUpdate.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await noUpdateClient.GetAsync(Opml)).StatusCode);

        await using var noCreate = new OdysseyApiFactory([PermissionClaims.NewsRead, PermissionClaims.NewsFeedsUpdate]);
        using var noCreateClient = noCreate.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await noCreateClient.PostAsync(Opml, Upload(Document("https://a.example/rss")))).StatusCode);
        Assert.Equal(0, await noCreate.WithContextAsync(c => c.NewsFeeds.CountAsync()));
    }

    [Fact]
    public async Task AC7_Without_news_tags_create_the_feeds_import_and_the_missing_tag_is_reported()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.NewsRead, PermissionClaims.NewsFeedsCreate]);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Opml, Upload(Document("https://a.example/rss", "https://b.example/rss")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadAsync<NewsOpmlImportResult>();
        Assert.Equal(2, result.Created);
        Assert.Equal(NewsOpmlTagSkipReason.NoPermission, Assert.Single(result.TagsSkipped).Reason);
        Assert.Equal(0, await factory.WithContextAsync(c => c.NewsTags.CountAsync()));
    }

    // ── Import ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC6_A_round_trip_through_both_endpoints_restores_the_feeds_and_tags()
    {
        await using var source = new OdysseyApiFactory(AllNewsClaims);
        var feed = await source.SeedFeedAsync(url: "https://round.example/rss", active: false);
        await source.TagFeedAsync(feed, await source.SeedTagAsync("a,b"));
        await source.TagFeedAsync(feed, await source.SeedTagAsync("100%"));
        using var sourceClient = source.CreateClient();
        var exported = await sourceClient.GetByteArrayAsync(Opml);

        await using var target = new OdysseyApiFactory(AllNewsClaims);
        using var targetClient = target.CreateClient();
        var result = await (await targetClient.PostAsync(Opml, Upload(exported))).ReadAsync<NewsOpmlImportResult>();

        Assert.Equal((1, 1, 1), (result.Feeds, result.Created, result.MergedOutlines));
        Assert.Equal(["100%", "a,b"], result.Items[0].Tags.Order(StringComparer.Ordinal));
        var created = await targetClient.GetFromJsonAsync<ExistingNewsFeed>($"/api/news-feeds/{result.Items[0].FeedId}", Json);
        Assert.False(created!.IsActive);
        Assert.Equal(NewsFeedHealth.Paused, created.Health);
    }

    [Fact]
    public async Task AC23_A_created_feed_derives_its_health_whatever_the_file_says()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims);
        using var client = factory.CreateClient();
        var file = "<opml xmlns:odyssey=\"urn:odyssey:opml:1\"><body><outline xmlUrl=\"https://a.example/rss\" odyssey:health=\"Healthy\" id=\"x\"/></body></opml>";

        var result = await (await client.PostAsync(Opml, Upload(file))).ReadAsync<NewsOpmlImportResult>();
        var feed = await client.GetFromJsonAsync<ExistingNewsFeed>($"/api/news-feeds/{result.Items[0].FeedId}", Json);

        Assert.Equal(NewsFeedHealth.Pending, feed!.Health);
    }

    [Fact]
    public async Task AC26_A_missing_file_is_file_required_and_a_txt_file_is_wrong_type()
    {
        await using var factory = new OdysseyApiFactory(ImportClaims);
        using var client = factory.CreateClient();

        var missing = await client.PostAsync(Opml, new MultipartFormDataContent { { new StringContent("x"), "other" } });
        Assert.Equal((HttpStatusCode.BadRequest, NewsErrorCodes.OpmlFileRequired), (missing.StatusCode, (await missing.ProblemAsync()).Code()));

        var empty = await client.PostAsync(Opml, Upload([]));
        Assert.Equal(NewsErrorCodes.OpmlFileRequired, (await empty.ProblemAsync()).Code());

        var txt = await client.PostAsync(Opml, Upload(Document("https://a.example/rss"), "feeds.txt", "text/plain"));
        Assert.Equal((HttpStatusCode.BadRequest, NewsErrorCodes.OpmlWrongType), (txt.StatusCode, (await txt.ProblemAsync()).Code()));

        var png = await client.PostAsync(Opml, Upload(Document("https://a.example/rss"), "feeds.opml", "image/png"));
        Assert.Equal(NewsErrorCodes.OpmlWrongType, (await png.ProblemAsync()).Code());
    }

    [Theory]
    [InlineData("feeds.xml", "text/xml")]
    [InlineData("feeds.OPML", "application/xml")]
    [InlineData("feeds.opml", "application/octet-stream")]
    [InlineData("feeds.opml", "text/x-opml; charset=utf-8")]
    [InlineData("feeds.opml", null)]
    public async Task The_accepted_extensions_and_declared_types_import(string fileName, string? contentType)
    {
        await using var factory = new OdysseyApiFactory(ImportClaims);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Opml, Upload(Document("https://a.example/rss"), fileName, contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AC25_An_unreadable_file_is_400_and_the_body_carries_no_parser_text()
    {
        await using var factory = new OdysseyApiFactory(ImportClaims);
        using var client = factory.CreateClient();

        foreach (var content in new[] { "<rss version=\"2.0\"><channel/></rss>", "<opml><head/></opml>", "<opml><body><outline", "\u0089PNG\r\n\u001a\n" })
        {
            var response = await client.PostAsync(Opml, Upload(content));
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(NewsErrorCodes.OpmlUnreadable, (await response.ProblemAsync()).Code());
            Assert.DoesNotContain("line", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("position", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rss", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AC27_More_distinct_feeds_than_the_entry_cap_is_422_with_the_limit_and_nothing_written()
    {
        await using var factory = new OdysseyApiFactory(ImportClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.NewsOpmlMaxImportEntries, "100");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Opml, Upload(Document([.. Enumerable.Range(0, 101).Select(i => $"https://f{i}.example/rss")])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(NewsErrorCodes.OpmlTooManyEntries, problem.Code());
        Assert.Equal(100, problem.GetProperty("limit").GetInt32());
        Assert.True(problem.HasError("file"));
        Assert.Equal(0, await factory.WithContextAsync(c => c.NewsFeeds.CountAsync()));
        Assert.Equal(0, await factory.WithContextAsync(c => c.NewsTags.CountAsync()));
    }

    [Fact]
    public async Task AC27_A_file_over_the_size_cap_is_refused_before_anything_is_written()
    {
        // Under TestServer the transport limit feature is absent, so the multipart limit refuses the body as a binding
        // failure (400); on Kestrel the same request is the middleware's 413 (the vCard import's precedent).
        await using var factory = new OdysseyApiFactory(ImportClaims);
        await factory.SetSystemSettingAsync(SystemSettingsKeys.NewsOpmlMaxImportMegabytes, "1");
        using var client = factory.CreateClient();
        var padding = "<!--" + new string('x', 60_000) + "-->";
        var large = "<opml><body>" + string.Concat(Enumerable.Repeat(padding, 20)) + "<outline xmlUrl=\"https://a.example/rss\"/></body></opml>";

        var response = await client.PostAsync(Opml, Upload(large));

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge });
        Assert.Equal(0, await factory.WithContextAsync(c => c.NewsFeeds.CountAsync()));
    }

    [Fact]
    public async Task AC14_The_import_makes_no_outbound_request_and_no_dns_lookup()
    {
        var dns = new FailingDns();
        await using var factory = new OdysseyApiFactory(ImportClaims, configureServices: s =>
        {
            s.Replace(ServiceDescriptor.Singleton<INewsDnsResolver>(dns));
            s.AddHttpClient(NewsEgress.HttpClientName).AddHttpMessageHandler(() => new FailingHandler());
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Opml, Upload(Document("https://a.example/rss", "https://b.example/rss")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, dns.Calls);
        Assert.Equal(0, FailingHandler.Sends);
    }

    [Fact]
    public async Task AC31_An_interrupted_transaction_during_an_import_is_503_news_busy()
    {
        var fault = new NewsQueryFault();
        await using var factory = new OdysseyApiFactory(ImportClaims, configureServices: s => s.ConfigureDbContext<OdysseyContext>(o => o.AddInterceptors(fault)));
        using var client = factory.CreateClient();
        fault.Armed = true;

        var response = await client.PostAsync(Opml, Upload(Document("https://a.example/rss")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(NewsErrorCodes.Busy, (await response.ProblemAsync()).Code());
        Assert.Equal("2", response.Headers.GetValues("Retry-After").Single());
    }

    // ── Concurrency ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC29_A_second_import_is_429_and_the_refusal_releases_the_shared_permit()
    {
        await using var factory = new OdysseyApiFactory([.. ImportClaims, PermissionClaims.TasksCreate, PermissionClaims.TasksUpdate, PermissionClaims.TasksRead]);
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<NewsOpmlImportLimiter>();

        using (var running = limiter.Limiter.AttemptAcquire())
        {
            Assert.True(running.IsAcquired);

            // Three refusals in a row: were the shared two-permit policy leaked by a refusal, the third would not run.
            for (var i = 0; i < 3; i++)
            {
                var refused = await client.PostAsync(Opml, Upload(Document("https://a.example/rss")));
                Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            }

            // A different import surface still gets its shared permit (a bad file answers 400, never 429).
            var other = await client.PostAsync("/api/tasks/ics", Upload("not a calendar", "tasks.ics", "text/calendar"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Opml, Upload(Document("https://a.example/rss")))).StatusCode);
        Assert.Equal(1, limiter.Limiter.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task AC29_The_filter_releases_its_permit_after_success_refusal_and_an_aborted_request()
    {
        var limiter = new NewsOpmlImportLimiter();
        var filter = new NewsOpmlImportConcurrencyFilter(limiter);

        var ok = ResourceContext();
        await filter.OnResourceExecutionAsync(ok, () => Task.FromResult(new ResourceExecutedContext(ok, ok.Filters)));
        Assert.Null(ok.Result);
        Assert.Equal(1, limiter.Limiter.GetStatistics()!.CurrentAvailablePermits);

        var aborted = ResourceContext();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            filter.OnResourceExecutionAsync(aborted, () => throw new OperationCanceledException("client went away")));
        Assert.Equal(1, limiter.Limiter.GetStatistics()!.CurrentAvailablePermits);

        using (limiter.Limiter.AttemptAcquire())
        {
            var refused = ResourceContext();
            var nextCalled = false;
            await filter.OnResourceExecutionAsync(refused, () =>
            {
                nextCalled = true;
                return Task.FromResult(new ResourceExecutedContext(refused, refused.Filters));
            });

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(refused.Result).StatusCode);
        }

        Assert.Equal(1, limiter.Limiter.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task AC29_An_import_while_two_other_imports_hold_the_shared_limit_is_429()
    {
        await using var factory = new OdysseyApiFactory([.. ImportClaims, PermissionClaims.TasksCreate, PermissionClaims.TasksUpdate, PermissionClaims.TasksRead]);
        using var client = StreamingClient(factory);

        // Two task imports whose bodies have not finished arriving hold both shared permits.
        var gates = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        var held = gates.Select(gate => client.PostAsync("/api/tasks/ics", SlowUpload(gate.Task, "tasks.ics", "text/calendar"))).ToList();
        await Task.Delay(300);
        Assert.All(held, h => Assert.False(h.IsCompleted));
        try
        {
            var refused = await client.PostAsync(Opml, Upload(Document("https://a.example/rss")));
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        }
        finally
        {
            foreach (var gate in gates)
            {
                gate.SetResult();
            }

            await Task.WhenAll(held);
        }
    }

    [Fact]
    public async Task AC29_A_second_concurrent_import_is_429_while_the_first_upload_is_still_arriving()
    {
        await using var factory = new OdysseyApiFactory(ImportClaims);
        using var client = StreamingClient(factory);
        var gate = new TaskCompletionSource();
        var held = client.PostAsync(Opml, SlowUpload(gate.Task, "feeds.opml", "text/x-opml"));
        await Task.Delay(300);
        try
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync(Opml, Upload(Document("https://a.example/rss")))).StatusCode);
        }
        finally
        {
            gate.SetResult();
        }

        // The first one's body completes as an unreadable file; its permit is released either way.
        Assert.Equal(HttpStatusCode.BadRequest, (await held).StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<NewsOpmlImportLimiter>().Limiter.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task AC33_The_opml_arm_sets_the_64_KiB_per_second_floor_before_the_body_is_read()
    {
        MinDataRate? seenByNext = null;
        var middleware = new ImportSizeLimitMiddleware(Next, NullLogger<ImportSizeLimitMiddleware>.Instance);
        var context = new DefaultHttpContext();
        var rate = new RecordingRateFeature();
        context.Features.Set<IHttpMinRequestBodyDataRateFeature>(rate);
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ImportSizeLimitAttribute(ImportSurface.NewsOpml)), "opml"));

        await middleware.InvokeAsync(context, new FakeImportExportLimitsLookup(), new FixedUploadLimits(),
            Options.Create(new FormOptions()), Options.Create(new FileStorageOptions()));

        Assert.NotNull(seenByNext);
        Assert.Equal(64 * 1024, seenByNext!.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(10), seenByNext.GracePeriod);

        // Another import surface keeps Kestrel's default.
        var tasks = new DefaultHttpContext();
        var untouched = new RecordingRateFeature();
        tasks.Features.Set<IHttpMinRequestBodyDataRateFeature>(untouched);
        tasks.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ImportSizeLimitAttribute(ImportSurface.Tasks)), "tasks"));
        await middleware.InvokeAsync(tasks, new FakeImportExportLimitsLookup(), new FixedUploadLimits(),
            Options.Create(new FormOptions()), Options.Create(new FileStorageOptions()));
        Assert.Null(untouched.MinDataRate);
        return;

        Task Next(HttpContext http)
        {
            seenByNext ??= http.Features.Get<IHttpMinRequestBodyDataRateFeature>()?.MinDataRate;
            return Task.CompletedTask;
        }
    }

    // ── Limits ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC34_Import_limits_publish_both_fields_and_a_settings_save_is_reflected()
    {
        await using var factory = new OdysseyApiFactory(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.SystemSettingsSecurityUpdate]);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<ImportLimitsDto>("/api/import-limits", Json);
        Assert.Equal(((int?)SystemSettingsDefaults.NewsOpmlMaxImportEntries, SystemSettingsDefaults.NewsOpmlMaxImportMegabytes),
            (before!.NewsOpmlMaxImportEntries, before.NewsOpmlMaxImportMegabytes));

        (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { NewsOpmlMaxImportEntries = 500, NewsOpmlMaxImportMegabytes = 8 }))
            .EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<ImportLimitsDto>("/api/import-limits", Json);
        Assert.Equal(((int?)500, 8), (after!.NewsOpmlMaxImportEntries, after.NewsOpmlMaxImportMegabytes));
    }

    [Fact]
    public async Task A_degraded_news_read_withholds_only_the_entry_cap_and_leaves_the_other_limits_served()
    {
        await using var factory = new OdysseyApiFactory(AllNewsClaims, configureServices: s =>
            s.Replace(ServiceDescriptor.Scoped<INewsSettingsLookup>(_ => new DegradedNewsSettings())));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/import-limits");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var limits = await response.Content.ReadFromJsonAsync<ImportLimitsDto>(Json);
        Assert.Null(limits!.NewsOpmlMaxImportEntries);
        Assert.Equal(SystemSettingsDefaults.NewsOpmlMaxImportMegabytes, limits.NewsOpmlMaxImportMegabytes);
        Assert.Equal(SystemSettingsDefaults.CalendarIcsMaxImportMegabytes, limits.CalendarIcsMaxImportMegabytes);
    }

    private sealed class DegradedNewsSettings : INewsSettingsLookup
    {
        public Task<NewsSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(NewsSettings.Defaults with { IsDegraded = true });

        public Task<IReadOnlyList<NewsPrivateHostGrant>> GetAllowListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NewsPrivateHostGrant>>([]);
    }

    [Fact]
    public async Task AC35_The_size_cap_needs_the_security_claim_and_the_entry_cap_can_only_be_lowered()
    {
        await using var factory = new OdysseyApiFactory([PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { NewsOpmlMaxImportMegabytes = 8 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/system-settings", new SystemSettingsUpdate { NewsOpmlMaxImportEntries = SystemSettingsDefaults.NewsOpmlMaxImportEntries + 1 })).StatusCode);
        Assert.Equal(SystemSettingsDefaults.NewsOpmlMaxImportEntries, SystemSettingsBounds.NewsOpmlMaxImportEntriesMax);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────

    private static ResourceExecutingContext ResourceContext() =>
        new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [], new List<IValueProviderFactory>());

    /// <summary>A multipart body whose file part does not finish until <paramref name="release"/> completes.</summary>
    private static MultipartFormDataContent SlowUpload(Task release, string fileName, string contentType)
    {
        var part = new StreamContent(new GatedStream(release));
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    /// <summary>
    /// A client that streams request bodies: the default one's redirect handler buffers the whole body before sending,
    /// so a slow upload would never be in flight.
    /// </summary>
    private static HttpClient StreamingClient(OdysseyApiFactory factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Once armed, materialising a feed or saving changes throws an interrupted news transaction — the fault a query or
    /// the feed transaction would raise on MariaDB. Interceptors run per execution, so the shared compiled-query cache
    /// does not hide them (a query-compilation interceptor would fire only for the first test to compile a query).
    /// </summary>
    private sealed class NewsQueryFault : IMaterializationInterceptor, ISaveChangesInterceptor
    {
        public volatile bool Armed;

        public InterceptionResult<object> CreatingInstance(MaterializationInterceptionData materializationData, InterceptionResult<object> result) =>
            Armed && materializationData.EntityType.ClrType == typeof(NewsFeed) ? throw new NewsTransactionRetryException() : result;

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Armed ? throw new NewsTransactionRetryException() : ValueTask.FromResult(result);
    }

    private sealed class FailingDns : INewsDnsResolver
    {
        public int Calls;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("The import must not resolve a host.");
        }
    }

    private sealed class FailingHandler : DelegatingHandler
    {
        public static int Sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Sends);
            throw new InvalidOperationException("The import must not send a request.");
        }
    }

    private sealed class RecordingRateFeature : IHttpMinRequestBodyDataRateFeature
    {
        public MinDataRate? MinDataRate { get; set; }
    }

    private sealed class FixedUploadLimits : IUploadLimitsLookup
    {
        public Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class GatedStream(Task release) : Stream
    {
        private bool sentHead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (!sentHead)
            {
                sentHead = true;
                buffer[offset] = (byte)'B';
                return 1;
            }

            await release.WaitAsync(cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
