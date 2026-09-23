using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using ContractType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The contract reference number over real HTTP (issue #181): the two write paths' validation and
/// normalisation, the two read shapes, the widened search and the new sort key.
/// </summary>
public class ContractReferenceNumberApiTests
{
    private const string ActorUserId = "contract-reference-actor-id";
    private const string Path = "/api/contracts";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    // ── Round-trip (AC 1, 2, 5, 6, 7) ─────────────────────────────────────────

    [Fact]
    public async Task A_reference_number_round_trips_through_create_and_get()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "AGR-2026/114-B.2");
        Assert.Equal("AGR-2026/114-B.2", created.ReferenceNumber);

        var fetched = await client.GetFromJsonAsync<ExistingContract>($"{Path}/{created.ContractId}");
        Assert.Equal("AGR-2026/114-B.2", fetched!.ReferenceNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_or_null_reference_number_stores_null(string? value)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, value);

        Assert.Null(created.ReferenceNumber);
        Assert.Null(await StoredAsync(factory, created.ContractId));
    }

    [Fact]
    public async Task An_omitted_reference_number_stores_null()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new { name = "No number", type = ContractType.Other });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<ExistingContract>())!;

        Assert.Null(created.ReferenceNumber);
        Assert.Null(await StoredAsync(factory, created.ContractId));
    }

    [Theory]
    [InlineData("Ω-2026.№114 (rev/2)")]
    [InlineData("AVT-åæø-01")]
    // Non-BMP (a CJK Extension B ideograph, U+20BB7). The defective \p{C} pattern rejects this, and
    // every BMP case above passes under both patterns — so only this row tells them apart.
    [InlineData("REF-\U00020BB7-1")]
    [InlineData("😀-2026")]
    public async Task Printable_values_are_accepted_and_round_trip_verbatim(string value)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, value);
        Assert.Equal(value, created.ReferenceNumber);

        var put = await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update(value + "b"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(value + "b", (await put.Content.ReadFromJsonAsync<ExistingContract>())!.ReferenceNumber);
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed_on_both_write_paths()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "  REF-1  ");
        Assert.Equal("REF-1", created.ReferenceNumber);
        Assert.Equal("REF-1", await StoredAsync(factory, created.ContractId));

        var put = await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update("\tREF-2 "));
        // A tab is a control character, so it is refused before the trim could remove it — the
        // character rule runs on the value as sent.
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        put = await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update("  REF 2 / 14  "));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        // Interior spacing is preserved verbatim: the value has to match the paperwork.
        Assert.Equal("REF 2 / 14", (await put.Content.ReadFromJsonAsync<ExistingContract>())!.ReferenceNumber);
        Assert.Equal("REF 2 / 14", await StoredAsync(factory, created.ContractId));
    }

    [Fact]
    public async Task A_put_with_null_or_an_omitted_reference_number_clears_it()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "REF-1");
        var put = await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update(null));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await put.Content.ReadFromJsonAsync<ExistingContract>())!.ReferenceNumber);

        var second = await CreateAsync(client, "REF-2");
        put = await client.PutAsJsonAsync($"{Path}/{second.ContractId}", new
        {
            name = second.Name,
            type = second.Type,
            startDate = second.StartDate,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null(await StoredAsync(factory, second.ContractId));
    }

    // ── Validation (AC 3, 4, 9, 20) ──────────────────────────────────────────

    [Fact]
    public async Task Sixty_four_characters_are_accepted_and_sixty_five_are_refused_without_echo()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var atLimit = new string('A', ContractReferenceNumber.MaxLength);
        var created = await CreateAsync(client, atLimit);
        Assert.Equal(atLimit, created.ReferenceNumber);

        var overLimit = "Z" + new string('Q', ContractReferenceNumber.MaxLength);
        var post = await client.PostAsJsonAsync(Path, New(overLimit));
        await AssertRefusedAsync(post, overLimit, expectMessageContaining: "64");

        var put = await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update(overLimit));
        await AssertRefusedAsync(put, overLimit, expectMessageContaining: "64");
        Assert.Equal(atLimit, await StoredAsync(factory, created.ContractId));
    }

    [Theory]
    [InlineData("REF\n1")]
    [InlineData("REF\r1")]
    [InlineData("REF\t1")]
    [InlineData("REF\u00001")]
    [InlineData("REF\u202E1")]
    [InlineData("REF-1\n")]
    public async Task Control_and_format_characters_are_refused_on_both_write_paths(string value)
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, New(value));
        await AssertRefusedAsync(post, value, expectMessageContaining: null);
        Assert.Empty(await client.GetPagedItemsAsync<ContractListItem>(Path));

        var created = await CreateAsync(client, "REF-KEEP");
        var put = await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update(value));
        await AssertRefusedAsync(put, value, expectMessageContaining: null);
        Assert.Equal("REF-KEEP", await StoredAsync(factory, created.ContractId));
    }

    [Fact]
    public async Task An_object_shaped_reference_number_is_refused_and_creates_nothing()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync(Path, new
        {
            name = "Object-shaped",
            type = ContractType.Other,
            referenceNumber = new { contractId = Guid.NewGuid() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Empty(await client.GetPagedItemsAsync<ContractListItem>(Path));
    }

    [Fact]
    public async Task Two_contracts_may_share_a_reference_number()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var first = await CreateAsync(client, "1001");
        var second = await CreateAsync(client, "1001");

        Assert.Equal("1001", await StoredAsync(factory, first.ContractId));
        Assert.Equal("1001", await StoredAsync(factory, second.ContractId));
    }

    [Fact]
    public async Task No_log_line_carries_a_submitted_reference_number()
    {
        const string Accepted = "LOGPROBE-ACCEPTED-7731";
        const string Refused = "LOGPROBE-REFUSED\n-7731";

        var logs = new CapturingLoggerProvider();
        await using var factory = new ApiFactory(ReadWrite, services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, Accepted);
        await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update(Accepted + "-2"));
        await client.PostAsJsonAsync(Path, New(Refused));
        await client.PutAsJsonAsync($"{Path}/{created.ContractId}", Update(Refused));
        await client.GetAsync($"{Path}?search=LOGPROBE");

        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.Message.Contains("LOGPROBE", StringComparison.Ordinal)
            || (entry.Exception?.ToString().Contains("LOGPROBE", StringComparison.Ordinal) ?? false));
    }

    // ── List read: projection, search, sort (AC 8, 12–15) ────────────────────

    [Fact]
    public async Task The_list_row_carries_the_reference_number()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "CUST-5508217", name: "With number");
        await CreateAsync(client, null, name: "Without number");

        var items = (await client.GetPagedItemsAsync<ContractListItem>(Path))!;

        Assert.Equal("CUST-5508217", Assert.Single(items, i => i.Name == "With number").ReferenceNumber);
        Assert.Null(Assert.Single(items, i => i.Name == "Without number").ReferenceNumber);
    }

    [Fact]
    public async Task Search_matches_a_contract_whose_only_match_is_its_reference_number()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var target = await CreateAsync(client, "AGR-2026/114-B.2", name: "Lease", description: "Flat");
        await CreateAsync(client, "OTHER-9", name: "Gym", description: "Membership");

        var hits = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?search=114-B"))!;

        Assert.Equal(target.ContractId, Assert.Single(hits).ContractId);
    }

    [Fact]
    public async Task Sorting_by_reference_number_keeps_nulls_last_in_both_directions()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        await CreateAsync(client, "B-2", name: "b");
        await CreateAsync(client, null, name: "none");
        await CreateAsync(client, "A-1", name: "a");
        await CreateAsync(client, "C-3", name: "c");

        var asc = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?sortBy=referenceNumber&sortDir=asc"))!;
        Assert.Equal(["A-1", "B-2", "C-3", null], asc.Select(i => i.ReferenceNumber));

        var desc = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?sortBy=referenceNumber&sortDir=desc"))!;
        Assert.Equal(["C-3", "B-2", "A-1", null], desc.Select(i => i.ReferenceNumber));

        // Natural default direction is ascending, joining Name, Type and Status.
        var natural = (await client.GetPagedItemsAsync<ContractListItem>($"{Path}?sortBy=referenceNumber"))!;
        Assert.Equal(asc.Select(i => i.ContractId), natural.Select(i => i.ContractId));
    }

    [Fact]
    public async Task An_unknown_sort_key_is_still_refused()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?sortBy=notAKey");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Delete (AC 18) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_the_contract_removes_its_reference_number()
    {
        await using var factory = new ApiFactory(ReadWrite);
        using var client = factory.CreateClient();

        var created = await CreateAsync(client, "GONE-1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{created.ContractId}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        Assert.False(await context.Contracts.AnyAsync(c => c.ReferenceNumber == "GONE-1"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string submitted, string? expectMessageContaining)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var errors = document.RootElement.GetProperty("errors");
        var key = errors.EnumerateObject()
            .Single(property => property.Name.Equals("ReferenceNumber", StringComparison.OrdinalIgnoreCase));
        if (expectMessageContaining is not null)
        {
            Assert.Contains(key.Value.EnumerateArray(),
                message => message.GetString()!.Contains(expectMessageContaining, StringComparison.Ordinal));
        }

        // The body names the field and the constraint, never the value. Checked against the raw
        // text AND the JSON-escaped form, so an escaped control character cannot slip past.
        Assert.DoesNotContain(submitted, body, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(submitted).Trim('"'), body, StringComparison.Ordinal);
    }

    private static async Task<string?> StoredAsync(OdysseyApiFactory factory, Guid contractId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return (await context.Contracts.AsNoTracking().SingleAsync(c => c.ContractId == contractId)).ReferenceNumber;
    }

    private static async Task<ExistingContract> CreateAsync(
        HttpClient client, string? referenceNumber, string name = "Lease agreement", string? description = null)
    {
        var post = await client.PostAsJsonAsync(Path, New(referenceNumber, name, description));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        return (await post.Content.ReadFromJsonAsync<ExistingContract>())!;
    }

    private static NewContract New(string? referenceNumber, string name = "Lease agreement", string? description = null) => new()
    {
        Name = name,
        Type = ContractType.Rental,
        Description = description,
        ReferenceNumber = referenceNumber,
        StartDate = FixedToday.AddMonths(-1),
    };

    private static UpdateContract Update(string? referenceNumber) => new()
    {
        Name = "Lease agreement",
        Type = ContractType.Rental,
        ReferenceNumber = referenceNumber,
        StartDate = FixedToday.AddMonths(-1),
    };

    private sealed class ApiFactory(
        IReadOnlyCollection<string>? permissions,
        Action<IServiceCollection>? extra = null)
        : OdysseyApiFactory(permissions, ActorUserId, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
            extra?.Invoke(services);
        });

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
