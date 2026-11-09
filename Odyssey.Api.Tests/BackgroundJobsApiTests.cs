using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Jobs;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The background-jobs HTTP contract (issue #409 §5): the overview's health precedence and totals, the run list's
/// filters, caps and sorts, the polymorphic detail, the cross-claim projection, and the manual run with its per-job
/// claims, cooldown, limiter, conflicts and audit line. Nothing executes a weather, file-storage or thumbnail run
/// here (AC 28) — the worker's executors are covered in <c>Odyssey.Core.Tests</c>.
/// </summary>
public class BackgroundJobsApiTests
{
    private const string Base = "/api/background-jobs";

    private static readonly DateTime Now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Everything the Admin role holds that this page reads or runs.</summary>
    private static readonly string[] Admin =
    [
        PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.WeatherPointsRead, PermissionClaims.WeatherPointsUpdate,
        PermissionClaims.FileStorageRead,
        PermissionClaims.PhotosRead, PermissionClaims.PhotosUpdate,
        PermissionClaims.FileAnalysisRead, PermissionClaims.FileAnalysisCreate,
    ];

    private static readonly string[] SettingsOnly = [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate];

    // ── Authorization ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_endpoint_needs_system_settings_read()
    {
        await using var factory = new ApiFactory([PermissionClaims.PhotosRead, PermissionClaims.PhotosUpdate]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Base}/PhotoThumbnails/runs", null)).StatusCode);
    }

    /// <summary>AC 15: the job's domain claim is required on top of system-settings.update (S1).</summary>
    [Theory]
    [InlineData("Weather")]
    [InlineData("FileStorageMaintenance")]
    [InlineData("PhotoThumbnails")]
    [InlineData("PhotoOriginalBackfill")]
    public async Task A_manual_run_without_the_jobs_own_claim_is_forbidden(string jobType)
    {
        await using var factory = new ApiFactory(SettingsOnly);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Base}/{jobType}/runs", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = factory.Scope();
        Assert.Empty(await scope.Context.JobRuns.ToListAsync());
    }

    [Fact]
    public async Task A_settings_reader_cannot_start_a_run()
    {
        await using var factory = new ApiFactory([PermissionClaims.SystemSettingsRead, PermissionClaims.PhotosUpdate]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Base}/PhotoThumbnails/runs", null)).StatusCode);
    }

    /// <summary>
    /// A14: an unknown job, a number, the Unknown sentinel or a comma-composed list is a 404. The last one parses to
    /// another job's ordinal (1 | 2 = 3), so without the exact-name check it would run file-storage maintenance.
    /// </summary>
    [Theory]
    [InlineData("Nope")]
    [InlineData("2")]
    [InlineData("Unknown")]
    [InlineData("ExchangeRateSync,Weather")]
    [InlineData("ExchangeRateSync, Weather")]
    public async Task An_unknown_job_is_not_found(string jobType)
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Base}/{jobType}/runs", null)).StatusCode);
    }

    // ── Overview (§5.1, AC 21, 13, 30) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_heartbeat_every_job_is_not_ticking_and_the_thresholds_travel_on_the_dto()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var overview = await client.GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");

        Assert.Equal(
            [JobType.ExchangeRateSync, JobType.Weather, JobType.FileStorageMaintenance, JobType.PhotoThumbnails, JobType.PhotoOriginalBackfill,
                JobType.FileAnalysis, JobType.FileBlobBackfill,
                JobType.InstrumentPriceSync],
            overview!.Jobs.Select(job => job.JobType));
        Assert.All(overview.Jobs, job =>
        {
            Assert.Equal(JobHealth.NotTicking, job.Health);
            Assert.Equal(300, job.NotTickingAfterSeconds);
            // The file-analysis queue has no manual run (issue #444): it only runs work a user consented to.
            Assert.Equal(job.JobType != JobType.FileAnalysis, job.CanRunNow);
            Assert.True(job.CanReadDetail);
        });
        Assert.Null(overview.Jobs.Single(job => job.JobType == JobType.Weather).OverdueAfterSeconds);
        Assert.Equal(7200, overview.Jobs.Single(job => job.JobType == JobType.FileStorageMaintenance).OverdueAfterSeconds);
        Assert.Equal(2 * 24 * 3600, overview.Jobs.Single(job => job.JobType == JobType.ExchangeRateSync).OverdueAfterSeconds);
        Assert.Equal(Now, overview.GeneratedAtUtc);
        Assert.NotNull(overview.WorkerHealth.FileStorage);
        Assert.NotNull(overview.WorkerHealth.Thumbnails);
        Assert.NotNull(overview.WorkerHealth.PhotoOriginalBackfill);
    }

    /// <summary>AC 30: an unconfigured job still heartbeats, and reads NotConfigured, not NotTicking.</summary>
    [Fact]
    public async Task A_ticking_job_with_nothing_to_work_on_is_not_configured_and_thumbnails_never_are()
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(context =>
        {
            foreach (var jobType in JobCatalogue.Entries.Select(entry => entry.JobType))
            {
                context.JobHeartbeats.Add(Beat(jobType, Guid.NewGuid()));
            }
        });
        using var client = factory.CreateClient();

        var jobs = (await client.GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview"))!.Jobs.ToDictionary(job => job.JobType);

        Assert.Equal(JobHealth.NotConfigured, jobs[JobType.ExchangeRateSync].Health);
        Assert.Equal(JobHealth.NotConfigured, jobs[JobType.Weather].Health);
        Assert.Equal(JobHealth.NotConfigured, jobs[JobType.FileStorageMaintenance].Health);
        Assert.Equal(JobHealth.NeverRun, jobs[JobType.PhotoThumbnails].Health);
        Assert.Equal(Now.AddSeconds(-30), jobs[JobType.Weather].LastHeartbeatAtUtc);
    }

    /// <summary>
    /// Security review on PR #418: NotConfigured is read from the job's own data (weather points, volumes), so a
    /// caller without the job's detail-read claim sees the health the run log alone gives — NeverRun here — while the
    /// sync, which has no detail claim, still reads NotConfigured.
    /// </summary>
    [Fact]
    public async Task Not_configured_is_shown_only_to_a_caller_who_can_read_the_jobs_detail()
    {
        await using var factory = new ApiFactory(SettingsOnly);
        await factory.SeedAsync(context =>
        {
            foreach (var jobType in JobCatalogue.Entries.Select(entry => entry.JobType))
            {
                context.JobHeartbeats.Add(Beat(jobType, Guid.NewGuid()));
            }
        });
        using var client = factory.CreateClient();

        var jobs = (await client.GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview"))!.Jobs.ToDictionary(job => job.JobType);

        Assert.Equal(JobHealth.NotConfigured, jobs[JobType.ExchangeRateSync].Health);
        Assert.Equal(JobHealth.NeverRun, jobs[JobType.Weather].Health);
        Assert.Equal(JobHealth.NeverRun, jobs[JobType.FileStorageMaintenance].Health);
        Assert.Equal(JobHealth.NeverRun, jobs[JobType.PhotoThumbnails].Health);
    }

    /// <summary>AC 21: the precedence, one state at a time, on the sync (configured by one enabled currency).</summary>
    [Theory]
    [InlineData("none", JobHealth.NeverRun)]
    [InlineData("recent-success", JobHealth.Healthy)]
    [InlineData("old-success", JobHealth.Overdue)]
    [InlineData("failed-then-skipped", JobHealth.Failing)]
    [InlineData("running-live", JobHealth.Running)]
    [InlineData("running-dead", JobHealth.NeverRun)]
    public async Task The_health_follows_its_precedence(string scenario, JobHealth expected)
    {
        await using var factory = new ApiFactory(Admin);
        var worker = Guid.NewGuid();
        await factory.SeedAsync(context =>
        {
            context.ExchangeRateSyncCurrencies.Add(new ExchangeRateSyncCurrency { CurrencyCode = "USD", Enabled = true, UpdatedAtUtc = Now });
            context.JobHeartbeats.Add(Beat(JobType.ExchangeRateSync, worker));
            switch (scenario)
            {
                case "recent-success":
                    context.JobRuns.Add(Finished<ExchangeRateSyncJobRun>(JobRunStatus.Succeeded, Now.AddHours(-1)));
                    break;
                case "old-success":
                    context.JobRuns.Add(Finished<ExchangeRateSyncJobRun>(JobRunStatus.Succeeded, Now.AddDays(-3)));
                    break;
                case "failed-then-skipped":
                    context.JobRuns.Add(Finished<ExchangeRateSyncJobRun>(JobRunStatus.Failed, Now.AddHours(-2), JobFailureCategory.ProviderUnavailable));
                    context.JobRuns.Add(Finished<ExchangeRateSyncJobRun>(JobRunStatus.Skipped, Now.AddHours(-1)));
                    break;
                case "running-live":
                    context.JobRuns.Add(Running<ExchangeRateSyncJobRun>(worker));
                    break;
                case "running-dead":
                    context.JobRuns.Add(Running<ExchangeRateSyncJobRun>(Guid.NewGuid()));
                    break;
            }
        });
        using var client = factory.CreateClient();

        var overview = await client.GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");
        var sync = overview!.Jobs.Single(job => job.JobType == JobType.ExchangeRateSync);

        Assert.Equal(expected, sync.Health);
        Assert.Equal(scenario == "running-live", overview.Running.Any(run => run.JobType == JobType.ExchangeRateSync));
    }

    /// <summary>AC 13, 33 (S-N6): the Held health stays visible; its reason, check and resume time follow the claim.</summary>
    [Fact]
    public async Task A_held_job_shows_its_reason_only_to_a_holder_of_its_read_claim()
    {
        var worker = Guid.NewGuid();
        await using var admin = new ApiFactory(Admin);
        await admin.SeedAsync(context =>
        {
            context.JobHeartbeats.Add(Beat(JobType.PhotoThumbnails, worker));
            context.JobRuns.Add(Held<PhotoThumbnailJobRun>(worker, JobHeldReason.DecoderPaused));
            context.JobHeartbeats.Add(Beat(JobType.FileStorageMaintenance, worker));
            var held = Held<FileStorageMaintenanceJobRun>(worker, JobHeldReason.StorageCheckFailed);
            held.HeldByCheck = "VolumeLost";
            context.JobRuns.Add(held);
        });
        await using var reader = new ApiFactory(SettingsOnly, sharingStoreWith: admin);

        var full = await admin.CreateClient().GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");
        var thumbnails = full!.Jobs.Single(job => job.JobType == JobType.PhotoThumbnails);
        var storage = full.Jobs.Single(job => job.JobType == JobType.FileStorageMaintenance);
        Assert.Equal((JobHealth.Held, JobHeldReason.DecoderPaused), (thumbnails.Health, thumbnails.HeldReason));
        Assert.Equal((JobHealth.Held, JobHeldReason.StorageCheckFailed, "VolumeLost"), (storage.Health, storage.HeldReason, storage.HeldCheck));

        var partial = await reader.CreateClient().GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");
        thumbnails = partial!.Jobs.Single(job => job.JobType == JobType.PhotoThumbnails);
        storage = partial.Jobs.Single(job => job.JobType == JobType.FileStorageMaintenance);
        Assert.Equal(JobHealth.Held, thumbnails.Health);
        Assert.Null(thumbnails.HeldReason);
        Assert.Null(storage.HeldCheck);
        Assert.Null(storage.HeldUntilUtc);
        Assert.False(thumbnails.CanReadDetail);
        Assert.False(thumbnails.CanRunNow);
        Assert.True(partial.Jobs.Single(job => job.JobType == JobType.ExchangeRateSync).CanRunNow);
        Assert.Null(partial.WorkerHealth.FileStorage);
        Assert.Null(partial.WorkerHealth.Thumbnails);
    }

    /// <summary>§5.1 Totals: runs count finished non-held rows; Skipped is in runs only; held counts held rows.</summary>
    [Fact]
    public async Task The_totals_count_by_their_rules()
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(context =>
        {
            context.JobRuns.AddRange(
                Finished<WeatherJobRun>(JobRunStatus.Succeeded, Now.AddHours(-1)),
                Finished<WeatherJobRun>(JobRunStatus.PartiallySucceeded, Now.AddHours(-2)),
                Finished<WeatherJobRun>(JobRunStatus.Failed, Now.AddHours(-3), JobFailureCategory.ProviderUnavailable),
                Finished<WeatherJobRun>(JobRunStatus.Skipped, Now.AddDays(-2)),
                Finished<WeatherJobRun>(JobRunStatus.Succeeded, Now.AddDays(-8)),
                Running<WeatherJobRun>(Guid.NewGuid()),
                Held<PhotoThumbnailJobRun>(Guid.NewGuid(), JobHeldReason.BreakerTripped));
        });
        using var client = factory.CreateClient();

        var overview = await client.GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");
        var weather = overview!.Jobs.Single(job => job.JobType == JobType.Weather).Totals;

        Assert.Equal(new JobRunTotalsWindow { Runs = 3, Succeeded = 2, Failed = 1, Held = 0 }, weather.Last24h);
        Assert.Equal(new JobRunTotalsWindow { Runs = 4, Succeeded = 2, Failed = 1, Held = 0 }, weather.Last7d);
        Assert.Equal(new JobRunTotalsWindow { Runs = 4, Succeeded = 2, Failed = 1, Held = 1 }, overview.Totals.Last7d);
    }

    // ── The run list (§5.2, AC 11) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_job_status_and_trigger_and_hides_counts_without_the_claim()
    {
        await using var admin = new ApiFactory(Admin);
        await admin.SeedAsync(context =>
        {
            var weather = Finished<WeatherJobRun>(JobRunStatus.Failed, Now.AddHours(-1), JobFailureCategory.ProviderUnavailable);
            weather.Trigger = JobRunTrigger.Manual;
            ((WeatherJobRun)weather).PointsAttempted = 2;
            ((WeatherJobRun)weather).PointsFailed = 2;
            weather.Finish(JobRunStatus.Failed, JobFailureCategory.ProviderUnavailable, Now.AddHours(-1));
            context.JobRuns.AddRange(
                weather,
                Finished<WeatherJobRun>(JobRunStatus.Succeeded, Now.AddHours(-2)),
                Finished<PhotoThumbnailJobRun>(JobRunStatus.Failed, Now.AddHours(-3), JobFailureCategory.Internal),
                Finished<ExchangeRateSyncJobRun>(JobRunStatus.Succeeded, Now.AddHours(-4)));
        });
        using var client = admin.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingJobRun>>(
            $"{Base}/runs?jobTypes=Weather&jobTypes=PhotoThumbnails&statuses=Failed&triggers=Manual");
        var only = Assert.Single(page!.Items);
        Assert.Equal((JobType.Weather, JobRunStatus.Failed, JobFailureCategory.ProviderUnavailable), (only.JobType, only.Status, only.FailureCategory));
        Assert.Equal((2, 2), (only.ItemsProcessed, only.ItemsFailed));

        var all = await client.GetFromJsonAsync<PagedResult<ExistingJobRun>>($"{Base}/runs");
        Assert.Equal(4, all!.TotalCount);
        Assert.Equal(25, all.Limit);
        Assert.Equal(all.Items.Select(run => run.StartedAtUtc).OrderByDescending(at => at), all.Items.Select(run => run.StartedAtUtc));

        await using var reader = new ApiFactory(SettingsOnly, sharingStoreWith: admin);
        var hidden = await reader.CreateClient().GetFromJsonAsync<PagedResult<ExistingJobRun>>($"{Base}/runs?jobTypes=Weather&statuses=Failed");
        var row = Assert.Single(hidden!.Items);
        Assert.Null(row.ItemsProcessed);
        Assert.Null(row.ItemsFailed);
        Assert.Equal(JobRunStatus.Failed, row.Status);
    }

    [Theory]
    [InlineData("jobTypes=Weather&jobTypes=Weather")]
    [InlineData("jobTypes=Unknown")]
    [InlineData("statuses=Bogus")]
    [InlineData("limit=101")]
    [InlineData("limit=0")]
    [InlineData("offset=100001")]
    [InlineData("search=x")]
    [InlineData("startedFrom=2026-11-05&startedTo=2026-11-04&tz=Europe/Oslo")]
    [InlineData("startedFrom=2026-11-05")]
    [InlineData("startedFrom=2026-11-05&tz=../../etc/passwd")]
    [InlineData("startedFrom=2026-11-05&tz=Mars/Olympus_Mons")]
    public async Task An_invalid_list_query_is_a_400(string query)
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Base}/runs?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwd", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Olympus", body, StringComparison.Ordinal);
    }

    /// <summary>S-N3: a time zone the host cannot resolve is the same generic 400, keyed tz, echoing nothing.</summary>
    [Fact]
    public async Task An_unknown_time_zone_is_a_generic_400_keyed_tz()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Base}/runs?startedFrom=2026-11-05&tz=Mars/Olympus_Mons");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            problem.GetProperty("errors").EnumerateObject(),
            error => string.Equals(error.Name, "tz", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Security review on PR #418: a date at either end of the calendar used to overflow the local-midnight
    /// conversion and 500. It is a 400 keyed on the date that was out of range.
    /// </summary>
    [Theory]
    [InlineData("startedTo=9999-12-31", "startedTo")]
    [InlineData("startedFrom=0001-01-01", "startedFrom")]
    [InlineData("startedFrom=0001-01-01&startedTo=0001-01-02", "startedFrom")]
    [InlineData("startedFrom=9999-12-31&startedTo=9999-12-31", "startedTo")]
    [InlineData("startedFrom=1979-12-31", "startedFrom")]
    public async Task An_out_of_range_date_is_a_400_keyed_on_the_date(string dates, string field)
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Base}/runs?{dates}&tz=Pacific/Kiritimati");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            problem.GetProperty("errors").EnumerateObject(),
            error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("startedFrom=1980-01-01&tz=Pacific/Kiritimati")]
    [InlineData("startedTo=9998-12-31&tz=Etc/GMT-14")]
    [InlineData("startedTo=9998-12-31&tz=Pacific/Pago_Pago")]
    public async Task The_extreme_accepted_dates_list_without_error(string query)
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Base}/runs?{query}")).StatusCode);
    }

    [Fact]
    public async Task A_time_zone_without_dates_is_ignored()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Base}/runs?tz=Mars/Olympus_Mons")).StatusCode);
    }

    /// <summary>AC 11: calendar days in Europe/Oslo across the October DST change (25 hours long).</summary>
    [Fact]
    public async Task The_date_filter_is_local_days_across_a_dst_change()
    {
        await using var factory = new ApiFactory(Admin);
        // 2026-10-25 in Oslo: CEST until 03:00 local, then CET — the day runs from 22:00Z on the 24th to 23:00Z on the 25th.
        var inside = new[] { new DateTime(2026, 10, 24, 22, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 22, 59, 0, DateTimeKind.Utc) };
        var outside = new[] { new DateTime(2026, 10, 24, 21, 59, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Utc) };
        await factory.SeedAsync(context =>
        {
            foreach (var at in inside.Concat(outside))
            {
                context.JobRuns.Add(Finished<WeatherJobRun>(JobRunStatus.Succeeded, at, started: at));
            }
        });
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingJobRun>>(
            $"{Base}/runs?startedFrom=2026-10-25&startedTo=2026-10-25&tz=Europe/Oslo&sortDir=asc");

        Assert.Equal(inside, page!.Items.Select(run => run.StartedAtUtc));
    }

    /// <summary>AC 11: a run without a duration is last in both directions.</summary>
    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task The_duration_sort_puts_running_rows_last(string direction)
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(context =>
        {
            context.JobRuns.AddRange(
                Running<WeatherJobRun>(Guid.NewGuid()),
                Finished<WeatherJobRun>(JobRunStatus.Succeeded, Now.AddMinutes(-10), started: Now.AddMinutes(-12)),
                Finished<WeatherJobRun>(JobRunStatus.Succeeded, Now.AddMinutes(-20), started: Now.AddMinutes(-21)));
        });
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<ExistingJobRun>>($"{Base}/runs?sortBy=Duration&sortDir={direction}");
        var durations = page!.Items.Select(run => run.DurationMs).ToList();

        Assert.Null(durations[^1]);
        Assert.Equal(direction == "asc" ? [60_000L, 120_000L] : [120_000L, 60_000L], durations.Take(2).Select(d => d!.Value));
    }

    // ── The detail (§5.3, AC 12–14) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_detail_is_discriminated_by_kind_and_hides_the_jobs_fields_without_its_claim()
    {
        await using var admin = new ApiFactory(Admin);
        var id = Guid.Empty;
        await admin.SeedAsync(context =>
        {
            var run = (PhotoThumbnailJobRun)Finished<PhotoThumbnailJobRun>(JobRunStatus.Succeeded, Now.AddMinutes(-1));
            run.PhotosRendered = 7;
            run.Finish(JobRunStatus.Succeeded, null, Now.AddMinutes(-1));
            run.RequestedByUserId = "someone";
            run.WorkerId = Guid.NewGuid();
            context.JobRuns.Add(run);
            id = run.JobRunId;
        });

        var raw = await admin.CreateClient().GetStringAsync($"{Base}/runs/{id}");
        using (var document = JsonDocument.Parse(raw))
        {
            Assert.Equal("PhotoThumbnails", document.RootElement.GetProperty("$kind").GetString());
            Assert.Equal("PhotoThumbnails", document.RootElement.GetProperty("jobType").GetString());
            Assert.Equal("Succeeded", document.RootElement.GetProperty("status").GetString());
        }

        // AC 14: no user id, worker id or anything else identifying on the wire.
        Assert.DoesNotContain("someone", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("requestedBy", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workerId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generation", raw, StringComparison.OrdinalIgnoreCase);

        var detail = Assert.IsType<PhotoThumbnailJobRunDetail>(JsonSerializer.Deserialize<JobRunDetail>(raw, Json));
        Assert.Equal((7, 7), (detail.PhotosRendered, detail.ItemsProcessed));

        await using var reader = new ApiFactory(SettingsOnly, sharingStoreWith: admin);
        var hidden = Assert.IsType<PhotoThumbnailJobRunDetail>(
            await reader.CreateClient().GetFromJsonAsync<JobRunDetail>($"{Base}/runs/{id}"));
        Assert.Null(hidden.PhotosRendered);
        Assert.Null(hidden.ItemsProcessed);
        Assert.Equal(JobRunStatus.Succeeded, hidden.Status);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.CreateClient().GetAsync($"{Base}/runs/{Guid.NewGuid()}")).StatusCode);
    }

    // ── Manual run (§3.5, §5.4) ───────────────────────────────────────────────────────────────────────────

    /// <summary>AC 15, 20, 28: accepted, unclaimed (never executed inline under Testing), audited; then the cooldown.</summary>
    [Fact]
    public async Task A_thumbnail_run_is_accepted_left_unclaimed_audited_and_then_cooled_down()
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(context => context.PhotoThumbnailJobs.Add(PendingJob()));
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Base}/PhotoThumbnails/runs", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = await response.Content.ReadFromJsonAsync<JobRunStarted>();
        await using (var scope = factory.Scope())
        {
            var run = await scope.Context.JobRuns.SingleAsync();
            Assert.Equal(started!.Id, run.JobRunId);
            Assert.Equal((JobRunTrigger.Manual, JobRunStatus.Running), (run.Trigger, run.Status));
            Assert.Null(run.ClaimedAtUtc);
            Assert.Equal("background-jobs-actor", run.RequestedByUserId);
        }

        var audit = Assert.Single(factory.Logs.ForCategory("SystemSettingsService"), entry => entry.Message.Contains("manual run", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, audit.Level);
        Assert.Contains("background-jobs-actor", audit.Message, StringComparison.Ordinal);
        Assert.Contains("PhotoThumbnails", audit.Message, StringComparison.Ordinal);
        Assert.Contains(started!.Id.ToString(), audit.Message, StringComparison.Ordinal);

        var again = await client.PostAsync($"{Base}/PhotoThumbnails/runs", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.NotNull(again.Headers.RetryAfter);
        var problem = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Now.AddSeconds(60), problem.GetProperty("retryAfterUtc").GetDateTime());
    }

    /// <summary>A manual run with nothing to do is finished Skipped at once and returned 200.</summary>
    [Theory]
    [InlineData("Weather")]
    [InlineData("FileStorageMaintenance")]
    [InlineData("PhotoThumbnails")]
    [InlineData("ExchangeRateSync")]
    [InlineData("PhotoOriginalBackfill")]
    public async Task A_run_with_nothing_to_do_is_skipped_at_once(string jobType)
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Base}/{jobType}/runs", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<ExistingJobRun>();
        Assert.Equal((Enum.Parse<JobType>(jobType), JobRunStatus.Skipped, JobRunTrigger.Manual), (run!.JobType, run.Status, run.Trigger));
    }

    [Fact]
    public async Task A_weather_run_with_a_tracked_point_is_accepted()
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(context =>
        {
            context.SystemSettings.Single(setting => setting.Key == SystemSettingsKeys.WeatherUserAgentContact).Value = "ops@example.org";
            context.WeatherPoints.Add(new WeatherPoint
            {
                WeatherPointId = Guid.NewGuid(), Name = "Home", ForecastEnabled = true, IsTracked = true, EnabledAt = Now, CurrentSiteVersion = 1,
            });
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"{Base}/Weather/runs", null)).StatusCode);
    }

    [Fact]
    public async Task A_second_manual_run_while_one_is_live_is_a_409_already_running()
    {
        await using var factory = new ApiFactory(Admin);
        var worker = Guid.NewGuid();
        await factory.SeedAsync(context =>
        {
            context.PhotoThumbnailJobs.Add(PendingJob());
            context.JobHeartbeats.Add(Beat(JobType.PhotoThumbnails, worker));
            var live = Running<PhotoThumbnailJobRun>(worker);
            live.Trigger = JobRunTrigger.Manual;
            live.StartedAtUtc = Now.AddMinutes(-5);
            context.JobRuns.Add(live);
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Base}/PhotoThumbnails/runs", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobRunConflictCodes.AlreadyRunning, problem.GetProperty("code").GetString());
    }

    /// <summary>AC 8: a dead manual request no worker took is abandoned inline, and the new request proceeds.</summary>
    [Fact]
    public async Task A_stale_request_is_abandoned_inline_and_the_run_proceeds()
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(context =>
        {
            context.PhotoThumbnailJobs.Add(PendingJob());
            var stale = Running<PhotoThumbnailJobRun>(null);
            stale.Trigger = JobRunTrigger.Manual;
            stale.ClaimedAtUtc = null;
            stale.StartedAtUtc = Now.AddMinutes(-5);
            context.JobRuns.Add(stale);
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"{Base}/PhotoThumbnails/runs", null)).StatusCode);
        await using var scope = factory.Scope();
        var runs = await scope.Context.JobRuns.OrderBy(r => r.StartedAtUtc).ToListAsync();
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Abandoned), (runs[0].Status, runs[0].FailureCategory));
        Assert.Equal(JobRunStatus.Running, runs[1].Status);
    }

    /// <summary>AC 16, 33: a held thumbnail worker refuses the run with no row; the held extensions follow photos.read.</summary>
    [Fact]
    public async Task A_thumbnail_run_while_the_worker_is_held_is_a_409_held()
    {
        await using var admin = new ApiFactory(Admin);
        await admin.SeedAsync(context =>
        {
            context.PhotoThumbnailJobs.Add(PendingJob());
            context.PhotoThumbnailWorkerHeartbeats.Add(new PhotoThumbnailWorkerHeartbeat
            {
                WorkerName = "worker-1", State = ThumbnailWorkerState.Tripped, UpdatedAtUtc = Now.AddSeconds(-10),
            });
            for (var i = 1; i <= 3; i++)
            {
                context.PhotoThumbnailIncidents.Add(new PhotoThumbnailIncident { Kind = PhotoThumbnailIncidentKind.Crash, OccurredAtUtc = Now.AddMinutes(-i) });
            }
        });

        var response = await admin.CreateClient().PostAsync($"{Base}/PhotoThumbnails/runs", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobRunConflictCodes.Held, problem.GetProperty("code").GetString());
        Assert.Equal("BreakerTripped", problem.GetProperty("heldReason").GetString());
        Assert.Equal(Now.AddMinutes(-1).AddHours(1), problem.GetProperty("resumesAtUtc").GetDateTime());

        await using var updater = new ApiFactory(
            [PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate, PermissionClaims.PhotosUpdate], sharingStoreWith: admin);
        var withheld = await (await updater.CreateClient().PostAsync($"{Base}/PhotoThumbnails/runs", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobRunConflictCodes.Held, withheld.GetProperty("code").GetString());
        Assert.False(withheld.TryGetProperty("heldReason", out _));
        Assert.False(withheld.TryGetProperty("resumesAtUtc", out _));

        await using var scope = admin.Scope();
        Assert.Empty(await scope.Context.JobRuns.ToListAsync());
    }

    /// <summary>
    /// Architect review on PR #418: when every live worker's file-storage pass is held on a failed storage check, a
    /// manual pass is refused 409 held with no row — no worker would claim it, and a 202 would only end Abandoned
    /// with a warning that blames a missing worker. One unheld live worker is enough for the run to proceed.
    /// </summary>
    [Fact]
    public async Task A_file_storage_run_while_every_live_worker_is_held_is_a_409_held()
    {
        await using var factory = new ApiFactory(Admin);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await factory.SeedAsync(context =>
        {
            context.JobHeartbeats.AddRange(Beat(JobType.FileStorageMaintenance, first), Beat(JobType.FileStorageMaintenance, second));
            context.JobRuns.AddRange(
                Held<FileStorageMaintenanceJobRun>(first, JobHeldReason.StorageCheckFailed),
                Held<FileStorageMaintenanceJobRun>(second, JobHeldReason.StorageCheckFailed));
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Base}/FileStorageMaintenance/runs", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JobRunConflictCodes.Held, problem.GetProperty("code").GetString());
        Assert.Equal("StorageCheckFailed", problem.GetProperty("heldReason").GetString());
        await using (var scope = factory.Scope())
        {
            Assert.Equal(2, await scope.Context.JobRuns.CountAsync());
        }

        await using var partly = new ApiFactory(Admin);
        await partly.SeedAsync(context =>
        {
            context.JobHeartbeats.AddRange(Beat(JobType.FileStorageMaintenance, first), Beat(JobType.FileStorageMaintenance, second));
            context.JobRuns.Add(Held<FileStorageMaintenanceJobRun>(first, JobHeldReason.StorageCheckFailed));
        });

        var proceeds = await partly.CreateClient().PostAsync($"{Base}/FileStorageMaintenance/runs", null);

        Assert.NotEqual(HttpStatusCode.Conflict, proceeds.StatusCode);
    }

    /// <summary>AC 15: the sixth manual-run request in a minute from one user is refused by the limiter.</summary>
    [Fact]
    public async Task The_sixth_request_in_a_minute_is_rate_limited()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        foreach (var jobType in new[] { "Weather", "FileStorageMaintenance", "PhotoThumbnails", "ExchangeRateSync", "Weather", "Weather" })
        {
            last = await client.PostAsync($"{Base}/{jobType}/runs", null);
            statuses.Add(last.StatusCode);
        }

        Assert.All(statuses.Take(4), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[4]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
        var problem = await last!.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("retryAfterUtc", out _));
        Assert.DoesNotContain("less than a minute ago", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>AC 25: the sync's own endpoints keep their shape, now reading the shared log.</summary>
    [Fact]
    public async Task The_exchange_rate_sync_endpoints_read_the_shared_log()
    {
        await using var factory = new ApiFactory(Admin);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Base}/ExchangeRateSync/runs", null)).StatusCode);

        var runs = await client.GetFromJsonAsync<PagedResult<ExistingExchangeRateSyncRun>>("/api/system-settings/exchange-rate-sync/runs");
        var run = Assert.Single(runs!.Items);
        Assert.Equal((ExchangeRateSyncTrigger.Manual, ExchangeRateSyncRunStatus.Skipped), (run.Trigger, run.Status));
        var status = await client.GetFromJsonAsync<ExchangeRateSyncStatus>("/api/system-settings/exchange-rate-sync/status");
        Assert.Equal(Now.AddSeconds(60), status!.ManualRunAvailableAtUtc);
    }

    // ── The photo-original move (issue #428) ─────────────────────────────────────────────────────────────

    private static void SeedLegacyPhoto(OdysseyContext context) =>
        Odyssey.Testing.FileStorage.PhotoTestData.StageLegacyPhoto(context, createdAt: Now.AddDays(-30));

    [Fact]
    public async Task A_photo_original_move_with_a_legacy_photo_left_is_accepted()
    {
        await using var factory = new ApiFactory(Admin);
        await factory.SeedAsync(SeedLegacyPhoto);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Base}/PhotoOriginalBackfill/runs", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>
    /// Every live worker held for storage: the move cannot run, so the request is a 409 held. Held only for its back-off
    /// it can — a manual run is what ends a back-off early — so that request is accepted.
    /// </summary>
    [Theory]
    [InlineData(JobHeldReason.StorageUnavailable, HttpStatusCode.Conflict)]
    [InlineData(JobHeldReason.BackingOff, HttpStatusCode.Accepted)]
    public async Task A_photo_original_move_is_refused_only_while_every_worker_is_held_for_storage(JobHeldReason reason, HttpStatusCode expected)
    {
        await using var factory = new ApiFactory(Admin);
        var worker = Guid.NewGuid();
        await factory.SeedAsync(context =>
        {
            SeedLegacyPhoto(context);
            context.JobHeartbeats.Add(Beat(JobType.PhotoOriginalBackfill, worker));
            context.JobRuns.Add(Held<PhotoOriginalBackfillJobRun>(worker, reason));
        });

        var response = await factory.CreateClient().PostAsync($"{Base}/PhotoOriginalBackfill/runs", null);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// The status card and the back-off's resume time are file-storage data: shown with file-storage.read, and null
    /// without it, like the other jobs' worker health.
    /// </summary>
    [Fact]
    public async Task The_move_status_and_its_back_off_are_shown_only_with_file_storage_read()
    {
        var worker = Guid.NewGuid();
        var resumes = Now.AddMinutes(14);
        await using var admin = new ApiFactory(Admin);
        await admin.SeedAsync(context =>
        {
            SeedLegacyPhoto(context);
            context.JobHeartbeats.Add(Beat(JobType.PhotoOriginalBackfill, worker));
            context.JobRuns.Add(Held<PhotoOriginalBackfillJobRun>(worker, JobHeldReason.BackingOff));
            // The lease row is seeded (HasData) once the store is created.
            var lease = context.PhotoOriginalBackfillLeases.Find(PhotoOriginalBackfillLease.SingletonId)!;
            lease.NextAttemptAtUtc = resumes;
            lease.ConsecutiveEnvironmentalFailures = 3;
            lease.CopiedSharedCount = 2;
        });
        await using var reader = new ApiFactory(SettingsOnly, sharingStoreWith: admin);

        var full = await admin.CreateClient().GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");
        var move = full!.Jobs.Single(job => job.JobType == JobType.PhotoOriginalBackfill);
        Assert.Equal((JobHealth.Held, JobHeldReason.BackingOff, resumes), (move.Health, move.HeldReason, move.HeldUntilUtc));
        var status = full.WorkerHealth.PhotoOriginalBackfill!;
        Assert.Equal((1, 2, resumes), (status.Remaining, status.CopiedShared, status.BackingOffUntilUtc));
        Assert.Equal(["error", "hash", "read", "too-large"], status.FailedByCode.Keys.Order());

        var partial = await reader.CreateClient().GetFromJsonAsync<BackgroundJobsOverview>($"{Base}/overview");
        move = partial!.Jobs.Single(job => job.JobType == JobType.PhotoOriginalBackfill);
        Assert.Equal((JobHealth.Held, (JobHeldReason?)null, (DateTime?)null), (move.Health, move.HeldReason, move.HeldUntilUtc));
        Assert.Null(partial.WorkerHealth.PhotoOriginalBackfill);
    }

    [Fact]
    public async Task A_move_run_detail_carries_its_counters_only_with_file_storage_read()
    {
        var id = Guid.NewGuid();
        await using var admin = new ApiFactory(Admin);
        await admin.SeedAsync(context =>
        {
            var run = new PhotoOriginalBackfillJobRun
            {
                JobRunId = id, Trigger = JobRunTrigger.Scheduled, StartedAtUtc = Now.AddMinutes(-3), ClaimedAtUtc = Now.AddMinutes(-3),
                LastActivityAtUtc = Now, PhotosMoved = 20, PhotosCopiedShared = 3, PhotosParked = 2, Interruptions = 0,
            };
            run.Finish(JobRunStatus.PartiallySucceeded, JobFailureCategory.UnsupportedInput, Now);
            context.JobRuns.Add(run);
        });
        await using var reader = new ApiFactory(SettingsOnly, sharingStoreWith: admin);

        var raw = await admin.CreateClient().GetStringAsync($"{Base}/runs/{id}");
        Assert.StartsWith("{\"$kind\":\"PhotoOriginalBackfill\"", raw, StringComparison.Ordinal);
        var detail = Assert.IsType<PhotoOriginalBackfillJobRunDetail>(JsonSerializer.Deserialize<JobRunDetail>(raw, Json));
        Assert.Equal((20, 3, 2, 0, 23, 2), (detail.PhotosMoved, detail.PhotosCopiedShared, detail.PhotosParked, detail.Interruptions,
            detail.ItemsProcessed, detail.ItemsFailed));

        var hidden = Assert.IsType<PhotoOriginalBackfillJobRunDetail>(
            await reader.CreateClient().GetFromJsonAsync<JobRunDetail>($"{Base}/runs/{id}"));
        Assert.Equal(((int?)null, (int?)null), (hidden.PhotosMoved, hidden.ItemsProcessed));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static JobHeartbeat Beat(JobType jobType, Guid worker) =>
        new() { JobType = jobType, WorkerId = worker, LastTickAtUtc = Now.AddSeconds(-30) };

    private static JobRun Finished<TRun>(JobRunStatus status, DateTime finished, JobFailureCategory? category = null, DateTime? started = null)
        where TRun : JobRun, new()
    {
        var run = new TRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, StartedAtUtc = started ?? finished.AddMinutes(-1),
            ClaimedAtUtc = started ?? finished, LastActivityAtUtc = finished,
        };
        run.Finish(status, category, finished);
        return run;
    }

    private static JobRun Running<TRun>(Guid? worker)
        where TRun : JobRun, new() => new TRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, Status = JobRunStatus.Running,
            StartedAtUtc = Now.AddMinutes(-1), ClaimedAtUtc = Now.AddMinutes(-1), LastActivityAtUtc = Now.AddMinutes(-1), WorkerId = worker,
        };

    private static TRun Held<TRun>(Guid worker, JobHeldReason reason)
        where TRun : JobRun, new() => new()
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Scheduled, Status = JobRunStatus.Held, HeldReason = reason,
            StartedAtUtc = Now.AddMinutes(-10), ClaimedAtUtc = Now.AddMinutes(-10), LastActivityAtUtc = Now.AddMinutes(-10), WorkerId = worker,
        };

    private static PhotoThumbnailJobEntry PendingJob() => new()
    {
        PhotoId = Guid.NewGuid(), State = PhotoThumbnailJobState.Pending, NextAttemptAtUtc = Now, UpdatedAtUtc = Now,
    };

    private sealed class ApiFactory(IReadOnlyCollection<string> permissions, OdysseyApiFactory? sharingStoreWith = null)
        : OdysseyApiFactory(
            permissions,
            "background-jobs-actor",
            // No photo volume: the file-storage job's "not configured" state is the host with no volume at all.
            configuration: new Dictionary<string, string?> { [NoFileVolumeKey] = "true" },
            configureServices: services => services.AddSingleton<TimeProvider>(new FixedClock()),
            sharingStoreWith: sharingStoreWith)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }

        public async Task SeedAsync(Action<OdysseyContext> seed)
        {
            await using var scope = Scope();
            await scope.Context.Database.EnsureCreatedAsync();
            seed(scope.Context);
            await scope.Context.SaveChangesAsync();
        }

        public ContextScope Scope() => new(Services.CreateAsyncScope());
    }

    private sealed class ContextScope(AsyncServiceScope scope) : IAsyncDisposable
    {
        public OdysseyContext Context { get; } = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }
}
