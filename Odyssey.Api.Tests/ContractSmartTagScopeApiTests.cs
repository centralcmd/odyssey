using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The contract smart-tag scope over real HTTP (issue #308): the create copy, the PUT's
/// normalisation, bounds and order rules, full replacement, and the claim gates. The scope is sent as
/// raw JSON so each wire form (bare day, <c>Z</c>, offset) reaches the binder exactly as a client
/// would send it.
/// </summary>
public class ContractSmartTagScopeApiTests
{
    private const string Path = "/api/contracts";

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate, PermissionClaims.ContractsUpdate,
    ];

    // ── Create (AC 1, 2, 10 create copy) ─────────────────────────────────────

    [Fact]
    public async Task Create_copies_the_term_days_into_the_scope()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "\"2021-03-01T09:30:00\"", "\"2026-08-31T00:00:00\"");

        Assert.Equal(Utc(2021, 3, 1), created.SmartTagScopeStart);
        Assert.Equal(Utc(2026, 8, 31), created.SmartTagScopeEnd);
        Assert.Equal(DateTimeKind.Utc, created.SmartTagScopeStart!.Value.Kind);
        // The term itself is stored as entered, time of day included.
        Assert.Equal(new DateTime(2021, 3, 1, 9, 30, 0), created.StartDate);

        var fetched = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{created.ContractId}"))!;
        Assert.Equal(Utc(2021, 3, 1), fetched.SmartTagScopeStart);
        Assert.Equal(Utc(2026, 8, 31), fetched.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Create_without_an_end_date_leaves_the_scope_open_at_the_end()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        Assert.Equal(Utc(2021, 3, 1), created.SmartTagScopeStart);
        Assert.Null(created.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Create_with_an_offset_takes_the_calendar_day_of_the_stored_start()
    {
        // AC 2a: the create copy reads the term AS STORED — the same reading the backfill's DATE()
        // takes — so a created and a backfilled contract with the same stored row agree.
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "\"2021-03-01T00:30:00+02:00\"", "null");

        var stored = await StoredAsync(factory, created.ContractId);
        Assert.Equal(DateTime.SpecifyKind(stored.StartDate!.Value.Date, DateTimeKind.Utc), created.SmartTagScopeStart);
        Assert.Equal(stored.StartDate.Value.Date, stored.SmartTagScopeStart);
    }

    [Fact]
    public async Task A_one_off_contract_is_created_with_an_open_scope()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await PostRawAsync(client, Path,
            """{"name":"Car purchase","type":6,"completionDate":"2026-03-01"}""");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingContract>())!;

        Assert.Null(created.SmartTagScopeStart);
        Assert.Null(created.SmartTagScopeEnd);
    }

    // ── Update (AC 3, 6, 8, 9, 10, 19) ───────────────────────────────────────

    [Theory]
    [InlineData("\"2027-06-30\"", 2027, 6, 30)]
    [InlineData("\"2027-06-30T00:00:00\"", 2027, 6, 30)]
    [InlineData("\"2027-06-30T15:45:00Z\"", 2027, 6, 30)]
    [InlineData("\"2027-06-30T00:30:00+02:00\"", 2027, 6, 29)]
    public async Task A_scope_date_is_stored_and_returned_as_its_utc_calendar_day(
        string wire, int year, int month, int day)
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        var updated = await PutScopeAsync(client, created.ContractId, "null", wire);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadAsStringAsync();
        Assert.Contains($"\"smartTagScopeEnd\":\"{year:D4}-{month:D2}-{day:D2}T00:00:00Z\"", body, StringComparison.Ordinal);
        Assert.Equal(new DateTime(year, month, day), (await StoredAsync(factory, created.ContractId)).SmartTagScopeEnd);
    }

    [Fact]
    public async Task A_term_edit_that_carries_the_scope_forward_leaves_it_unchanged()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        // The motivating case: an end date is added; the scope stays open.
        var response = await PutRawAsync(client, created.ContractId, $$"""
            {"name":"Employment","type":0,"startDate":"2021-03-01","endDate":"2026-08-31",
             "smartTagScopeStart":"2021-03-01T00:00:00Z","smartTagScopeEnd":null}
            """);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(factory, created.ContractId);
        Assert.Equal(new DateTime(2026, 8, 31), stored.EndDate);
        Assert.Equal(new DateTime(2021, 3, 1), stored.SmartTagScopeStart);
        Assert.Null(stored.SmartTagScopeEnd);
    }

    [Fact]
    public async Task Omitting_both_scope_fields_opens_the_scope()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "\"2026-08-31\"");

        var response = await PutRawAsync(client, created.ContractId,
            """{"name":"Employment","type":0,"startDate":"2021-03-01","endDate":"2026-08-31"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(factory, created.ContractId);
        Assert.Null(stored.SmartTagScopeStart);
        Assert.Null(stored.SmartTagScopeEnd);
    }

    [Fact]
    public async Task A_scope_outside_the_term_is_accepted()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "\"2026-08-31\"");

        var response = await PutScopeAsync(client, created.ContractId, "\"2020-01-01\"", "\"2027-06-30\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(factory, created.ContractId);
        Assert.Equal(new DateTime(2020, 1, 1), stored.SmartTagScopeStart);
        Assert.Equal(new DateTime(2027, 6, 30), stored.SmartTagScopeEnd);
    }

    [Fact]
    public async Task An_inverted_scope_is_a_422_keyed_on_the_end_and_nothing_is_written()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "\"2026-08-31\"");

        var response = await PutRawAsync(client, created.ContractId, """
            {"name":"Renamed","type":0,"startDate":"2021-03-01","endDate":"2026-08-31",
             "smartTagScopeStart":"2027-07-01","smartTagScopeEnd":"2027-06-30"}
            """);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("The scope end must not be before its start.", await ErrorsForAsync(response, "smartTagScopeEnd"));
        var stored = await StoredAsync(factory, created.ContractId);
        Assert.Equal("Employment", stored.Name);
        Assert.Equal(new DateTime(2026, 8, 31), stored.SmartTagScopeEnd);
    }

    [Fact]
    public async Task A_same_day_scope_is_valid()
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        var response = await PutScopeAsync(client, created.ContractId, "\"2027-06-30\"", "\"2027-06-30T18:00:00\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("smartTagScopeStart", "\"1899-12-31\"")]
    [InlineData("smartTagScopeEnd", "\"1899-12-31\"")]
    [InlineData("smartTagScopeStart", "\"0001-01-01\"")]
    public async Task A_scope_date_before_1900_is_a_400_keyed_on_the_field(string field, string wire)
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        var response = field == "smartTagScopeStart"
            ? await PutScopeAsync(client, created.ContractId, wire, "null")
            : await PutScopeAsync(client, created.ContractId, "null", wire);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Enter a date between 1 Jan 1900 and 31 Dec 9999.", await ErrorsForAsync(response, field));
    }

    [Theory]
    [InlineData("\"1900-01-01\"", "\"9999-12-31\"")]
    // The ceiling is the last INSTANT of 9999-12-31, so a time of day on the last day truncates to
    // that day rather than being refused.
    [InlineData("null", "\"9999-12-31T15:00:00\"")]
    public async Task The_bounds_themselves_are_accepted(string start, string end)
    {
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        var response = await PutScopeAsync(client, created.ContractId, start, end);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new DateTime(9999, 12, 31), (await StoredAsync(factory, created.ContractId)).SmartTagScopeEnd);
    }

    [Fact]
    public async Task An_offset_value_on_the_floor_that_normalises_below_it_is_a_400()
    {
        // Passes the [Range] as bound (00:00 local on 1900-01-01 is not below the floor) but is
        // 1899-12-31 in UTC — the service re-checks after normalisation.
        await using var factory = new OdysseyApiFactory(ReadWrite);
        using var client = factory.CreateClient();
        var created = await CreateAsync(client, "\"2021-03-01\"", "null");

        var response = await PutScopeAsync(client, created.ContractId, "\"1900-01-01T00:30:00+05:00\"", "null");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(await ErrorsForAsync(response, "smartTagScopeStart"));
    }

    // ── Authorization (AC 11) and the list (AC 18) ───────────────────────────

    [Fact]
    public async Task The_scope_cannot_be_written_without_contracts_update()
    {
        // The action-level policy runs before the action, so the contract need not exist.
        await using var factory = new OdysseyApiFactory([PermissionClaims.ContractsRead]);
        using var client = factory.CreateClient();

        var response = await PutScopeAsync(client, Guid.NewGuid(), "\"2021-03-01\"", "null");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void The_list_item_carries_no_scope()
    {
        var names = typeof(ContractListItem).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.StartsWith("SmartTagScope", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<ExistingContract> CreateAsync(HttpClient client, string startJson, string endJson)
    {
        var post = await PostRawAsync(client, Path,
            $$"""{"name":"Employment","type":0,"startDate":{{startJson}},"endDate":{{endJson}}}""");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!;
    }

    private static Task<HttpResponseMessage> PutScopeAsync(HttpClient client, Guid id, string startJson, string endJson) =>
        PutRawAsync(client, id, $$"""
            {"name":"Employment","type":0,"startDate":"2021-03-01",
             "smartTagScopeStart":{{startJson}},"smartTagScopeEnd":{{endJson}}}
            """);

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PutRawAsync(HttpClient client, Guid id, string json) =>
        client.PutAsync($"{Path}/{id}", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<List<string>> ErrorsForAsync(HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var key = document.RootElement.GetProperty("errors").EnumerateObject()
            .Single(property => property.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        return key.Value.EnumerateArray().Select(message => message.GetString()!).ToList();
    }

    private static async Task<Contract> StoredAsync(OdysseyApiFactory factory, Guid contractId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Contracts.AsNoTracking().SingleAsync(c => c.ContractId == contractId);
    }
}
