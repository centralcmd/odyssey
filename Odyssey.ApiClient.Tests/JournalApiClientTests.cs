using System.Net;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The journal list / export query string. Both endpoints bind the same
/// <c>JournalEntriesQueryParams</c> server-side, which is what lets the page's "Export filtered"
/// describe exactly the set on screen — but only if the typed client actually emits every filter on
/// both. A filter silently dropped here does not fail: it returns a WIDER set than the caller asked
/// for, which reads as "the filter does nothing" on the list and as extra rows in a downloaded file.
/// </summary>
public class JournalApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (JournalApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new JournalApiClient(api), handler);
    }

    private static string QueryOf(RecordingHandler handler) => handler.LastRequest!.RequestUri!.Query;

    [Fact]
    public async Task List_WithAttachmentFilters_SendsBothFlags()
    {
        var (client, handler) = Create();

        await client.ListAsync(hasPhotos: true, hasFiles: true);

        var query = QueryOf(handler);
        Assert.Contains("hasPhotos=true", query, StringComparison.Ordinal);
        Assert.Contains("hasFiles=true", query, StringComparison.Ordinal);
    }

    // false is a real filter ("entries with NO photos"), so it has to reach the wire. Were it dropped
    // as falsy, the request would silently ask for everything instead.
    [Fact]
    public async Task List_WithFalseAttachmentFilter_SendsItRatherThanDroppingIt()
    {
        var (client, handler) = Create();

        await client.ListAsync(hasPhotos: false);

        Assert.Contains("hasPhotos=false", QueryOf(handler), StringComparison.Ordinal);
    }

    // null is "don't filter" and must leave the key off entirely — the server treats a present key as
    // a filter, so an emitted empty value would narrow a list nobody asked to narrow.
    [Fact]
    public async Task List_WithoutAttachmentFilters_OmitsTheKeys()
    {
        var (client, handler) = Create();

        await client.ListAsync();

        var query = QueryOf(handler);
        Assert.DoesNotContain("hasPhotos", query, StringComparison.Ordinal);
        Assert.DoesNotContain("hasFiles", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_SendsEveryFilterItWasGiven()
    {
        var (client, handler) = Create();
        var tagId = Guid.NewGuid();
        var contactId = Guid.NewGuid();

        await client.ListAsync(
            search: "trip",
            tagIds: [tagId.ToString()],
            contactIds: [contactId.ToString()],
            status: "Active",
            sortBy: "entryDate",
            sortDir: "desc",
            from: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            hasPhotos: true,
            hasFiles: true);

        var query = QueryOf(handler);
        Assert.Contains("search=trip", query, StringComparison.Ordinal);
        Assert.Contains($"tagIds={tagId}", query, StringComparison.Ordinal);
        Assert.Contains($"contactIds={contactId}", query, StringComparison.Ordinal);
        Assert.Contains("status=Active", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=entryDate", query, StringComparison.Ordinal);
        Assert.Contains("sortDir=desc", query, StringComparison.Ordinal);
        Assert.Contains("from=2026-01-01", query, StringComparison.Ordinal);
        Assert.Contains("to=2026-12-31", query, StringComparison.Ordinal);
        Assert.Contains("hasPhotos=true", query, StringComparison.Ordinal);
        Assert.Contains("hasFiles=true", query, StringComparison.Ordinal);
    }

    // The export's whole point is that it takes the SAME filter set as the list. Pinned as one test so
    // a filter added to the list and forgotten on the export shows up here rather than as a download
    // quietly containing rows the page never showed.
    [Fact]
    public async Task Export_SendsTheSameFilterSetAsTheList()
    {
        var (client, handler) = Create();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        var ics = new JournalIcsApiClient(api);
        var tagId = Guid.NewGuid();
        var contactId = Guid.NewGuid();

        await ics.ExportAsync(
            search: "trip",
            tagIds: [tagId.ToString()],
            status: "Active",
            contactIds: [contactId.ToString()],
            from: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            hasPhotos: true,
            hasFiles: true);

        var query = QueryOf(handler);
        Assert.Contains("/api/journal-entries/vjournal", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("search=trip", query, StringComparison.Ordinal);
        Assert.Contains($"tagIds={tagId}", query, StringComparison.Ordinal);
        Assert.Contains($"contactIds={contactId}", query, StringComparison.Ordinal);
        Assert.Contains("status=Active", query, StringComparison.Ordinal);
        Assert.Contains("from=2026-01-01", query, StringComparison.Ordinal);
        Assert.Contains("to=2026-12-31", query, StringComparison.Ordinal);
        Assert.Contains("hasPhotos=true", query, StringComparison.Ordinal);
        Assert.Contains("hasFiles=true", query, StringComparison.Ordinal);
    }
}
