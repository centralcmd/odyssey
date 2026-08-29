using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Application;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Route and contract coverage for <see cref="LegalApiClient"/> (issue #354 §7).
/// </summary>
/// <remarks>
/// The one that earns its keep is the "no version published" case: that endpoint answers <c>200</c>
/// with a literal <c>null</c> body, so "nothing published yet" and "the call failed" arrive as
/// success-with-null and failure respectively. Collapsing the two would make a fresh install look
/// broken — or, worse, make a genuine outage look like an empty state.
/// </remarks>
public class LegalApiClientTests
{
    [Fact]
    public async Task CurrentTermsOfService_withNoVersionPublished_isASuccessCarryingNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "null");
        var client = ClientFor(handler);

        var result = await client.GetCurrentTermsOfServiceAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("/api/legal/terms-of-service/current", handler.LastPath);
    }

    [Fact]
    public async Task CurrentTermsOfService_whenTheCallFails_isAFailure()
    {
        var client = ClientFor(new StubHandler(HttpStatusCode.InternalServerError, "{}"));

        var result = await client.GetCurrentTermsOfServiceAsync();

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CurrentTermsOfService_withAPublishedVersion_carriesIt()
    {
        var client = ClientFor(new StubHandler(
            HttpStatusCode.OK,
            """{"id":7,"content":"Terms v7","publishedAt":"2026-05-01T12:00:00Z"}"""));

        var result = await client.GetCurrentTermsOfServiceAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value!.Id);
        Assert.Equal("Terms v7", result.Value.Content);
    }

    [Fact]
    public async Task License_readsTheContentAndDigest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"content":"BSD 2-Clause","sha256":"abc123"}""");
        var client = ClientFor(handler);

        var result = await client.GetLicenseAsync();

        Assert.Equal("/api/legal/license", handler.LastPath);
        Assert.Equal("BSD 2-Clause", result.Value!.Content);
        Assert.Equal("abc123", result.Value.Sha256);
    }

    /// <summary>
    /// The document type has to go over the wire by name — the server binds
    /// <c>"License" | "TermsOfService"</c>, and an ordinal would bind to neither.
    /// </summary>
    [Fact]
    public async Task Respond_sendsTheDocumentTypeByName()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, "");
        var client = ClientFor(handler);

        await client.RespondAsync(new LegalDocumentResponse
        {
            DocumentType = LegalDocumentType.TermsOfService,
            Accepted = true,
            TosVersionId = 4,
        });

        Assert.Equal("/api/legal/respond", handler.LastPath);
        Assert.Contains("\"documentType\":\"TermsOfService\"", handler.LastBody);
        Assert.Contains("\"accepted\":true", handler.LastBody);
        Assert.Contains("\"tosVersionId\":4", handler.LastBody);
    }

    /// <summary>A stale echoed version must surface as a distinguishable 409, not a generic failure.</summary>
    [Fact]
    public async Task Respond_withAStaleVersion_surfacesTheConflictStatus()
    {
        var client = ClientFor(new StubHandler(HttpStatusCode.Conflict, """{"detail":"The Terms of Service changed."}"""));

        var result = await client.RespondAsync(new LegalDocumentResponse
        {
            DocumentType = LegalDocumentType.TermsOfService,
            Accepted = true,
            TosVersionId = 1,
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
    }

    [Fact]
    public async Task Versions_readTheMetadataList()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """
            [{"id":2,"publishedAt":"2026-05-02T09:00:00Z","publishedByUserId":"u1","publishedByDisplayName":"Ada L."},
             {"id":1,"publishedAt":"2026-05-01T09:00:00Z","publishedByUserId":null,"publishedByDisplayName":null}]
            """);
        var client = ClientFor(handler);

        var result = await client.GetVersionsAsync();

        Assert.Equal("/api/legal/terms-of-service/versions", handler.LastPath);
        var versions = result.Value!;
        Assert.Equal([2, 1], versions.Select(version => version.Id));
        // A deleted publisher yields null for both, which the UI renders as "deleted user".
        Assert.Null(versions[1].PublishedByUserId);
        Assert.Null(versions[1].PublishedByDisplayName);
    }

    [Fact]
    public async Task Version_readsOneVersionsFullTextOnDemand()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"id":3,"content":"Terms v3","publishedAt":"2026-05-03T09:00:00Z","publishedByUserId":"u1","publishedByDisplayName":"Ada L."}""");
        var client = ClientFor(handler);

        var result = await client.GetVersionAsync(3);

        Assert.Equal("/api/legal/terms-of-service/versions/3", handler.LastPath);
        Assert.Equal("Terms v3", result.Value!.Content);
    }

    [Fact]
    public async Task PublishVersion_postsTheContentToTheVersionsCollection()
    {
        var handler = new StubHandler(
            HttpStatusCode.Created,
            """{"id":8,"content":"Terms v8","publishedAt":"2026-05-08T09:00:00Z","publishedByUserId":"u1","publishedByDisplayName":"Ada L."}""");
        var client = ClientFor(handler);

        var result = await client.PublishVersionAsync(new NewTermsOfServiceVersion { Content = "Terms v8" });

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/legal/terms-of-service/versions", handler.LastPath);
        Assert.Contains("\"content\":\"Terms v8\"", handler.LastBody);
        Assert.Equal(8, result.Value!.Id);
    }

    private static ILegalApiClient ClientFor(StubHandler handler) =>
        new LegalApiClient(new OdysseyApi(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.odyssey.test/"),
        }));

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string LastPath { get; private set; } = "";
        public string LastBody { get; private set; } = "";
        public HttpMethod LastMethod { get; private set; } = HttpMethod.Get;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastMethod = request.Method;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
