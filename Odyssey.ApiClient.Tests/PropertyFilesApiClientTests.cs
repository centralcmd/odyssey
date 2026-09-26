using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The five typed-client methods issue #210 adds to the property surface: list, attach, update,
/// download and detach — each addressed through its property id, never by the file id alone.
/// </summary>
public class PropertyFilesApiClientTests
{
    private static readonly Guid PropertyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContactId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class RecordingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond();
        }
    }

    private static (IPropertiesApiClient Client, RecordingHandler Handler) Create(
        string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        Create(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        });

    private static (IPropertiesApiClient Client, RecordingHandler Handler) Create(Func<HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new PropertiesApiClient(api), handler);
    }

    private const string OneFile = """
        [{
          "propertyFileId": "44444444-4444-4444-4444-444444444444",
          "propertyId": "11111111-1111-1111-1111-111111111111",
          "fileMetadata": {
            "id": "22222222-2222-2222-2222-222222222222",
            "fileName": "deed.pdf",
            "contentType": "application/pdf",
            "sizeBytes": 3,
            "fileBlobId": "55555555-5555-5555-5555-555555555555",
            "uploadedAtUtc": "2026-01-05T00:00:00Z"
          },
          "fileType": 1,
          "attachedByUserId": "user-1",
          "attachedByName": "Ada L.",
          "attachedAtUtc": "2026-01-05T00:00:00Z",
          "validFrom": "2026-01-01T00:00:00Z",
          "validTo": "2026-12-31T00:00:00Z",
          "issuedAt": "2025-12-18T00:00:00Z",
          "issuedBy": "33333333-3333-3333-3333-333333333333"
        }]
        """;

    [Fact]
    public async Task ListFilesAsync_IssuesGetToThePropertyScopedRoute()
    {
        var (client, handler) = Create("[]");

        var result = await client.ListFilesAsync(PropertyId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/files", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListFilesAsync_DeserialisesEveryField()
    {
        var (client, _) = Create(OneFile);

        var result = await client.ListFilesAsync(PropertyId);

        var file = Assert.Single(result.Value!);
        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), file.PropertyFileId);
        Assert.Equal(PropertyId, file.PropertyId);
        Assert.Equal(FileId, file.FileMetadata.Id);
        Assert.Equal("deed.pdf", file.FileMetadata.FileName);
        Assert.Equal(PropertyFileType.Deed, file.FileType);
        Assert.Equal("user-1", file.AttachedByUserId);
        Assert.Equal("Ada L.", file.AttachedByName);
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), file.AttachedAtUtc);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), file.ValidFrom);
        Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), file.ValidTo);
        Assert.Equal(new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc), file.IssuedAt);
        Assert.Equal(ContactId, file.IssuedBy);
    }

    [Fact]
    public async Task ListFilesAsync_SurfacesANotFound()
    {
        var (client, _) = Create("""{"title":"Not Found","status":404}""", HttpStatusCode.NotFound, "application/problem+json");

        var result = await client.ListFilesAsync(PropertyId);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, result.Status);
    }

    [Fact]
    public async Task AttachFileAsync_IssuesPostToThePropertyScopedRoute_WithTheWholeBody()
    {
        var (client, handler) = Create("{}", HttpStatusCode.Created);

        var result = await client.AttachFileAsync(PropertyId, new AttachPropertyFileRequest
        {
            FileMetadataId = FileId,
            FileType = PropertyFileType.Valuation,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            IssuedAt = new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc),
            IssuedBy = ContactId,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/files", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains(FileId.ToString(), handler.LastBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"fileType\":3", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"validFrom\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"validTo\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"issuedAt\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(ContactId.ToString(), handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A duplicate attach is a <c>409</c>, and the status survives on the result.</summary>
    [Fact]
    public async Task AttachFileAsync_KeepsAConflictDistinguishable()
    {
        var (client, _) = Create(
            """{"title":"Conflict","status":409,"detail":"Already attached."}""",
            HttpStatusCode.Conflict,
            "application/problem+json");

        var result = await client.AttachFileAsync(PropertyId, new AttachPropertyFileRequest { FileMetadataId = FileId });

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("Already attached.", result.Problem!.Detail);
    }

    [Fact]
    public async Task UpdateFileAsync_IssuesPutToThePropertyScopedRoute_WithTheFieldsOnTheBody()
    {
        var (client, handler) = Create("", HttpStatusCode.NoContent);

        var result = await client.UpdateFileAsync(PropertyId, FileId, new UpdatePropertyFileRequest
        {
            FileType = PropertyFileType.Warranty,
            ValidTo = new DateTime(2027, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            IssuedBy = ContactId,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/files/{FileId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"fileType\":7", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"validTo\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(ContactId.ToString(), handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A per-field <c>400</c> reaches the caller with its <c>errors</c> dictionary intact.</summary>
    [Fact]
    public async Task UpdateFileAsync_SurfacesAPerFieldBadRequestWithItsErrorsIntact()
    {
        var (client, _) = Create(
            """
            {
              "title": "Bad Request",
              "status": 400,
              "detail": "Contact was not found.",
              "errors": { "IssuedBy": ["Contact was not found."] }
            }
            """,
            HttpStatusCode.BadRequest,
            "application/problem+json");

        var result = await client.UpdateFileAsync(PropertyId, FileId, new UpdatePropertyFileRequest
        {
            FileType = PropertyFileType.Other,
            IssuedBy = ContactId,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.Problem!.Status);
        Assert.Equal("Contact was not found.", result.Problem.ErrorFor("IssuedBy"));
    }

    [Fact]
    public async Task DownloadFileAsync_IssuesGetToThePropertyScopedRoute_AndReadsTheFile()
    {
        var (client, handler) = Create(() =>
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "deed.pdf" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await client.DownloadFileAsync(PropertyId, FileId);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/files/{FileId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal([1, 2, 3], result.Value!.Bytes);
        Assert.Equal("deed.pdf", result.Value.FileName);
        Assert.Equal("application/pdf", result.Value.ContentType);
    }

    [Fact]
    public async Task DownloadFileAsync_FallsBackToADefaultName_WithoutAContentDisposition()
    {
        var (client, _) = Create(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9]),
        });

        var result = await client.DownloadFileAsync(PropertyId, FileId);

        Assert.True(result.IsSuccess);
        Assert.Equal("property-file", result.Value!.FileName);
    }

    [Fact]
    public async Task DownloadFileAsync_SurfacesANotFound()
    {
        var (client, _) = Create("""{"title":"Not Found","status":404}""", HttpStatusCode.NotFound, "application/problem+json");

        var result = await client.DownloadFileAsync(PropertyId, FileId);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task DetachFileAsync_IssuesDeleteToThePropertyScopedRoute_WithNoBody()
    {
        var (client, handler) = Create("", HttpStatusCode.NoContent);

        var result = await client.DetachFileAsync(PropertyId, FileId);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/api/properties/{PropertyId}/files/{FileId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Null(handler.LastRequest.Content);
    }
}
