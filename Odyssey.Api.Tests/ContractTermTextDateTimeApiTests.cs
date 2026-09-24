using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;
// Both halves of the aligned pair are in scope here (Odyssey.Context for the stored rows,
// Odyssey.Dtos.Finance for the wire), so each wire type is named explicitly.
using ContractType = Odyssey.Dtos.Finance.ContractType;
using ContractEventType = Odyssey.Dtos.Finance.ContractEventType;
using TermValueUnit = Odyssey.Dtos.Finance.TermValueUnit;
using TermDirection = Odyssey.Dtos.Finance.TermDirection;

namespace Odyssey.Api.Tests;

/// <summary>
/// The two non-numeric term kinds (issue #192) — <c>Text</c> and <c>DateTime</c> — over real HTTP
/// against the shared <c>OdysseyApiFactory</c> fixture. Bodies are sent as raw JSON with the enums as
/// ORDINALS, because that is how they cross the wire, and so a date-time's offset reaches the binder
/// exactly as a client wrote it.
/// </summary>
public class ContractTermTextDateTimeApiTests
{
    private const string ActorUserId = "contract-term-text-actor-id";
    private const string Path = "/api/contracts";
    private const string NoticeText = "3 months, to the end of a month";

    private static readonly DateTime FixedToday = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ReadWrite =
    [
        PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate,
        PermissionClaims.ContractsUpdate, PermissionClaims.ContractsDelete,
    ];

    // ── AC 1–3: create and read both kinds ────────────────────────────────────

    [Fact]
    public async Task Post_Text_Returns201_WithTheTextTrimmedAndTheOtherValuesNull()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), TextBody($"   {NoticeText}  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingTerm>())!;
        Assert.Equal(TermValueUnit.Text, created.ValueUnit);
        Assert.Equal(NoticeText, created.TextValue);
        Assert.Null(created.Value);
        Assert.Null(created.DateTimeValue);
        Assert.Null(created.CurrencyCode);
        Assert.Null(created.Interval);
        Assert.Equal(TermDirection.Outgoing, created.Direction);
    }

    [Fact]
    public async Task Post_DateTimeWithAnOffset_Returns201_NormalisedToUtcWithAZ()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), DateTimeBody("2027-03-31T12:00:00+02:00"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"dateTimeValue\":\"2027-03-31T10:00:00Z\"", raw, StringComparison.Ordinal);

        var created = JsonSerializer.Deserialize<ExistingTerm>(raw, JsonSerializerOptions.Web)!;
        Assert.Equal(TermValueUnit.DateTime, created.ValueUnit);
        Assert.Null(created.Value);
        Assert.Null(created.TextValue);
    }

    [Fact]
    public async Task Get_HistoryCurrentAndContract_CarryBothKinds()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostAsync(client, contractId, TextBody(NoticeText));
        await PostAsync(client, contractId, DateTimeBody("2027-03-31T10:00:00Z", label: "Break deadline"));

        var history = (await client.GetFromJsonAsync<List<ExistingTerm>>(Terms(contractId)))!;
        Assert.Equal(NoticeText, history.Single(t => t.ValueUnit == TermValueUnit.Text).TextValue);
        Assert.Equal(
            new DateTime(2027, 3, 31, 10, 0, 0, DateTimeKind.Utc),
            history.Single(t => t.ValueUnit == TermValueUnit.DateTime).DateTimeValue!.Value.ToUniversalTime());

        var current = (await client.GetFromJsonAsync<List<CurrentTerm>>($"{Terms(contractId)}/current"))!;
        Assert.Equal(2, current.Count);
        Assert.Equal(NoticeText, current.Single(t => t.ValueUnit == TermValueUnit.Text).TextValue);
        Assert.NotNull(current.Single(t => t.ValueUnit == TermValueUnit.DateTime).DateTimeValue);

        var contract = (await client.GetFromJsonAsync<ExistingContract>($"{Path}/{contractId}"))!;
        Assert.Contains(contract.CurrentTerms, t => t.ValueUnit == TermValueUnit.Text && t.TextValue == NoticeText && t.Value is null);
        Assert.Contains(contract.CurrentTerms, t => t.ValueUnit == TermValueUnit.DateTime && t.DateTimeValue is not null);
    }

    // ── AC 4: a series changes kind on PUT ────────────────────────────────────

    [Fact]
    public async Task Put_ChangesATermFromAmountToTextAndBack()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var term = await PostAsync(client, contractId, AmountBody());
        var url = $"{Terms(contractId)}/{term.TermId}";

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, TextBody(NoticeText, label: "Service charge"))).StatusCode);
        var asText = await ReadRowAsync(factory, term.TermId);
        Assert.Equal(Odyssey.Context.TermValueUnit.Text, asText.ValueUnit);
        Assert.Null(asText.Value);
        Assert.Equal(NoticeText, asText.TextValue);
        Assert.Null(asText.CurrencyCode);
        Assert.Null(asText.Interval);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url, AmountBody())).StatusCode);
        var asAmount = await ReadRowAsync(factory, term.TermId);
        Assert.Equal(Odyssey.Context.TermValueUnit.Amount, asAmount.ValueUnit);
        Assert.Equal(45m, asAmount.Value);
        Assert.Null(asAmount.TextValue);
        Assert.Null(asAmount.DateTimeValue);
    }

    // ── AC 6–8: every shape rule refuses with its own field key ───────────────

    public static TheoryData<string, object> ShapeViolations()
    {
        var control = "Notice\u202Eperiod";
        return new TheoryData<string, object>
        {
            { nameof(NewTerm.TextValue), TextBody(null) },
            { nameof(NewTerm.TextValue), TextBody("      ") },
            { nameof(NewTerm.TextValue), TextBody(new string('x', TermTextValue.MaxLength + 1)) },
            { nameof(NewTerm.TextValue), TextBody("line one\nline two") },
            { nameof(NewTerm.TextValue), TextBody("tab\tseparated") },
            { nameof(NewTerm.TextValue), TextBody(control) },
            { nameof(NewTerm.Value), TextBody(NoticeText, value: 1m) },
            { nameof(NewTerm.DateTimeValue), TextBody(NoticeText, dateTimeValue: "2027-03-31T10:00:00Z") },
            { nameof(NewTerm.CurrencyCode), TextBody(NoticeText, currencyCode: "EUR") },
            { nameof(NewTerm.Interval), TextBody(NoticeText, interval: 5) },
            { nameof(NewTerm.IntervalCount), TextBody(NoticeText, interval: null, intervalCount: 2) },
            { nameof(NewTerm.AnchorDate), TextBody(NoticeText, anchorDate: "2026-02-01T00:00:00Z") },
            { nameof(NewTerm.Direction), TextBody(NoticeText, direction: 1) },
            { nameof(NewTerm.DateTimeValue), DateTimeBody(null) },
            { nameof(NewTerm.DateTimeValue), DateTimeBody("2027-03-31T12:00:00") },
            { nameof(NewTerm.DateTimeValue), DateTimeBody("1899-12-31T23:59:59Z") },
            { nameof(NewTerm.DateTimeValue), DateTimeBody("2201-01-01T00:00:00Z") },
            { nameof(NewTerm.TextValue), DateTimeBody("2027-03-31T10:00:00Z", textValue: NoticeText) },
            { nameof(NewTerm.Value), new { label = "Rate", valueUnit = 0, effectiveFrom = "2026-02-01T00:00:00Z" } },
            { nameof(NewTerm.Value), new { label = "Rent", valueUnit = 1, currencyCode = "EUR", effectiveFrom = "2026-02-01T00:00:00Z" } },
            { nameof(NewTerm.TextValue), new { label = "Rent", valueUnit = 1, value = 10m, currencyCode = "EUR", textValue = NoticeText, effectiveFrom = "2026-02-01T00:00:00Z" } },
        };
    }

    [Theory]
    [MemberData(nameof(ShapeViolations))]
    public async Task Post_ShapeViolation_Returns400KeyedOnTheField(string field, object body)
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ApiProblemBody>())!;
        Assert.NotNull(problem.Errors);
        Assert.Contains(problem.Errors!.Keys, key => string.Equals(key, field, StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("." + field, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await ReadAllRowsAsync(factory));
    }

    /// <summary>
    /// The exact edges are ACCEPTED: a text of exactly the maximum length after the trim, and each
    /// end of the date-time range. The refusals one step beyond each are in
    /// <see cref="ShapeViolations"/>, so an off-by-one in either comparison fails one side or the other.
    /// </summary>
    [Theory]
    [InlineData("text", null)]
    [InlineData("datetime", "1900-01-01T00:00:00Z")]
    [InlineData("datetime", "2200-12-31T23:59:59Z")]
    public async Task Post_AtTheExactBoundary_Returns201(string kind, string? instant)
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);
        var atMax = new string('x', TermTextValue.MaxLength);

        var response = await client.PostAsJsonAsync(Terms(contractId),
            kind == "text" ? TextBody(atMax) : DateTimeBody(instant));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ExistingTerm>())!;
        if (kind == "text")
            Assert.Equal(TermTextValue.MaxLength, created.TextValue!.Length);
        else
            Assert.Equal(DateTime.Parse(instant!, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                created.DateTimeValue!.Value.ToUniversalTime());
    }

    /// <summary>AC 7 — a refusal names the field and the rule and never echoes the submitted text.</summary>
    [Theory]
    [InlineData("SECRET-CLAUSE line\nbreak")]
    [InlineData("SECRET-CLAUSE\u202Ereversed")]
    public async Task Post_RefusedText_IsNeverEchoedInTheBody(string text)
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        var response = await client.PostAsJsonAsync(Terms(contractId), TextBody(text));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("SECRET-CLAUSE", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>AC 8 — the numeric kinds keep their behaviour: a valid amount and rate still post.</summary>
    [Fact]
    public async Task Post_NumericKinds_StillSucceed()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Terms(contractId), AmountBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Terms(contractId),
            new { label = "Interest", valueUnit = 0, value = 0.0325m, effectiveFrom = "2026-02-01T00:00:00Z" })).StatusCode);
    }

    // ── AC 9: the claim gates are unchanged ───────────────────────────────────

    [Fact]
    public async Task Writes_WithoutContractsUpdate_Return403()
    {
        await using var factory = await NewFactoryAsync([PermissionClaims.ContractsRead, PermissionClaims.ContractsCreate]);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Terms(contractId), TextBody(NoticeText))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{Terms(contractId)}/{Guid.NewGuid()}", TextBody(NoticeText))).StatusCode);
    }

    // ── AC 10: the TermChanged event renders the value ────────────────────────

    [Fact]
    public async Task Post_StagesATermChangedEvent_RenderingTheTextQuotedAndTheInstantInUtc()
    {
        await using var factory = await NewFactoryAsync(ReadWrite);
        using var client = factory.CreateClient();
        var contractId = await CreateContractAsync(client);

        await PostAsync(client, contractId, TextBody(NoticeText));
        await PostAsync(client, contractId, DateTimeBody("2027-03-31T12:00:00+02:00", label: "Break deadline"));

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var events = await context.ContractEvents.Where(e => e.ContractId == contractId).ToListAsync();

        Assert.All(events, e => Assert.Equal((int)ContractEventType.TermChanged, (int)e.Type));
        Assert.Contains(events, e => e.Description!.Contains($"“{NoticeText}”", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Description!.Contains("2027-03-31 10:00 UTC", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object TextBody(
        string? textValue, string label = "Notice period", decimal? value = null, string? dateTimeValue = null,
        string? currencyCode = null, int? interval = null, int? intervalCount = null, string? anchorDate = null,
        int direction = 0) => new
    {
        label,
        valueUnit = (int)TermValueUnit.Text,
        direction,
        value,
        textValue,
        dateTimeValue,
        currencyCode,
        interval,
        intervalCount,
        anchorDate,
        effectiveFrom = "2026-02-01T00:00:00Z",
    };

    private static object DateTimeBody(string? dateTimeValue, string label = "Break deadline", string? textValue = null) => new
    {
        label,
        valueUnit = (int)TermValueUnit.DateTime,
        dateTimeValue,
        textValue,
        effectiveFrom = "2026-02-01T00:00:00Z",
    };

    private static object AmountBody() => new
    {
        label = "Service charge",
        valueUnit = (int)TermValueUnit.Amount,
        value = 45m,
        currencyCode = "EUR",
        interval = 5,
        effectiveFrom = "2026-02-01T00:00:00Z",
    };

    private static string Terms(Guid contractId) => $"{Path}/{contractId}/terms";

    private static async Task<ExistingTerm> PostAsync(HttpClient client, Guid contractId, object body)
    {
        var response = await client.PostAsJsonAsync(Terms(contractId), body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingTerm>())!;
    }

    private static async Task<Term> ReadRowAsync(ApiFactory factory, Guid termId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Terms.AsNoTracking().SingleAsync(t => t.TermId == termId);
    }

    private static async Task<List<Term>> ReadAllRowsAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.Terms.AsNoTracking().ToListAsync();
    }

    private static async Task<Guid> CreateContractAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Path, new NewContract
        {
            Name = "Maple St lease",
            Type = ContractType.Rental,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExistingContract>())!.ContractId;
    }

    private static async Task<ApiFactory> NewFactoryAsync(IReadOnlyCollection<string> permissions)
    {
        var factory = new ApiFactory(permissions);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OdysseyContext>().Database.EnsureCreatedAsync();
        return factory;
    }

    private sealed record ApiProblemBody
    {
        public Dictionary<string, string[]>? Errors { get; init; }
    }

    private sealed class ApiFactory(IReadOnlyCollection<string> permissions)
        : OdysseyApiFactory(permissions, ActorUserId, configuration: null, configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedToday));
        });

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
