using System.Net;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The journal export's URLs. The summary and the browser-native download must name the SAME filter set — the
/// summary is what refuses an export the download would then fail, so a filter dropped from one of them would check a
/// different set from the one downloaded.
/// </summary>
public class JournalIcsApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"entryCount":3,"attachmentCount":2,"photoCount":4,"attachmentBytes":4096}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (JournalIcsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5188/") });
        return (new JournalIcsApiClient(api), handler);
    }

    private static readonly DateTime From = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Summary_SendsEveryFilter_AndReadsTheCounts()
    {
        var (client, handler) = Create();

        var result = await client.SummarizeExportAsync("bergen", ["t1", "t2"], "Archived", ["c1"], From, To, true, false);

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/api/journal-entries/vjournal/summary", uri.AbsolutePath);
        foreach (var part in new[] { "search=bergen", "tagIds=t1", "tagIds=t2", "status=Archived", "contactIds=c1", "from=", "to=", "hasPhotos=true", "hasFiles=false" })
        {
            Assert.Contains(part, uri.Query, StringComparison.Ordinal);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal((3, 2, 4, 4096L),
            (result.Value!.EntryCount, result.Value.AttachmentCount, result.Value.PhotoCount, result.Value.AttachmentBytes));
    }

    [Fact]
    public async Task ExportUrl_IsAbsolute_AndCarriesTheSameQueryAsTheSummary()
    {
        var (client, handler) = Create();
        await client.SummarizeExportAsync("bergen", ["t1"], "Active", ["c1"], From, To, false, true);
        var summary = handler.LastRequest!.RequestUri!;

        var export = new Uri(client.ExportUrl("bergen", ["t1"], "Active", ["c1"], From, To, false, true));

        Assert.Equal("http://localhost:5188/api/journal-entries/vjournal", export.GetLeftPart(UriPartial.Path));
        Assert.Equal(summary.Query, export.Query);
    }

    [Fact]
    public async Task SingleEntry_SummaryAndExportUrl_NameTheSameEntry()
    {
        var (client, handler) = Create();
        var id = Guid.NewGuid();

        await client.SummarizeExportOneAsync(id);

        Assert.Equal($"/api/journal-entries/{id}/vjournal/summary", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal($"http://localhost:5188/api/journal-entries/{id}/vjournal", client.ExportOneUrl(id));
    }

    [Fact]
    public void ImportUrl_IsTheAbsoluteImportEndpoint()
    {
        var (client, _) = Create();

        Assert.Equal("http://localhost:5188/api/journal-entries/vjournal", client.ImportUrl);
    }
}
