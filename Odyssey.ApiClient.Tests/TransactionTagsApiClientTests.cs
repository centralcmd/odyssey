using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.ApiClient.Tests;

/// <summary>
/// Covers the client behind the Smart Tags picker — the surface where the
/// <c>PagedResult&lt;T&gt;</c>-deserialized-as-<c>List&lt;T&gt;</c> bug actually manifested.
/// </summary>
public class TransactionTagsApiClientTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private static (TransactionTagsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new TransactionTagsApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    /// <summary>
    /// The regression that broke the Smart Tags picker: the endpoint answers with a paged envelope,
    /// and the client must unwrap it into rows rather than trying to read the body as a bare list.
    /// </summary>
    [Fact]
    public async Task ListAllAsync_unwraps_the_paged_envelope_into_rows()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"items":[{"transactionTagId":"11111111-1111-1111-1111-111111111111","name":"Rent","archived":null},
                          {"transactionTagId":"22222222-2222-2222-2222-222222222222","name":"Utilities","archived":null}],
                 "offset":0,"limit":99999,"totalCount":2}
                """, System.Text.Encoding.UTF8, "application/json"),
        };

        var result = await client.ListAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(["Rent", "Utilities"], result.ValueOr([]).Select(t => t.Name));
    }

    [Fact]
    public async Task ListAllAsync_requests_the_full_window()
    {
        var (client, handler) = Create();

        await client.ListAllAsync();

        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task ListAsync_windows_and_composes_filters()
    {
        var (client, handler) = Create();

        await client.ListAsync(3, 25, search: "rent", status: "Archived", sortBy: "name", sortDir: "asc");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("offset=50&limit=25", query);
        Assert.Contains("search=rent", query);
        Assert.Contains("status=Archived", query);
        Assert.Contains("sortBy=name", query);
        Assert.Contains("sortDir=asc", query);
    }

    [Fact]
    public async Task CreateAsync_exposes_the_new_id_from_the_Location_header()
    {
        var (client, handler) = Create();
        var newId = Guid.NewGuid();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Headers = { Location = new Uri($"http://localhost/api/transaction-tags/{newId}") },
        };

        var result = await client.CreateAsync(new NewTransactionTag { Name = "Rent", Archived = false });

        Assert.True(result.IsSuccess);
        Assert.Equal(newId, result.CreatedId);
    }

    [Fact]
    public async Task UpdateAsync_and_DeleteAsync_target_the_id_route()
    {
        var (client, handler) = Create();
        var id = Guid.NewGuid();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);

        await client.UpdateAsync(id, new NewTransactionTag { Name = "Renamed", Archived = false });
        Assert.Equal($"/api/transaction-tags/{id}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);

        await client.DeleteAsync(id);
        Assert.Equal($"/api/transaction-tags/{id}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }
}
