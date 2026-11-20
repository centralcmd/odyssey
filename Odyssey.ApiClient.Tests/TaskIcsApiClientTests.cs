using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Testing.Http;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The task export's three URLs. The summary and the browser-native download must name the SAME filter set as the
/// typed export — the summary is what refuses an export the download would then fail, so a filter dropped from one of
/// them would check a different set from the one downloaded.
/// </summary>
public class TaskIcsApiClientTests
{
    private static (TaskApiClient Client, RecordingHttpHandler Handler) Create()
    {
        var handler = new RecordingHttpHandler("""{"taskCount":3,"attachmentCount":2,"attachmentBytes":4096}""");
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5188/") });
        return (new TaskApiClient(api), handler);
    }

    [Fact]
    public async Task Summary_SendsEveryFilter_AndReadsTheCounts()
    {
        var (client, handler) = Create();

        var result = await client.SummarizeIcsExportAsync("milk", ["t1", "t2"], ["Backlog", "Archived"], ["i1"]);

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/api/tasks/ics/summary", uri.AbsolutePath);
        foreach (var part in new[] { "search=milk", "tagIds=t1", "tagIds=t2", "statuses=Backlog", "statuses=Archived", "ids=i1" })
        {
            Assert.Contains(part, uri.Query, StringComparison.Ordinal);
        }

        Assert.True(result.IsSuccess);
        Assert.Equal((3, 2, 4096L), (result.Value!.TaskCount, result.Value.AttachmentCount, result.Value.AttachmentBytes));
    }

    [Fact]
    public async Task ExportUrl_IsAbsolute_AndCarriesTheSameQueryAsTheSummary()
    {
        var (client, handler) = Create();
        await client.SummarizeIcsExportAsync("milk", ["t1"], ["Done"], ["i1"]);
        var summary = handler.LastRequest!.RequestUri!;

        var export = new Uri(client.ExportIcsUrl("milk", ["t1"], ["Done"], ["i1"]));

        Assert.Equal("http://localhost:5188/api/tasks/ics", export.GetLeftPart(UriPartial.Path));
        Assert.Equal(summary.Query, export.Query);
    }

    [Fact]
    public void ImportUrl_IsTheAbsoluteImportEndpoint()
    {
        var (client, _) = Create();

        Assert.Equal("http://localhost:5188/api/tasks/ics", client.ImportIcsUrl);
    }
}
