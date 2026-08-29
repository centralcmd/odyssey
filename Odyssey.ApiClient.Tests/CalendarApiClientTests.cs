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

    [Fact]
    public async Task ExportAggregateAsync_NoFilters_OmitsQueryString()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        await client.ExportAggregateAsync(null, null, null, null);

        Assert.Equal("/api/calendar-events/ics", handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    // Pins the exact wire format ExportCalendarEventsDialog actually sends: its date fields bind
    // DateTime? values from OdsDatePicker, which come back DateTimeKind.Unspecified (a calendar date,
    // not an instant) — "o" formats an Unspecified DateTime WITHOUT a trailing Z or offset. Every
    // server-side API test uses DateTimeKind.Utc, which DOES emit Z, so this exact string was
    // previously covered at no tier (test-coverage review, PR #345).
    [Fact]
    public async Task ExportAggregateAsync_UnspecifiedKindDates_FormatWithoutTrailingZ()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var to = new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Unspecified);

        await client.ExportAggregateAsync(from, to, null, null);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("from=2030-01-01T00%3A00%3A00.0000000", query);
        Assert.Contains("to=2030-01-15T00%3A00%3A00.0000000", query);
        Assert.DoesNotContain("Z", query);
    }

    [Fact]
    public async Task ExportAggregateAsync_AllFilters_BuildsCombinedQueryString()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);
        var from = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var calendarA = Guid.NewGuid();
        var calendarB = Guid.NewGuid();

        await client.ExportAggregateAsync(from, to, [calendarA, calendarB], "team standup");

        var query = handler.LastRequest!.RequestUri!.Query;
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
    public async Task ExportAggregateAsync_DoesNotSendPagingParameters()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        await client.ExportAggregateAsync(null, null, null, "standup");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("offset", query);
        Assert.DoesNotContain("limit", query);
    }

    [Fact]
    public async Task ExportAggregateAsync_SuccessResponse_UsesContentDispositionFileName()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("BEGIN:VEVENT\r\nEND:VEVENT\r\n"))
                {
                    Headers = { ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "20300101_calendar-events.ics" } },
                },
            },
        };
        // issue #343 §11: the completeness check requires this header to match the body's BEGIN:VEVENT count.
        handler.Response.Headers.Add("X-Odyssey-Export-Rows", "1");
        var client = CreateClient(handler);

        var outcome = await client.ExportAggregateAsync(null, null, null, null);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("20300101_calendar-events.ics", outcome.Value!.FileName);
    }

    [Fact]
    public async Task ExportAggregateAsync_ResponseWithoutContentDisposition_FallsBackToDefaultFileName()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("BEGIN:VEVENT\r\nEND:VEVENT\r\n")),
            },
        };
        handler.Response.Headers.Add("X-Odyssey-Export-Rows", "1");
        var client = CreateClient(handler);

        var outcome = await client.ExportAggregateAsync(null, null, null, null);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("calendar-events.ics", outcome.Value!.FileName);
    }

    [Fact]
    public async Task ExportAggregateAsync_MissingCompletenessHeader_IsTreatedAsFailedDownload()
    {
        // issue #343 §11: a missing X-Odyssey-Export-Rows header fails closed, identically to a short
        // count — never "unverifiable, allow it".
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("BEGIN:VEVENT\r\nEND:VEVENT\r\n")),
            },
        };
        var client = CreateClient(handler);

        var outcome = await client.ExportAggregateAsync(null, null, null, null);

        Assert.False(outcome.IsSuccess);
    }

    [Fact]
    public async Task ExportAggregateAsync_ShortCompletenessCount_IsTreatedAsFailedDownload()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("BEGIN:VEVENT\r\nEND:VEVENT\r\n")),
            },
        };
        // Declares 2 rows but the body contains only 1 BEGIN:VEVENT — a truncated mid-stream response.
        handler.Response.Headers.Add("X-Odyssey-Export-Rows", "2");
        var client = CreateClient(handler);

        var outcome = await client.ExportAggregateAsync(null, null, null, null);

        Assert.False(outcome.IsSuccess);
    }

    [Fact]
    public async Task ExportAggregateAsync_ErrorResponse_ReturnsProblemDetailAsError_NotSuccess()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"detail":"That date range spans more than 92 days."}""", System.Text.Encoding.UTF8, "application/problem+json"),
            },
        };
        var client = CreateClient(handler);

        var outcome = await client.ExportAggregateAsync(null, null, null, null);

        Assert.False(outcome.IsSuccess);
        Assert.Null(outcome.Value);
        Assert.Equal("That date range spans more than 92 days.", outcome.Error);
        Assert.Equal(HttpStatusCode.BadRequest, outcome.Status);
    }

    [Fact]
    public async Task ExportEventAsync_ResponseWithoutContentDisposition_FallsBackToDefaultFileName()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
        };
        var client = CreateClient(handler);

        var result = await client.ExportEventAsync(Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("odyssey-event.ics", result.Value!.FileName);
    }

    [Fact]
    public async Task ExportPatternAsync_ResponseWithoutContentDisposition_FallsBackToDefaultFileName()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
        };
        var client = CreateClient(handler);

        var result = await client.ExportPatternAsync(Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("odyssey-series.ics", result.Value!.FileName);
    }

    // Previously unreachable: these error paths used to toast through the injected ISnackbar, which the
    // test passed as null!, so exercising them threw a NullReferenceException instead of asserting.
    [Fact]
    public async Task ExportEventAsync_ErrorResponse_ReturnsFailureWithoutFile()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"detail":"No such event."}""", System.Text.Encoding.UTF8, "application/problem+json"),
            },
        };
        var client = CreateClient(handler);

        var result = await client.ExportEventAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("No such event.", result.Error);
        Assert.Equal(HttpStatusCode.NotFound, result.Status);
    }

    // A non-JSON error body must still yield a usable message rather than throwing — the fallback
    // chain in ApiProblem.Message (detail → reason phrase → status).
    [Fact]
    public async Task ExportAsync_NonJsonErrorBody_FallsBackToReasonPhrase()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>nope</html>", System.Text.Encoding.UTF8, "text/html"),
                ReasonPhrase = "Forbidden",
            },
        };
        var client = CreateClient(handler);

        var result = await client.ExportAsync(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("Forbidden", result.Error);
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
}
