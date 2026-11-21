using System.Net;
using System.Net.Http.Headers;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Unit coverage for <see cref="CalendarApiClient"/>'s aggregate-export query-string assembly and
/// file-response parsing (issue #340) — the parts of the client the PR #345 test-coverage review found
/// untested at any tier.
/// </summary>
/// <remarks>
/// This class used to construct the client with <c>api: null!, snackbar: null!</c> and could therefore
/// only exercise paths that touched neither — which excluded every error path, since those toasted.
/// Now that the client returns results instead of presenting them and depends only on
/// <see cref="IOdysseyApi"/>, it can be built for real over a recording handler and the error paths
/// are reachable.
/// </remarks>
public class CalendarApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>
        /// Media types of a multipart request's parts, snapshotted during the send. The client disposes
        /// the <see cref="MultipartFormDataContent"/> once the response is read, so the parts cannot be
        /// inspected after the call returns.
        /// </summary>
        public List<string?> LastPartMediaTypes { get; } = [];

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPartMediaTypes.Clear();
            if (request.Content is MultipartFormDataContent multipart)
            {
                LastPartMediaTypes.AddRange(multipart.Select(part => part.Headers.ContentType?.MediaType));
            }

            return Task.FromResult(Response);
        }
    }

    private static CalendarApiClient CreateClient(RecordingHandler handler) =>
        new(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

    private static CalendarApiClient CreateClient() =>
        new(new OdysseyApi(new HttpClient(new RecordingHandler()) { BaseAddress = new Uri("http://localhost/") }));

    private static string AggregateQuery(DateTime? from, DateTime? to, IReadOnlyCollection<Guid>? calendarIds, string? search) =>
        new Uri(CreateClient().ExportAggregateUrl(from, to, calendarIds, search)).Query;

    [Fact]
    public void ExportAggregateUrl_NoFilters_OmitsQueryString()
    {
        var url = new Uri(CreateClient().ExportAggregateUrl(null, null, null, null));

        Assert.Equal("/api/calendar-events/ics", url.PathAndQuery);
    }

    // Pins the exact wire format ExportCalendarEventsDialog actually sends: its date fields bind
    // DateTime? values from OdsDatePicker, which come back DateTimeKind.Unspecified (a calendar date,
    // not an instant) — "o" formats an Unspecified DateTime WITHOUT a trailing Z or offset. Every
    // server-side API test uses DateTimeKind.Utc, which DOES emit Z, so this exact string was
    // previously covered at no tier (test-coverage review, PR #345).
    [Fact]
    public void ExportAggregateUrl_UnspecifiedKindDates_FormatWithoutTrailingZ()
    {
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var to = new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Unspecified);

        var query = AggregateQuery(from, to, null, null);

        Assert.Contains("from=2030-01-01T00%3A00%3A00.0000000", query);
        Assert.Contains("to=2030-01-15T00%3A00%3A00.0000000", query);
        Assert.DoesNotContain("Z", query);
    }

    [Fact]
    public void ExportAggregateUrl_AllFilters_BuildsCombinedQueryString()
    {
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var calendarA = Guid.NewGuid();
        var calendarB = Guid.NewGuid();

        var query = AggregateQuery(from, to, [calendarA, calendarB], "team standup");

        Assert.Contains("from=2030-01-01T00%3A00%3A00.0000000Z", query);
        Assert.Contains("to=2030-01-15T00%3A00%3A00.0000000Z", query);
        Assert.Contains($"calendarIds={calendarA}", query);
        Assert.Contains($"calendarIds={calendarB}", query);
        Assert.Contains("search=team%20standup", query);
    }

    // The aggregate export streams a whole .ics and takes no paging, so its URL must NOT carry the
    // offset/limit PagedQuery would append. Guards the hand-built query string against being
    // "simplified" into PagedQuery later.
    [Fact]
    public void ExportAggregateUrl_DoesNotSendPagingParameters()
    {
        var query = AggregateQuery(null, null, null, "standup");

        Assert.DoesNotContain("offset", query);
        Assert.DoesNotContain("limit", query);
    }

    [Fact]
    public async Task ImportAsync_PostsMultipartAsTextCalendar()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"importedCount":1}""", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        var client = CreateClient(handler);
        var upload = new ApiUpload("mine.ics", "application/octet-stream", 3, () => new MemoryStream([1, 2, 3]));

        await client.ImportAsync(Guid.NewGuid(), upload);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("text/calendar", Assert.Single(handler.LastPartMediaTypes));
    }

    private static RecordingHandler SummaryHandler() => new()
    {
        Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"eventCount":3,"seriesCount":1,"occurrenceCount":7}""", System.Text.Encoding.UTF8, "application/json"),
        },
    };

    [Fact]
    public async Task Summaries_ReadTheCounts_FromEachExportsOwnSummaryRoute()
    {
        var id = Guid.NewGuid();
        var cases = new (Func<CalendarApiClient, Task<ApiResult<Odyssey.Dtos.Journal.CalendarIcsExportSummary>>> Call, string Path)[]
        {
            (c => c.SummarizeExportAsync(id), $"/api/calendars/{id}/ics/summary"),
            (c => c.SummarizeEventExportAsync(id), $"/api/calendar-events/{id}/ics/summary"),
            (c => c.SummarizePatternExportAsync(id), $"/api/recurrence-patterns/{id}/ics/summary"),
            (c => c.SummarizeAggregateExportAsync(null, null, null, null), "/api/calendar-events/ics/summary"),
        };

        foreach (var (call, path) in cases)
        {
            var handler = SummaryHandler();
            var result = await call(CreateClient(handler));

            Assert.Equal(path, handler.LastRequest!.RequestUri!.AbsolutePath);
            Assert.Equal((3, 1, 7), (result.Value!.EventCount, result.Value.SeriesCount, result.Value.OccurrenceCount));
        }
    }

    [Fact]
    public async Task ExportUrls_AreAbsolute_AndTheAggregateCarriesTheSameQueryAsItsSummary()
    {
        var id = Guid.NewGuid();
        var handler = SummaryHandler();
        var client = CreateClient(handler);
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await client.SummarizeAggregateExportAsync(from, from.AddDays(3), [id], "dentist");

        var aggregate = new Uri(client.ExportAggregateUrl(from, from.AddDays(3), [id], "dentist"));

        Assert.Equal("http://localhost/api/calendar-events/ics", aggregate.GetLeftPart(UriPartial.Path));
        Assert.Equal(handler.LastRequest!.RequestUri!.Query, aggregate.Query);
        Assert.Equal($"http://localhost/api/calendars/{id}/ics", client.ExportUrl(id));
        Assert.Equal($"http://localhost/api/calendar-events/{id}/ics", client.ExportEventUrl(id));
        Assert.Equal($"http://localhost/api/recurrence-patterns/{id}/ics", client.ExportPatternUrl(id));
        Assert.Equal($"http://localhost/api/calendars/{id}/ics", client.ImportUrl(id));
    }

    [Fact]
    public async Task The_attachments_mode_travels_on_the_three_exports_and_their_summaries_only_when_omitted()
    {
        var id = Guid.NewGuid();
        var handler = SummaryHandler();
        var client = CreateClient(handler);

        await client.SummarizeExportAsync(id, Odyssey.Dtos.Journal.IcsAttachmentsMode.Omit);
        Assert.Equal($"/api/calendars/{id}/ics/summary?attachments=omit", handler.LastRequest!.RequestUri!.PathAndQuery);
        await client.SummarizeEventExportAsync(id, Odyssey.Dtos.Journal.IcsAttachmentsMode.Omit);
        Assert.Equal($"/api/calendar-events/{id}/ics/summary?attachments=omit", handler.LastRequest!.RequestUri!.PathAndQuery);
        await client.SummarizePatternExportAsync(id, Odyssey.Dtos.Journal.IcsAttachmentsMode.Include);
        Assert.Equal($"/api/recurrence-patterns/{id}/ics/summary", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal($"http://localhost/api/calendars/{id}/ics?attachments=omit", client.ExportUrl(id, Odyssey.Dtos.Journal.IcsAttachmentsMode.Omit));
        Assert.Equal($"http://localhost/api/calendar-events/{id}/ics?attachments=omit", client.ExportEventUrl(id, Odyssey.Dtos.Journal.IcsAttachmentsMode.Omit));
        Assert.Equal($"http://localhost/api/recurrence-patterns/{id}/ics", client.ExportPatternUrl(id, Odyssey.Dtos.Journal.IcsAttachmentsMode.Include));
    }

    [Fact]
    public void TransferUrls_WithoutABaseAddress_ThrowAClearInvalidOperationException()
    {
        var client = new CalendarApiClient(new OdysseyApi(new HttpClient(SummaryHandler())));

        var error = Assert.Throws<InvalidOperationException>(() => client.ExportUrl(Guid.NewGuid()));

        Assert.Contains("BaseAddress", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportAsync_SendsAllowRemovals_OnlyWhenAsked(bool allowRemovals)
    {
        string? body = null;
        var handler = new CapturingBodyHandler(text => body = text);
        var client = new CalendarApiClient(new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));
        var upload = new ApiUpload("mine.ics", "text/calendar", 3, () => new MemoryStream([1, 2, 3]));

        await client.ImportAsync(Guid.NewGuid(), upload, allowRemovals);

        Assert.Equal(allowRemovals, body!.Contains("name=allowRemovals", StringComparison.Ordinal));
    }

    private sealed class CapturingBodyHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"importedCount":1}""", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
