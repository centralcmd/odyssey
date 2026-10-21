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

        /// <summary>The request body, read before the client disposes the content.</summary>
        public string? LastBody { get; private set; }

        public HttpResponseMessage Response { get; set; } =
            new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[],"offset":0,"limit":50,"totalCount":0}""",
                                            System.Text.Encoding.UTF8, "application/json"),
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return Response;
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

    // ── Type conversion (issue #335) ─────────────────────────────────────────────

    [Fact]
    public async Task PreviewConversionAsync_gets_the_preview_with_target_and_carry_flag()
    {
        var (client, handler) = Create();
        handler.Response = Json("""{"currentType":1,"targetType":2,"updatedAt":"2026-10-21T09:12:44.1234567Z","canConvert":true}""");
        var id = Guid.NewGuid();

        var result = await client.PreviewConversionAsync(id, ContactType.Organization, carryLifecycleDates: false);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/api/contacts/{id}/conversion-preview", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("targetType=Organization", handler.LastRequest.RequestUri.Query);
        Assert.Contains("carryLifecycleDates=false", handler.LastRequest.RequestUri.Query);
        Assert.Equal(new DateTime(2026, 10, 21, 9, 12, 44, DateTimeKind.Utc).AddTicks(1_234_567), result.Value!.UpdatedAt);
    }

    [Fact]
    public async Task ConvertAsync_posts_the_request_body()
    {
        var (client, handler) = Create();
        handler.Response = Json("""{"contactId":"00000000-0000-0000-0000-000000000001","externalUid":"u","normalizedName":"N","resolvedDisplayName":"N","type":2}""");
        var id = Guid.NewGuid();

        await client.ConvertAsync(id, new ContactConversionRequest
        {
            TargetType = ContactType.Organization,
            ExpectedUpdatedAt = new DateTime(2026, 10, 21, 9, 12, 44, DateTimeKind.Utc),
            LegalName = "Acme",
        });

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"/api/contacts/{id}/conversion", handler.LastRequest.RequestUri!.AbsolutePath);
        var body = handler.LastBody!;
        Assert.Contains("\"legalName\":\"Acme\"", body);
        Assert.Contains("\"expectedUpdatedAt\"", body);
    }

    // The dialog decides what a 409 means, so the raw code and extensions must survive the client.
    [Fact]
    public async Task ConvertAsync_passes_a_blocked_409_through_with_code_and_extensions()
    {
        var (client, handler) = Create();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"status":409,"code":"contact-type-change-blocked","photoPersonTags":{"count":2,"photoIds":[],"truncated":false}}""",
                System.Text.Encoding.UTF8, "application/problem+json"),
        };

        var result = await client.ConvertAsync(Guid.NewGuid(), new ContactConversionRequest
        {
            TargetType = ContactType.Organization,
            ExpectedUpdatedAt = DateTime.UtcNow,
        });

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal(ContactConversionCodes.Blocked, result.Problem!.Code);
        Assert.Equal(2, result.Problem.Extension<ContactPhotoTagBlocker>("photoPersonTags")!.Count);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
