using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The background-jobs typed client (issue #409 §5): the routes and the list query it builds, and the tolerance the
/// contract promises (F8, FS1, AC 24) — an enum name or a <c>$kind</c> this build does not know reads as
/// <c>Unknown</c> or as the base detail, never as an exception.
/// </summary>
public class BackgroundJobsApiClientTests
{
    private const string Base = "/api/background-jobs";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string ResponseJson { get; set; } = """{"items":[],"offset":0,"limit":25,"totalCount":0}""";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseJson, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (BackgroundJobsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new BackgroundJobsApiClient(api), handler);
    }

    [Fact]
    public async Task ListRuns_sends_the_window_every_filter_the_zone_and_the_sort()
    {
        var (client, handler) = Create();

        await client.ListRunsAsync(3, 25, new JobRunsFilter
        {
            JobTypes = [JobType.Weather, JobType.PhotoThumbnails],
            Statuses = [JobRunStatus.Failed],
            Triggers = [JobRunTrigger.Manual],
            StartedFrom = new DateOnly(2026, 10, 25),
            StartedTo = new DateOnly(2026, 10, 26),
            TimeZone = "Europe/Oslo",
            SortBy = JobRunSortBy.Duration,
            Ascending = true,
        });

        Assert.Equal($"{Base}/runs", handler.LastRequest!.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query);
        Assert.Contains("offset=50", query);
        Assert.Contains("limit=25", query);
        Assert.Contains("jobTypes=Weather", query);
        Assert.Contains("jobTypes=PhotoThumbnails", query);
        Assert.Contains("statuses=Failed", query);
        Assert.Contains("triggers=Manual", query);
        Assert.Contains("startedFrom=2026-10-25", query);
        Assert.Contains("startedTo=2026-10-26", query);
        Assert.Contains("tz=Europe/Oslo", query);
        Assert.Contains("sortBy=Duration", query);
        Assert.Contains("sortDir=asc", query);
    }

    [Fact]
    public async Task A_zone_is_sent_only_with_a_date()
    {
        var (client, handler) = Create();

        await client.ListRunsAsync(1, 25, new JobRunsFilter { TimeZone = "Europe/Oslo" });

        Assert.DoesNotContain("tz=", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task RunNow_posts_to_the_jobs_name()
    {
        var (client, handler) = Create();
        handler.Status = HttpStatusCode.Accepted;
        var id = Guid.NewGuid();
        handler.ResponseJson = $$"""{"id":"{{id}}"}""";

        var result = await client.RunNowAsync(JobType.FileStorageMaintenance);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"{Base}/FileStorageMaintenance/runs", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal((HttpStatusCode.Accepted, id), (result.Status, result.Value!.Id));
    }

    [Fact]
    public async Task A_409_carries_its_code_and_held_extensions()
    {
        var (client, handler) = Create();
        handler.Status = HttpStatusCode.Conflict;
        handler.ResponseJson = """{"status":409,"detail":"held","code":"job-run.held","heldReason":"BreakerTripped","resumesAtUtc":"2026-11-05T11:00:00Z"}""";

        var result = await client.RunNowAsync(JobType.PhotoThumbnails);

        Assert.Equal(JobRunConflictCodes.Held, result.Problem!.Code);
        Assert.Equal("BreakerTripped", result.Problem.Extension<string>("heldReason"));
        Assert.Equal(JobHeldReason.BreakerTripped, JsonSerializer.Deserialize<JobHeldReason>("\"BreakerTripped\""));
        Assert.Equal(JobHeldReason.Unknown, JsonSerializer.Deserialize<JobHeldReason>("\"NotYetInvented\""));
        Assert.Equal(JobHeldReason.Unknown, JsonSerializer.Deserialize<JobHeldReason>("99"));
    }

    /// <summary>AC 24: a future enum name and a future <c>$kind</c> are read, not thrown on.</summary>
    [Fact]
    public async Task An_unknown_enum_name_and_an_unknown_kind_are_tolerated()
    {
        var (client, handler) = Create();
        handler.ResponseJson = """
            {"$kind":"Backups","id":"6f0e1c2a-0000-0000-0000-000000000001","jobType":"Backups","trigger":"Webhook",
             "status":"Paused","failureCategory":"Quota","heldReason":"Maintenance","startedAtUtc":"2026-11-05T10:00:00Z",
             "lastActivityAtUtc":"2026-11-05T10:00:00Z","futureField":42}
            """;

        var result = await client.GetRunAsync(Guid.NewGuid());

        Assert.True(result.IsSuccess, result.Problem?.Detail ?? result.Problem?.Title);
        var detail = result.Value!;
        Assert.Equal(typeof(JobRunDetail), detail.GetType());
        Assert.Equal(
            (JobType.Unknown, JobRunTrigger.Unknown, JobRunStatus.Unknown, JobFailureCategory.Unknown, JobHeldReason.Unknown),
            (detail.JobType, detail.Trigger, detail.Status, detail.FailureCategory!.Value, detail.HeldReason!.Value));
    }

    [Fact]
    public async Task A_known_kind_reads_as_its_subtype_and_the_overview_reads_its_enums_by_name()
    {
        var (client, handler) = Create();
        handler.ResponseJson = """
            {"$kind":"Weather","id":"6f0e1c2a-0000-0000-0000-000000000002","jobType":"Weather","trigger":"Scheduled",
             "status":"PartiallySucceeded","failureCategory":"ProviderRateLimited","startedAtUtc":"2026-11-05T10:00:00Z",
             "lastActivityAtUtc":"2026-11-05T10:00:00Z","pointsAttempted":3,"pointsFailed":1,"failureKind":3}
            """;

        var weather = Assert.IsType<WeatherJobRunDetail>((await client.GetRunAsync(Guid.NewGuid())).Value);
        Assert.Equal((3, 1, WeatherFetchFailureKind.RateLimited), (weather.PointsAttempted, weather.PointsFailed, weather.FailureKind));

        handler.ResponseJson = """
            {"generatedAtUtc":"2026-11-05T10:00:00Z","jobs":[{"jobType":"Weather","health":"Exploded","notTickingAfterSeconds":300,
             "totals":{"last24h":{"runs":1,"succeeded":1,"failed":0,"held":0},"last7d":{"runs":1,"succeeded":1,"failed":0,"held":0}}}],
             "running":[],"workerHealth":{"fileStorage":null,"thumbnails":{"state":"Running","resumesAtUtc":null}}}
            """;
        var overview = (await client.GetOverviewAsync()).Value!;
        Assert.Equal((JobType.Weather, JobHealth.Unknown), (overview.Jobs[0].JobType, overview.Jobs[0].Health));
        Assert.Null(overview.WorkerHealth.FileStorage);
    }

    [Fact]
    public void The_client_is_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost/") });
        services.AddOdysseyApiClient();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<BackgroundJobsApiClient>(scope.ServiceProvider.GetRequiredService<IBackgroundJobsApiClient>());
    }

    /// <summary>The DTO enums serialise as names, so a client never depends on an ordinal.</summary>
    [Fact]
    public void The_enums_travel_as_names()
    {
        var json = JsonSerializer.Serialize(new ExistingJobRun { JobType = JobType.Weather, Status = JobRunStatus.Held, Trigger = JobRunTrigger.Manual });

        Assert.Contains("\"Weather\"", json);
        Assert.Contains("\"Held\"", json);
        Assert.Contains("\"Manual\"", json);
    }
}
