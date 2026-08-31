using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Route coverage for the write surfaces added when the read-only clients were completed.
/// </summary>
/// <remarks>
/// The point of these is the <b>parent scoping</b>. Insurance and contract sub-resources (renewals,
/// parties, attachments) are addressed through their parent id — never by their own id alone — which
/// is what keeps the IDOR-free guarantee the scoped downloads already had. A refactor that quietly
/// flattened one of these routes would not otherwise fail anything.
/// </remarks>
public class ScopedWriteRouteTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private static (IOdysseyApi Api, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new OdysseyApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    private static readonly Guid Parent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Child = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Grandchild = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static TheoryData<string, string, string> ScopedRoutes() => new()
    {
        // label                       expected path                                                    method
        { "insurance renewal update",  $"/api/insurance-policies/{Parent}/renewals/{Child}",            "PUT" },
        { "insurance renewal delete",  $"/api/insurance-policies/{Parent}/renewals/{Child}",            "DELETE" },
        { "insurance renewal file",    $"/api/insurance-policies/{Parent}/renewals/{Child}/files/{Grandchild}", "DELETE" },
        { "contract party remove",     $"/api/contracts/{Parent}/parties/{Child}",                      "DELETE" },
        { "contract file detach",      $"/api/contracts/{Parent}/files/{Child}",                        "DELETE" },
        { "tax file attach",           $"/api/tax-statements/{Parent}/files",                           "POST" },
        { "tax file download",         $"/api/tax-statements/{Parent}/files/{Child}",                   "GET" },
        { "tax file detach",           $"/api/tax-statements/{Parent}/files/{Child}",                   "DELETE" },
        { "transaction file list",     $"/api/transactions/{Parent}/files",                             "GET" },
        { "transaction file detach",   $"/api/transactions/{Parent}/files/{Child}",                     "DELETE" },
    };

    [Theory]
    [MemberData(nameof(ScopedRoutes))]
    public async Task Sub_resource_operations_are_addressed_through_their_parent(string label, string expectedPath, string method)
    {
        var (api, handler) = Create();
        var insurance = new InsuranceApiClient(api);
        var contracts = new ContractsApiClient(api);
        var tax = new TaxStatementsApiClient(api);
        var transactions = new TransactionsApiClient(api);

        // Not all of these return ApiResult (downloads and lists return a payload), so each arm is
        // awaited as a bare Task and the assertion is on the recorded request.
        Task call = label switch
        {
            "insurance renewal update" => insurance.UpdateRenewalAsync(Parent, Child, SampleRenewalUpdate()),
            "insurance renewal delete" => insurance.DeleteRenewalAsync(Parent, Child),
            "insurance renewal file" => insurance.DetachRenewalFileAsync(Parent, Child, Grandchild),
            "contract party remove" => contracts.RemovePartyAsync(Parent, Child),
            "contract file detach" => contracts.DetachFileAsync(Parent, Child),
            "tax file attach" => tax.AttachFileAsync(Parent, new AttachTaxStatementFileRequest(Child)),
            "tax file download" => tax.DownloadFileAsync(Parent, Child),
            "tax file detach" => tax.DetachFileAsync(Parent, Child),
            "transaction file list" => transactions.ListFilesAsync(Parent),
            _ => transactions.DetachFileAsync(Parent, Child),
        };
        await call;

        Assert.Equal(expectedPath, handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(method, handler.LastRequest.Method.Method);
    }

    private static UpdatePolicyRenewal SampleRenewalUpdate() => new()
    {
        FromDate = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ToDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Premium = 100m,
        CoverageAmount = 1000m,
    };

    /// <summary>
    /// <c>POST /api/photos</c> answers <c>201</c> with the created photo, and the album-create flow
    /// depends on reading it back — a client that discarded the body would break that silently.
    /// </summary>
    [Fact]
    public async Task Photos_CreateAsync_returns_the_created_photo()
    {
        var handler = new RecordingHandlerWithBody("""
            {"photoId":"44444444-4444-4444-4444-444444444444","fileId":"55555555-5555-5555-5555-555555555555",
             "createdByUserId":"u1","createdAt":"2030-01-01T00:00:00Z","updatedAt":"2030-01-01T00:00:00Z"}
            """, HttpStatusCode.Created);
        var client = new PhotosApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        var result = await client.CreateAsync(new NewPhoto { FileId = Child });

        Assert.True(result.IsSuccess);
        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), result.Value!.PhotoId);
    }

    /// <summary>
    /// A <c>409</c> means "this file is already a library photo". The upload flow treats that as a
    /// benign no-op, so the status has to survive on the result rather than collapsing into a
    /// generic failure.
    /// </summary>
    [Fact]
    public async Task Photos_CreateAsync_keeps_Conflict_distinguishable()
    {
        var handler = new RecordingHandlerWithBody("""{"detail":"Already a photo."}""", HttpStatusCode.Conflict);
        var client = new PhotosApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }));

        var result = await client.CreateAsync(new NewPhoto { FileId = Child });

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
    }

    private sealed class RecordingHandlerWithBody(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
