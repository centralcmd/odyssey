using System.Net;
using System.Text;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// The four alias methods (issue #48, AC 48). They return <c>ApiResult</c>/<c>ApiResult&lt;T&gt;</c>
/// and <b>never toast</b> — deciding that a failure becomes a snackbar is the UI's job, made at the
/// page call site, and for this family it deliberately does not: a 400/409/422 belongs inline on the
/// dialog's value field, not in a snackbar alongside it.
/// </summary>
public class ContactAliasApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.NoContent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return Response;
        }
    }

    private static (ContactsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new ContactsApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    private static readonly Guid ContactId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AliasId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ListAliasesAsync_gets_the_collection_and_deserialises_it()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""[{"id":"{{AliasId}}","contactId":"{{ContactId}}","value":"Kari","label":"nickname"}]""",
                Encoding.UTF8, "application/json"),
        };

        var result = await client.ListAliasesAsync(ContactId);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/api/contacts/{ContactId}/aliases", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.True(result.IsSuccess);
        var alias = Assert.Single(result.Value!);
        Assert.Equal("Kari", alias.Value);
        Assert.Equal("nickname", alias.Label);
        Assert.Equal(ContactId, alias.ContactId);
    }

    [Fact]
    public async Task AddAliasAsync_posts_the_two_scalars_to_the_collection()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Created);

        var result = await client.AddAliasAsync(ContactId, new NewContactAlias { Value = "Kari", Label = "nickname" });

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/contacts/{ContactId}/aliases", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"value\":\"Kari\"", handler.LastBody);
        Assert.Contains("\"label\":\"nickname\"", handler.LastBody);
        // No id and no contactId on the wire: the parent comes from the route and the id from the
        // database, so there is no over-posting path to re-parent an alias.
        Assert.DoesNotContain("\"id\"", handler.LastBody);
        Assert.DoesNotContain("\"contactId\"", handler.LastBody);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task UpdateAliasAsync_puts_to_the_item_route()
    {
        var (client, handler) = Create();

        var result = await client.UpdateAliasAsync(ContactId, AliasId, new NewContactAlias { Value = "Kari" });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal($"/api/contacts/{ContactId}/aliases/{AliasId}", handler.LastRequest.RequestUri!.AbsolutePath);
        // A null label is SENT, not omitted: the PUT is a replace, so it is what clears a stored one.
        Assert.Contains("\"label\":null", handler.LastBody);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DeleteAliasAsync_deletes_the_item_route_with_no_body()
    {
        var (client, handler) = Create();

        var result = await client.DeleteAliasAsync(ContactId, AliasId);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal($"/api/contacts/{ContactId}/aliases/{AliasId}", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Null(handler.LastRequest.Content);
        Assert.True(result.IsSuccess);
    }

    // A duplicate comes back as a plain ApiResult carrying the status and the problem's per-field
    // entry. The library reports; it does not present — the dialog is what renders this on its value
    // field, and there is no snackbar in this layer to suppress.
    [Fact]
    public async Task AddAliasAsync_conflict_surfaces_the_status_and_the_field_error()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"title":"Conflict","status":409,"errors":{"value":["This contact already has that alias."]}}""",
                Encoding.UTF8, "application/problem+json"),
        };

        var result = await client.AddAliasAsync(ContactId, new NewContactAlias { Value = "Kari" });

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal("This contact already has that alias.", result.Problem!.ErrorFor("value"));
    }

    [Fact]
    public async Task AddAliasAsync_capExceeded_surfaces_422_and_the_field_error()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                """{"title":"Unprocessable","status":422,"errors":{"value":["A contact can have at most 32 aliases."]}}""",
                Encoding.UTF8, "application/problem+json"),
        };

        var result = await client.AddAliasAsync(ContactId, new NewContactAlias { Value = "Kari" });

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.Status);
        Assert.Contains("32", result.Problem!.ErrorFor("value"));
    }
}
