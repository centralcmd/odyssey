using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The reader half of the export's completeness contract (issue #401). The endpoint streams its JSON,
/// so a failure past the first byte arrives as a <c>200</c> with a truncated body rather than a
/// ProblemDetails — the client must decide from the payload, not the status, whether it holds a whole
/// database export.
/// </summary>
public class DataExportApiClientTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    private static DataExportApiClient Create(HttpResponseMessage response)
    {
        var http = new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("http://localhost/") };
        return new DataExportApiClient(new OdysseyApi(http));
    }

    private static HttpResponseMessage Attachment(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = "\"odyssey-database-export-20260806.json\"",
        };
        return response;
    }

    // A whole document, indented the way the server's writer emits it.
    private const string CompleteExport = """
        {
          "schemaVersion": 1,
          "databases": {
            "finance": {
              "accounts": []
            }
          },
          "complete": true
        }
        """;

    // The same export cut off mid-row, which is what a mid-stream failure leaves behind.
    private const string TruncatedExport = """
        {
          "schemaVersion": 1,
          "databases": {
            "finance": {
              "accounts": [
                {
                  "accountId": "8a3f
        """;

    // ── DownloadAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_ReturnsTheFile_WhenTheDocumentCarriesTheSentinel()
    {
        var client = Create(Attachment(CompleteExport));

        var result = await client.DownloadAsync();

        Assert.Equal(DataExportOutcome.Success, result.Outcome);
        Assert.NotNull(result.File);
        Assert.Equal("odyssey-database-export-20260806.json", result.File.FileName);
    }

    /// <summary>
    /// A truncated body is a partial database, so no file comes back at all — saving it would put an
    /// export in the downloads folder that is indistinguishable from a whole one.
    /// </summary>
    [Fact]
    public async Task DownloadAsync_ReportsIncompleteAndWithholdsTheFile_WhenTheSentinelIsMissing()
    {
        var client = Create(Attachment(TruncatedExport));

        var result = await client.DownloadAsync();

        Assert.Equal(DataExportOutcome.Incomplete, result.Outcome);
        Assert.Null(result.File);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, DataExportOutcome.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, DataExportOutcome.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, DataExportOutcome.Failed)]
    public async Task DownloadAsync_MapsTheStatus_WhenTheServerRefusesBeforeTheBody(
        HttpStatusCode status, DataExportOutcome expected)
    {
        var client = Create(new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"title":"nope"}""", Encoding.UTF8, "application/problem+json"),
        });

        var result = await client.DownloadAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.File);
    }

    // ── The tail scan itself ──────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"complete": true}""")]
    [InlineData("""{"complete":true}""")]
    [InlineData("{\"complete\" : true\r\n}\r\n")]
    [InlineData("{\n  \"complete\": true\n}\n")]
    public void HasCompletenessSentinel_AcceptsTheSentinel_RegardlessOfWhitespace(string payload) =>
        Assert.True(DataExportApiClient.HasCompletenessSentinel(Encoding.UTF8.GetBytes(payload)));

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"complete": false}""")]
    // Present but not terminal: the document was cut off after the sentinel's own bytes.
    [InlineData("""{"complete": true""")]
    // A row that merely mentions the word is not the sentinel.
    [InlineData("""{"accounts":[{"name":"complete"}]}""")]
    // Truncated at a point that still happens to parse as JSON — the case the sentinel exists for.
    [InlineData("""{"schemaVersion":1,"databases":{"finance":{"accounts":[]}}}""")]
    public void HasCompletenessSentinel_RejectsAnythingElse(string payload) =>
        Assert.False(DataExportApiClient.HasCompletenessSentinel(Encoding.UTF8.GetBytes(payload)));
}
