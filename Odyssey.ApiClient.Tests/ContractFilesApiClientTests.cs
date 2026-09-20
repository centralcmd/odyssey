using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The two typed-client methods issue #146 adds to the contract-file surface: the contract-scoped
/// <c>PUT</c> and the <c>GET</c> list. AC 19.
/// </summary>
public class ContractFilesApiClientTests
{
    private static readonly Guid ContractId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContactId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    status == HttpStatusCode.BadRequest ? "application/problem+json" : "application/json"),
            };
        }
    }

    private static (IContractsApiClient Client, RecordingHandler Handler) Create(
        string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(body, status);
        var api = new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        return (new ContractsApiClient(api), handler);
    }

    private const string OneFile = """
        [{
          "contractFileId": "44444444-4444-4444-4444-444444444444",
          "contractId": "11111111-1111-1111-1111-111111111111",
          "fileMetadata": {
            "id": "22222222-2222-2222-2222-222222222222",
            "fileName": "agreement.pdf",
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
    public async Task ListFilesAsync_IssuesGetToTheContractScopedRoute()
    {
        var (client, handler) = Create("[]");

        await client.ListFilesAsync(ContractId);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/api/contracts/{ContractId}/files", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListFilesAsync_DeserialisesTheFourNewFields()
    {
        var (client, _) = Create(OneFile);

        var result = await client.ListFilesAsync(ContractId);

        var file = Assert.Single(result.Value!);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), file.ValidFrom);
        Assert.Equal(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), file.ValidTo);
        Assert.Equal(new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc), file.IssuedAt);
        Assert.Equal(ContactId, file.IssuedBy);
        Assert.Equal(ContractFileType.Amendment, file.FileType);
        Assert.Equal("Ada L.", file.AttachedByName);
    }

    [Fact]
    public async Task UpdateFileAsync_IssuesPutToTheContractScopedRoute_WithTheFourFieldsOnTheBody()
    {
        var (client, handler) = Create("{}", HttpStatusCode.NoContent);

        var result = await client.UpdateFileAsync(ContractId, FileId, new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            IssuedAt = new DateTime(2025, 12, 18, 0, 0, 0, DateTimeKind.Utc),
            IssuedBy = ContactId,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/contracts/{ContractId}/files/{FileId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"fileType\":1", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"validFrom\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"validTo\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"issuedAt\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains(ContactId.ToString(), handler.LastBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC 19's second half — a per-field <c>400</c> reaches the caller with its <c>errors</c>
    /// dictionary intact, so the page can render the message on the responsible control rather than
    /// only in a toast.
    /// </summary>
    [Fact]
    public async Task UpdateFileAsync_SurfacesAPerFieldBadRequestWithItsErrorsIntact()
    {
        var (client, _) = Create(
            """
            {
              "title": "Bad Request",
              "status": 400,
              "detail": "ValidTo must be on or after ValidFrom.",
              "errors": { "ValidTo": ["ValidTo must be on or after ValidFrom."] }
            }
            """,
            HttpStatusCode.BadRequest);

        var result = await client.UpdateFileAsync(ContractId, FileId, new UpdateContractFileRequest
        {
            FileType = ContractFileType.Amendment,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.Problem!.Status);
        Assert.Equal("ValidTo must be on or after ValidFrom.", result.Problem.ErrorFor("ValidTo"));
    }
}
