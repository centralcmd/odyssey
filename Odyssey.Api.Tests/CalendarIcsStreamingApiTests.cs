using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Controllers;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.Logging;
using Xunit;
using ContextCalendar = Odyssey.Context.Calendar;
using ContextCalendarEvent = Odyssey.Context.CalendarEvent;

namespace Odyssey.Api.Tests;

/// <summary>
/// Calendar <c>.ics</c> export and import over HTTP: streaming, limits and round-trip fixes (issue #516) where only the
/// endpoint shows them — throttling, download headers, problem-details codes, the aborted stream, the 403 and the upload
/// spool. The import and export rules are pinned at the service tier in
/// <c>Odyssey.Core.Tests/Ics/CalendarIcsStreamingServiceTests</c>; the real-engine behaviour (no export transaction, the
/// batch transactions, the query counts) is in <c>Odyssey.IntegrationTests</c>.
/// </summary>
public class CalendarIcsStreamingApiTests(CalendarIcsStreamingApiTests.Host host)
    : SharedApiHostTests<CalendarIcsStreamingApiTests.Host>(host)
{
    private const string ActorUserId = "calendar-ics-streaming-actor";
    private const string CalendarsPath = "/api/calendars";
    private const string EventsPath = "/api/calendar-events";
    private const string PatternsPath = "/api/recurrence-patterns";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
        PermissionClaims.CalendarDelete,
    ];

    private static readonly string[] ReadWriteNoDelete =
    [
        PermissionClaims.CalendarRead, PermissionClaims.CalendarCreate, PermissionClaims.CalendarUpdate,
    ];

    private static readonly string[] WithSettings =
    [
        .. ReadWrite, PermissionClaims.SystemSettingsRead, PermissionClaims.SystemSettingsUpdate,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    // ------------------------------------------------------------------ AC 2: throttling on all eight

    public static TheoryData<Type, string> ThrottledActions() => new()
    {
        { typeof(CalendarIcsController), nameof(CalendarIcsController.Export) },
        { typeof(CalendarIcsController), nameof(CalendarIcsController.SummarizeExport) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.ExportIcs) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.SummarizeIcsExport) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.ExportEventIcs) },
        { typeof(CalendarEventsController), nameof(CalendarEventsController.SummarizeEventIcsExport) },
        { typeof(RecurrencePatternsController), nameof(RecurrencePatternsController.ExportIcs) },
        { typeof(RecurrencePatternsController), nameof(RecurrencePatternsController.SummarizeIcsExport) },
    };

    [Theory]
    [MemberData(nameof(ThrottledActions))]
    public void Every_calendar_export_and_summary_carries_the_export_limiter_and_the_concurrency_filter(Type controller, string action)
    {
        var method = controller.GetMethod(action)!;

        Assert.Equal(
            ImportExportRateLimiting.ExportConcurrencyPolicy,
            method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(typeof(ExportConcurrencyFilter), method.GetCustomAttribute<TypeFilterAttribute>()?.ImplementationType);
    }

    // ------------------------------------------------------------------ AC 3, 4, 5: caps and summaries

    [Fact]
    public async Task Summaries_match_their_exports_and_the_rows_header()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Counts");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Standup", count: 3);
        var one = await CreateEventAsync(client, calendar.CalendarId, "One");
        await CreateEventAsync(client, calendar.CalendarId, "Two");

        foreach (var (export, summary, events, rules, occurrences) in new[]
        {
            ($"{EventsPath}/{one.CalendarEventId}/ics", $"{EventsPath}/{one.CalendarEventId}/ics/summary", 1, 0, 1),
            ($"{CalendarsPath}/{calendar.CalendarId}/ics", $"{CalendarsPath}/{calendar.CalendarId}/ics/summary", 3, 1, 5),
            ($"{PatternsPath}/{series.RecurrencePatternId}/ics", $"{PatternsPath}/{series.RecurrencePatternId}/ics/summary", 1, 1, 3),
            ($"{EventsPath}/ics", $"{EventsPath}/ics/summary", 3, 1, 5),
            ($"{EventsPath}/ics?search=One", $"{EventsPath}/ics/summary?search=One", 1, 0, 1),
        })
        {
            var planned = (await (await client.GetAsync(summary)).Content.ReadFromJsonAsync<CalendarIcsExportSummary>())!;
            var response = await client.GetAsync(export);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(events, planned.EventCount);
            Assert.Equal(rules, planned.SeriesCount);
            Assert.Equal(occurrences, planned.OccurrenceCount);
            Assert.Equal(planned.EventCount, Count(body, "BEGIN:VEVENT"));
            Assert.Equal(planned.SeriesCount, Count(body, "RRULE:"));
            Assert.Equal(planned.EventCount.ToString(), response.Headers.GetValues("X-Odyssey-Export-Rows").Single());
        }
    }

    [Fact]
    public async Task Every_export_carries_the_download_headers()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Headers");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Weekly", count: 2);
        var single = await CreateEventAsync(client, calendar.CalendarId, "Single");

        foreach (var url in new[]
        {
            $"{CalendarsPath}/{calendar.CalendarId}/ics", $"{EventsPath}/{single.CalendarEventId}/ics",
            $"{PatternsPath}/{series.RecurrencePatternId}/ics", $"{EventsPath}/ics",
        })
        {
            var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.True(response.Headers.Contains("X-Odyssey-Export-Rows"));
        }
    }

    [Fact]
    public async Task Count_cap_refuses_each_export_and_summary_with_the_calendar_code_and_unlimited_admits_them()
    {
        var factory = Factory.ForCaller(WithSettings);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Capped");
        var series = await CreateSeriesAsync(client, calendar.CalendarId, "Six", count: 6);
        // An edited occurrence is a RECURRENCE-ID component since issue #519: the calendar export carries the master, the
        // override and the single event (3), the series export the master and the override (2), and the unfiltered
        // aggregate — flat for a series with any exception — the six rows and the single event (7).
        await EditFirstOccurrenceAsync(factory, series.RecurrencePatternId);
        var single = await CreateEventAsync(client, calendar.CalendarId, "Single");
        // The single-event export plans exactly one VEVENT, and the cap's floor is 1, so no cap can refuse it: it is
        // asserted to pass under the cap rather than left out (tester finding 3).
        var singleUrl = $"{EventsPath}/{single.CalendarEventId}/ics";

        var urls = new[]
        {
            $"{CalendarsPath}/{calendar.CalendarId}/ics", $"{PatternsPath}/{series.RecurrencePatternId}/ics", $"{EventsPath}/ics",
        };

        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxExportEvents = new CapacityLimit { Value = 1 } });
        foreach (var url in urls)
        {
            foreach (var target in new[] { url, Summary(url) })
            {
                var response = await client.GetAsync(target);
                Assert.True(HttpStatusCode.BadRequest == response.StatusCode, target);
                var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
                Assert.Equal(ExportLimitExceededException.CalendarEventsCode, problem!.Extensions["code"]?.ToString());
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(singleUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Summary(singleUrl))).StatusCode);

        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxExportEvents = new CapacityLimit { Unlimited = true } });
        foreach (var url in urls.Append(singleUrl))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Summary(url))).StatusCode);
        }
    }

    [Theory]
    [InlineData("calendar")]
    [InlineData("pattern")]
    public async Task Byte_cap_aborts_the_export_while_its_summary_still_answers(string kind)
    {
        // AC 3 on the per-calendar and the series export (the aggregate paths are in CalendarEventsIcsApiTests). A
        // single event cannot reach the 1 MB floor of the cap, so its writer is covered in CalendarIcsExportByteCapTests.
        var factory = Factory.ForCaller(WithSettings);
        using var client = factory.CreateClient();
        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxExportMegabytes = 1 });
        var calendar = await CreateCalendarAsync(client, "Bulky");
        var patternId = await SeedBulkySeriesAsync(factory, calendar.CalendarId, 1000);
        var url = kind == "calendar" ? $"{CalendarsPath}/{calendar.CalendarId}/ics" : $"{PatternsPath}/{patternId}/ics";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Summary(url))).StatusCode);
        await AssertAbortedMidStreamAsync(client, url, expectedRows: 1000);
    }

    // The headers arrive as a 200 with the planned count, and only the body read fails: any other status, or a
    // complete body, fails the test (tester finding 2).
    internal static async Task AssertAbortedMidStreamAsync(HttpClient client, string url, int expectedRows)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedRows.ToString(), response.Headers.GetValues("X-Odyssey-Export-Rows").Single());

        // TestServer surfaces HttpContext.Abort() on the body as this chain; any other failure is not the abort.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        var io = Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("The application aborted the request.", Assert.IsType<OperationCanceledException>(io.InnerException).Message);
    }

    [Fact]
    public async Task Summaries_return_404_exactly_where_their_exports_do()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        foreach (var url in new[] { $"{CalendarsPath}/{unknown}/ics", $"{EventsPath}/{unknown}/ics", $"{PatternsPath}/{unknown}/ics" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Summary(url))).StatusCode);
        }
    }

    [Fact]
    public async Task A_summary_followed_at_once_by_its_export_succeeds()
    {
        // AC 18b: the summary holds its permits only while it plans, so the download never 429s its own user.
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Sequence");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics/summary")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{CalendarsPath}/{calendar.CalendarId}/ics")).StatusCode);
        }
    }

    // ------------------------------------------------------------------ AC 15: cancellation

    [Fact]
    public async Task Allow_removals_without_calendar_delete_is_a_403_and_nothing_is_applied()
    {
        var factory = Factory.ForCaller(ReadWriteNoDelete);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "NoDelete");
        var file = Vcalendar(
            Vevent("new@example.com", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", "SUMMARY:New"),
            Cancelled("other@example.com", "Other"));

        var response = await PostIcsAsync(client, calendar.CalendarId, file, allowRemovals: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OdysseyContext>().CalendarEvents
            .AnyAsync(e => e.CalendarId == calendar.CalendarId));
    }

    // ------------------------------------------------------------------ AC 16b, 17: streaming caps and partial failure

    [Fact]
    public async Task A_file_whose_first_component_is_unclosed_is_a_plain_400()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Unclosed");

        var response = await PostIcsAsync(client, calendar.CalendarId, "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\n");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.False(problem.Extensions.ContainsKey("code"));
    }

    [Fact]
    public async Task A_failure_in_batch_three_keeps_batches_one_and_two_and_a_rerun_completes_the_file()
    {
        // Groups commit in UID-hash order since issue #519 (AR3), so the failure is one every group spends alike: three
        // batches' worth of single-occurrence series against an occurrence budget of two batches fail in batch three
        // whatever the order. This copy pins the HTTP error shape and the re-run on EF InMemory, so it lowers the batch
        // size to keep the file small; the committed-batch semantics at the production batch size are
        // CalendarIcsStreamingIntegrationTests' twin on MariaDB.
        const int batchSize = 5;
        // A host of its own: the batch size is a replaced service.
        await using var factory = new ApiFactory(WithSettings, importBatchGroups: batchSize);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Partial");
        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxAggregateOccurrences = 2 * batchSize });
        var file = Vcalendar([.. Enumerable.Range(0, 3 * batchSize).Select(i => Vevent(
            $"{SyntheticGuid(i)}@odyssey.local", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:Row {i}",
            "RRULE:FREQ=DAILY;COUNT=1"))]);

        var failed = await PostIcsAsync(client, calendar.CalendarId, file);

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var problem = (await failed.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(CalendarIcsImportPartiallyAppliedException.DiscriminatorCode, problem.Extensions["code"]?.ToString());
        Assert.Equal("2", problem.Extensions["committedBatches"]?.ToString());
        Assert.Equal($"{2 * batchSize}", problem.Extensions["committedEvents"]?.ToString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var series = await db.RecurrencePatterns.Where(p => p.CalendarId == calendar.CalendarId).ToListAsync();
            Assert.Equal(2 * batchSize, series.Count);
            // The match keys were written in the same batch as their rows, so the re-run finds them.
            Assert.All(series, p => Assert.NotNull(p.ImportSourceUid));
        }

        await SetAsync(client, new SystemSettingsUpdate { CalendarIcsMaxAggregateOccurrences = 20000 });
        var rerun = await ImportAsync(client, calendar.CalendarId, file);

        Assert.Equal(2 * batchSize, rerun.UpdatedCount);
        Assert.Equal(batchSize, rerun.ImportedCount);
    }

    [Fact]
    public async Task The_buffered_upload_is_deleted_after_a_failed_import()
    {
        // AC 16 (tester finding 6): ASP.NET Core spools a multipart body over 64 KB to ASPNETCORE_TEMP. The probe
        // proves the spool existed while the request ran, so its absence afterwards is the deletion, not a body that
        // was never written to disk.
        var probe = new SpoolProbe($"spool-{Guid.NewGuid():N}");
        // A host of its own: the probe is a startup filter.
        await using var factory = new ApiFactory(ReadWrite, startup: probe);
        using var client = factory.CreateClient();
        var calendar = await CreateCalendarAsync(client, "Spooled");
        var file = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nX-MARKER:" + probe.Marker + "\r\nX-PAD:" + new string('p', 32 * 1024) + "\r\n"
            + string.Concat(Enumerable.Range(0, CalendarIcsService.ImportBatchSize).Select(i => Standalone($"spool-{i}@example.com", $"Spool {i}")))
            + "BEGIN:VEVENT\r\nUID:never\r\n";
        Assert.True(Encoding.UTF8.GetByteCount(file) > 64 * 1024);

        var response = await PostIcsAsync(client, calendar.CalendarId, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(probe.SeenDuringRequest, "The upload was never spooled to disk, so this test proves nothing.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.SpoolFiles().Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Empty(probe.SpoolFiles());
    }

    // ------------------------------------------------------------------ helpers

    // Looks for the request's spool file (ASPNETCORE_*.tmp holding the marker) as the response starts, which is
    // before the framework disposes it.
    private sealed class SpoolProbe(string marker) : IStartupFilter
    {
        public string Marker { get; } = marker;

        public bool SeenDuringRequest { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                // OnStarting runs before the client can see any response, so the check never races the test, and
                // after model binding has spooled the form.
                if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Value!.EndsWith("/ics", StringComparison.Ordinal))
                {
                    context.Response.OnStarting(() =>
                    {
                        SeenDuringRequest |= SpoolFiles().Count > 0;
                        return Task.CompletedTask;
                    });
                }

                await inner(context);
            });
            next(app);
        };

        public List<string> SpoolFiles()
        {
            var directory = Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") is { Length: > 0 } configured
                ? configured
                : Path.GetTempPath();
            var found = new List<string>();
            foreach (var path in Directory.EnumerateFiles(directory, "ASPNETCORE_*.tmp"))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var head = new byte[4096];
                    var read = stream.Read(head, 0, head.Length);
                    if (Encoding.UTF8.GetString(head, 0, read).Contains(Marker, StringComparison.Ordinal))
                    {
                        found.Add(path);
                    }
                }
                catch (IOException)
                {
                    // Deleted, or another test's spool held exclusively, between the listing and the open.
                }
            }

            return found;
        }
    }

    private static string Summary(string url)
    {
        var at = url.IndexOf('?');
        return at < 0 ? url + "/summary" : url[..at] + "/summary" + url[at..];
    }

    private static Guid SyntheticGuid(int i) => new($"00000000-0000-0000-0000-{i:D12}");

    private static string Standalone(string uid, string title) =>
        Vevent(uid, "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{title}");

    private static string Cancelled(string uid, string title) =>
        Vevent(uid, "STATUS:CANCELLED", "DTSTART:20300101T090000Z", "DTEND:20300101T100000Z", $"SUMMARY:{title}");

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Vcalendar(params string[] vevents) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" + string.Concat(vevents) + "END:VCALENDAR\r\n";

    private static string Vevent(string uid, params string[] lines) =>
        "BEGIN:VEVENT\r\nUID:" + uid + "\r\n" + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\n";

    private static async Task SetAsync(HttpClient client, SystemSettingsUpdate update) =>
        (await client.PutAsJsonAsync("/api/system-settings", update)).EnsureSuccessStatusCode();

    private static async Task<ExistingCalendar> CreateCalendarAsync(HttpClient client, string name)
    {
        var post = await client.PostAsJsonAsync(CalendarsPath, new NewCalendar { Name = name, Color = "#0369A1" });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendar>())!;
    }

    private static async Task<ExistingCalendarEvent> CreateEventAsync(HttpClient client, Guid calendarId, string title)
    {
        var start = new DateTime(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var post = await client.PostAsJsonAsync(EventsPath, new NewCalendarEvent
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingCalendarEvent>())!;
    }

    private static async Task<ExistingRecurrencePattern> CreateSeriesAsync(
        HttpClient client, Guid calendarId, string title, int count, DateTime? start = null)
    {
        var first = start ?? new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var post = await client.PostAsJsonAsync(PatternsPath, new NewRecurrencePattern
        {
            CalendarId = calendarId,
            Title = title,
            StartDateTime = first,
            EndDateTime = first.AddHours(1),
            Frequency = Odyssey.Dtos.Journal.RecurrenceFrequency.Daily,
            Interval = 1,
            OccurrenceCount = count,
        });
        post.EnsureSuccessStatusCode();
        return (await post.Content.ReadFromJsonAsync<ExistingRecurrencePattern>())!;
    }

    private static async Task EditFirstOccurrenceAsync(ApiFactory factory, Guid patternId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var first = await db.CalendarEvents.Where(e => e.RecurrencePatternId == patternId).OrderBy(e => e.StartDateTime).FirstAsync();
        first.Title = "Edited";
        await db.SaveChangesAsync();
    }

    // A series of `rows` daily occurrences, each with a 2,000-character description (~2 KB a VEVENT); the first is
    // edited, so every export of it writes row by row.
    private static async Task<Guid> SeedBulkySeriesAsync(ApiFactory factory, Guid calendarId, int rows)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var start = new DateTime(2030, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var stamp = new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var pattern = new RecurrencePattern
        {
            CalendarId = calendarId,
            Title = "Bulky",
            Description = new string('x', 2000),
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
            Frequency = Odyssey.Context.RecurrenceFrequency.Daily,
            Interval = 1,
            OccurrenceCount = rows,
            CreatedByUserId = ActorUserId,
            CreatedAt = stamp,
            UpdatedAt = stamp,
        };
        db.RecurrencePatterns.Add(pattern);
        for (var i = 0; i < rows; i++)
        {
            db.CalendarEvents.Add(new ContextCalendarEvent
            {
                CalendarId = calendarId,
                Title = i == 0 ? "Edited" : "Bulky",
                Description = new string('x', 2000),
                StartDateTime = start.AddDays(i),
                EndDateTime = start.AddDays(i).AddHours(1),
                RecurrencePattern = pattern,
                CreatedByUserId = ActorUserId,
                CreatedAt = stamp,
                UpdatedAt = stamp,
            });
        }

        await db.SaveChangesAsync();
        return pattern.RecurrencePatternId;
    }

    private static async Task<IcsImportResult> ImportAsync(HttpClient client, Guid calendarId, string ics, bool allowRemovals = false)
    {
        var response = await PostIcsAsync(client, calendarId, ics, allowRemovals);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<IcsImportResult>())!;
    }

    private static async Task<HttpResponseMessage> PostIcsAsync(HttpClient client, Guid calendarId, string ics, bool allowRemovals = false)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        content.Add(file, "file", "calendar.ics");
        if (allowRemovals)
        {
            content.Add(new StringContent("true"), "allowRemovals");
        }

        return await client.PostAsync($"{CalendarsPath}/{calendarId}/ics", content);
    }

    /// <summary>The class's shared host: a calendar editor, with logs captured and cleared per test.</summary>
    public sealed class Host() : ApiFactory(ReadWrite, new RecordingLoggerProvider());

    public class ApiFactory : OdysseyApiFactory
    {
        public ApiFactory(
            IReadOnlyCollection<string> permissions, RecordingLoggerProvider? logs = null, TimeProvider? clock = null,
            IStartupFilter? startup = null, int? importBatchGroups = null)
            : base(permissions, ActorUserId, configureServices: services =>
            {
                if (startup is not null)
                {
                    services.AddSingleton(startup);
                }

                if (importBatchGroups is { } groups)
                {
                    services.RemoveAll<CalendarIcsService>();
                    services.AddScoped(provider =>
                    {
                        var service = ActivatorUtilities.CreateInstance<CalendarIcsService>(provider);
                        service.ImportBatchGroups = groups;
                        return service;
                    });
                }

                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }

                var name = $"calendar-ics-streaming-{Guid.NewGuid()}";
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(name));
                if (logs is not null)
                {
                    services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
                }
            }) =>
            Logs = logs;

        public RecordingLoggerProvider? Logs { get; }

        public override async Task ResetStoreAsync()
        {
            await base.ResetStoreAsync();
            Logs?.Clear();
        }
    }
}
