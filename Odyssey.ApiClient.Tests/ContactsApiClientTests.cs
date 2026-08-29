using System.Net;
using Odyssey.ApiClient.Resources;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.ApiClient.Tests;

public class ContactsApiClientTests
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

    private static NewContact SampleContact() =>
        new()
        {
            Type = ContactType.Organization,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Acme" },
        };

    private static (ContactsApiClient Client, RecordingHandler Handler) Create()
    {
        var handler = new RecordingHandler();
        return (new ContactsApiClient(new OdysseyApi(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") })), handler);
    }

    [Fact]
    public async Task ListAsync_composes_search_types_and_sort()
    {
        var (client, handler) = Create();

        await client.ListAsync(2, 25, search: "acme", types: ["Organization"], sortBy: "displayName", sortDir: "asc");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("offset=25&limit=25", query);
        Assert.Contains("search=acme", query);
        Assert.Contains("types=Organization", query);
        Assert.Contains("sortBy=displayName", query);
        Assert.Contains("sortDir=asc", query);
    }

    // Status is the Active/Archived two-value multiselect: filter only when exactly one is chosen.
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "Active", "Archived" }, false)]
    [InlineData(new[] { "Archived" }, true)]
    public async Task ListAsync_filters_status_only_when_one_is_selected(string[] status, bool expectFilter)
    {
        var (client, handler) = Create();

        await client.ListAsync(1, 25, status: status);

        Assert.Equal(expectFilter, handler.LastRequest!.RequestUri!.Query.Contains("status="));
    }

    // The photos surfaces load people this way; a truncated window would silently drop options.
    [Fact]
    public async Task ListAllAsync_with_a_type_requests_the_full_window()
    {
        var (client, handler) = Create();

        await client.ListAllAsync(types: ["Person"]);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains($"offset=0&limit={PagedQuery.LimitAll}", query);
        Assert.Contains("types=Person", query);
    }

    [Fact]
    public async Task CreateAsync_exposes_the_new_id_from_the_Location_header()
    {
        var (client, handler) = Create();
        var newId = Guid.NewGuid();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Headers = { Location = new Uri($"http://localhost/api/contacts/{newId}") },
        };

        var result = await client.CreateAsync(SampleContact());

        Assert.True(result.IsSuccess);
        Assert.Equal(newId, result.CreatedId);
    }

    /// <summary>
    /// A duplicate name comes back as <c>409</c>; the transaction dialog's quick-create reconciles
    /// against the existing record instead of failing, so the status must survive on the result.
    /// </summary>
    [Fact]
    public async Task CreateAsync_reports_Conflict_distinctly()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{"detail":"A contact with that name already exists."}""",
                                        System.Text.Encoding.UTF8, "application/problem+json"),
        };

        var result = await client.CreateAsync(SampleContact());

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Null(result.CreatedId);
    }

    [Theory]
    [InlineData("addresses")]
    [InlineData("emails")]
    [InlineData("phones")]
    public async Task Contact_method_routes_are_scoped_to_the_parent_contact(string segment)
    {
        var (client, handler) = Create();
        var contactId = Guid.NewGuid();
        var methodId = Guid.NewGuid();
        handler.Response = new HttpResponseMessage(HttpStatusCode.NoContent);

        Task<ApiResult> call = segment switch
        {
            "addresses" => client.DeleteAddressAsync(contactId, methodId),
            "emails" => client.DeleteEmailAsync(contactId, methodId),
            _ => client.DeletePhoneAsync(contactId, methodId),
        };
        await call;

        Assert.Equal($"/api/contacts/{contactId}/{segment}/{methodId}",
                     handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }
}
