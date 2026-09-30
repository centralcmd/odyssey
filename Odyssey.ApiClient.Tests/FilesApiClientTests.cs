using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// <c>UpdateMetadataAsync</c> returns an <see cref="ApiResult{T}"/> (issue #252): a failed rename used
/// to surface as a bare <c>null</c>, dropping the server's <c>400</c>/<c>409</c> reason on the floor.
/// </summary>
public class FilesApiClientTests
{
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class RecordingHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        }
    }

    private static (IFilesApiClient Client, RecordingHandler Handler) Create(
        string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
    {
        var handler = new RecordingHandler(status, body, mediaType);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new FilesApiClient(api), handler);
    }

    [Fact]
    public async Task UpdateMetadataAsync_PutsTheNameAndDescription_AndReturnsTheStoredMetadata()
    {
        var (client, handler) = Create("""
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "fileName": "deed.pdf",
              "contentType": "application/pdf",
              "sizeBytes": 3,
              "sha256Hash": "abc",
              "uploadedAtUtc": "2026-01-05T00:00:00Z",
              "description": "Signed copy"
            }
            """);

        var result = await client.UpdateMetadataAsync(FileId, "Signed copy", "deed.pdf");

        Assert.True(result.IsSuccess);
        Assert.Equal(FileId, result.Value!.Id);
        Assert.Equal("deed.pdf", result.Value.FileName);
        Assert.Equal("Signed copy", result.Value.Description);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/files/{FileId}/metadata", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"deed.pdf\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"Signed copy\"", handler.LastBody, StringComparison.Ordinal);
    }

    /// <summary>The server's reason survives on the result — the whole point of leaving <c>T?</c>.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "File name is too long.")]
    [InlineData(HttpStatusCode.Forbidden, "You do not have permission to update files.")]
    [InlineData(HttpStatusCode.Conflict, "A file with that name already exists.")]
    public async Task UpdateMetadataAsync_KeepsTheServersReasonOnFailure(HttpStatusCode status, string detail)
    {
        var (client, _) = Create(
            $$"""{"title":"Rejected","status":{{(int)status}},"detail":"{{detail}}"}""",
            status,
            "application/problem+json");

        var result = await client.UpdateMetadataAsync(FileId, null, "deed.pdf");

        Assert.False(result.IsSuccess);
        Assert.Equal(status, result.Status);
        Assert.Equal(detail, result.Problem!.Detail);
        Assert.Null(result.Value);
    }

    /// <summary>An unreachable API is a failure result carrying the transport's reason, never a throw.</summary>
    [Fact]
    public async Task UpdateMetadataAsync_TurnsANetworkFailureIntoAFailureResult()
    {
        var api = new OdysseyApi(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost/") });
        var client = new FilesApiClient(api);

        var result = await client.UpdateMetadataAsync(FileId, null, "deed.pdf");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Contains("the API is unreachable", result.Error, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("the API is unreachable");
    }
}
