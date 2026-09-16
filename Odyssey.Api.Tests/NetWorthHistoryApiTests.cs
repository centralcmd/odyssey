using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Api.Tests;

/// <summary>
/// <c>GET /api/accounts/net-worth-history</c> over HTTP (issue #90): the claim gate, the two shapes of
/// <c>400</c>, the four empty causes, ISO-8601 date binding, and the exact agreement between the final
/// point and <c>/api/accounts/totals</c>.
/// </summary>
public class NetWorthHistoryApiTests
{
    private const string ActorUserId = "net-worth-history-actor-id";
    private const string Path = "/api/accounts/net-worth-history";
    private const string TotalsPath = "/api/accounts/totals";

    // Pinned so both services resolve the same `now`. Comparing two HTTP requests that each resolve
    // their own would make the agreement below a flaky test rather than a contract.
    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(
            permissions,
            ActorUserId,
            configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
            });

    private static readonly string[] ReadAccounts = [PermissionClaims.AccountsRead];

    // ── AC12 — the claim gate ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task WithoutAccountsRead_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(permissions: []);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task WithAccountsRead_ReturnsOk()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path)).StatusCode);
    }

    // ── AC2 — the final point equals /accounts/totals, exactly ────────────────────────────────

    [Fact]
    public async Task TheFinalPoint_EqualsTheTotalsEndpointExactly()
    {
        await using var factory = new ApiFactory(ReadAccounts);

        // The dataset deliberately carries future-dated rows of all three kinds plus an account that
        // has not opened yet. Before issue #90 G7 the totals counted every one of them and the two
        // endpoints could not have agreed.
        await SeedAsync(factory, context =>
        {
            var checking = Guid.NewGuid();
            var savings = Guid.NewGuid();
            var house = Guid.NewGuid();
            var card = Guid.NewGuid();
            var future = Guid.NewGuid();

            context.Accounts.AddRange(
                Account(checking, "Checking", AccountType.CheckingAccount, "USD", FixedNow.AddYears(-2)),
                Account(savings, "EUR Savings", AccountType.SavingsAccount, "EUR", FixedNow.AddYears(-2)),
                Account(house, "House", AccountType.Property, "USD", FixedNow.AddYears(-2)),
                Account(card, "Card", AccountType.CreditCard, "USD", FixedNow.AddYears(-2)),
                Account(future, "Opens tomorrow", AccountType.CheckingAccount, "USD", FixedNow.AddDays(1)));

            context.Transactions.AddRange(
                Transaction(checking, 12_345.67m, FixedNow.AddMonths(-10)),
                Transaction(checking, -2_001.50m, FixedNow.AddDays(-3)),
                Transaction(checking, 999_999m, FixedNow.AddDays(2)),   // future — excluded
                Transaction(savings, 4_200.25m, FixedNow.AddMonths(-8)),
                Transaction(card, -3_150.75m, FixedNow.AddMonths(-1)),
                Transaction(house, 1m, FixedNow.AddMonths(-20)),
                Transaction(future, 50_000m, FixedNow.AddDays(3)));

            context.AccountEstimates.AddRange(
                Estimate(house, 4_750_000.50m, FixedNow.AddMonths(-6)),
                Estimate(house, 9_000_000m, FixedNow.AddMonths(3)));     // future — not yet in force

            context.ExchangeRates.AddRange(
                Rate("EUR", "USD", 1.0875m, FixedNow.AddMonths(-9)),
                Rate("EUR", "USD", 99m, FixedNow.AddDays(1)));           // future — not in force
        });

        using var client = factory.CreateClient();

        var history = await client.GetFromJsonAsync<NetWorthHistory>($"{Path}?mainCurrency=USD");
        var totals = await client.GetFromJsonAsync<AccountTotals>($"{TotalsPath}?mainCurrency=USD");

        Assert.NotNull(history);
        Assert.NotNull(totals);

        var last = history.Points[^1];
        Assert.Equal(totals.TotalAssets, last.TotalAssets);
        Assert.Equal(totals.TotalLiabilities, last.TotalLiabilities);
        Assert.Equal(totals.NetWorth, last.NetWorth);
        Assert.Equal(DateOnly.FromDateTime(FixedNow), last.Date);
    }

    // ── AC10 — the two shapes of 400 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ARangeError_CarriesAnErrorsDictionary()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?from=2026-05-01&to=2026-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("errors", out var errors));
        Assert.Equal("From", errors.EnumerateObject().Single().Name);
    }

    [Fact]
    public async Task AFutureTo_IsRejected()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?to=2026-06-16");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("errors", out var errors));
        Assert.Equal("To", errors.EnumerateObject().Single().Name);
    }

    [Fact]
    public async Task AWindowOverTheIntervalCap_IsRejectedAndNamesTheCap()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        // 32 days at Daily resolution, one over the cap.
        var response = await client.GetAsync($"{Path}?interval=Daily&from=2026-05-15&to=2026-06-15");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"errors\"", body, StringComparison.Ordinal);
        Assert.Contains(NetWorthHistoryQuery.MaxDailyPoints.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowExactlyAtTheIntervalCap_IsAccepted()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?interval=Daily&from=2026-05-16&to=2026-06-15");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnUnsupportedCurrency_IsAFlatDetail_AndEchoesNothingElse()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory, context => context.Accounts.Add(
            Account(Guid.NewGuid(), "Zurich brokerage", AccountType.InvestmentAccount, "CHF", FixedNow.AddYears(-1))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?mainCurrency=ZZZ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The currency check is a database lookup, so it is a DomainValidationException and comes back
        // as a flat detail rather than an errors dictionary. §11 keeps the two shapes apart on purpose.
        using var problem = JsonDocument.Parse(body);
        Assert.False(problem.RootElement.TryGetProperty("errors", out _));
        Assert.Contains("ZZZ", problem.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        // It echoes the caller's own input and nothing else — no account name, id or figure.
        Assert.DoesNotContain("Zurich brokerage", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CHF", body, StringComparison.Ordinal);
    }

    // ── AC11 — the four empty causes are distinguishable from the payload alone ────────────────

    [Fact]
    public async Task NoAccounts_IsReportedAsItsOwnCause()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        var history = await client.GetFromJsonAsync<NetWorthHistory>(Path);

        Assert.NotNull(history);
        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.NoAccounts, history.EmptyReason);
    }

    [Fact]
    public async Task AWindowBeforeTheFirstAccount_IsReportedAsItsOwnCause()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory, context => context.Accounts.Add(
            Account(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD", FixedNow.AddYears(-1))));
        using var client = factory.CreateClient();

        var history = await client.GetFromJsonAsync<NetWorthHistory>($"{Path}?from=2019-01-01&to=2019-06-30");

        Assert.NotNull(history);
        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.WindowBeforeFirstAccount, history.EmptyReason);
    }

    [Fact]
    public async Task NothingConvertible_IsReportedAsItsOwnCause()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory, context =>
        {
            var zurich = Guid.NewGuid();
            context.Accounts.Add(Account(zurich, "Zurich brokerage", AccountType.InvestmentAccount, "CHF", FixedNow.AddYears(-1)));
            context.Transactions.Add(Transaction(zurich, 900m, FixedNow.AddMonths(-6)));
        });
        using var client = factory.CreateClient();

        var history = await client.GetFromJsonAsync<NetWorthHistory>($"{Path}?mainCurrency=USD");

        Assert.NotNull(history);
        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.NothingConvertible, history.EmptyReason);
        Assert.Single(history.UnconvertedAccounts);
    }

    /// <summary>
    /// The causes have to be separable from the payload without the client inferring anything. An
    /// earlier draft made three of the four responses it then had byte-identical apart from
    /// <c>unconvertedAccounts</c>, which cannot tell "no accounts" from "the window ends before the
    /// first one" at all.
    /// </summary>
    [Fact]
    public async Task TheEmptyCauses_AreCarriedExplicitly_NotInferred()
    {
        await using var noAccounts = new ApiFactory(ReadAccounts);
        await SeedAsync(noAccounts);

        await using var beforeFirst = new ApiFactory(ReadAccounts);
        await SeedAsync(beforeFirst, context => context.Accounts.Add(
            Account(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD", FixedNow.AddYears(-1))));

        using var first = noAccounts.CreateClient();
        using var second = beforeFirst.CreateClient();

        var a = await first.GetFromJsonAsync<NetWorthHistory>(Path);
        var b = await second.GetFromJsonAsync<NetWorthHistory>($"{Path}?from=2019-01-01&to=2019-06-30");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Empty(a.Points);
        Assert.Empty(b.Points);
        Assert.Empty(a.UnconvertedAccounts);
        Assert.Empty(b.UnconvertedAccounts);

        // Identical in every other respect, and still distinguishable.
        Assert.NotEqual(a.EmptyReason, b.EmptyReason);
    }

    // ── AC23 / AC25 — date binding ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALocaleFormattedDate_IsRejected()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        // DateOnly's TypeConverter parses under the current culture, so a d/M/y string binds on some
        // hosts and not others. ISO-8601 is the contract on both sides.
        var response = await client.GetAsync($"{Path}?from=15%2F09%2F2026");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnIsoDate_Binds()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory, context => context.Accounts.Add(
            Account(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))));
        using var client = factory.CreateClient();

        var history = await client.GetFromJsonAsync<NetWorthHistory>($"{Path}?from=2026-02-17&to=2026-04-30");

        Assert.NotNull(history);
        Assert.Equal(new DateOnly(2026, 2, 1), history.From);
        Assert.Equal(new DateOnly(2026, 4, 30), history.To);
    }

    /// <summary>
    /// AC25. The earliest representable <c>to</c> with no <c>from</c>: the default window saturates at
    /// <see cref="DateOnly.MinValue"/>, so <c>from == to</c> and every rule passes — the response is a
    /// well-formed <c>200</c> with no points, and specifically <b>never a 500</b>. An earlier draft of
    /// the spec asserted a <c>400</c> here, which its own rules do not produce; a test written that way
    /// would have failed a correct implementation and pushed an implementer into adding a spurious
    /// rejection.
    /// </summary>
    [Fact]
    public async Task TheEarliestRepresentableTo_IsAWellFormedEmptyResponse()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory, context => context.Accounts.Add(
            Account(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD", FixedNow.AddYears(-1))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Path}?to=0001-01-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<NetWorthHistory>();
        Assert.NotNull(history);
        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.WindowBeforeFirstAccount, history.EmptyReason);
    }

    [Fact]
    public async Task TheLatestRepresentableWindow_DoesNotOverflow()
    {
        await using var factory = new ApiFactory(ReadAccounts);
        await SeedAsync(factory, context => context.Accounts.Add(
            Account(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD", FixedNow.AddYears(-1))));
        using var client = factory.CreateClient();

        // A yearly window from the first representable date: the count is far over the cap, so this is
        // a 400 naming it rather than an ArgumentOutOfRangeException on the way to counting.
        var response = await client.GetAsync($"{Path}?interval=Yearly&from=0001-01-01&to=2026-06-15");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── AC24 — the OpenAPI document ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Swagger_RendersFromAndToAsIsoDates()
    {
        await using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parameters = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/accounts/net-worth-history")
            .GetProperty("get")
            .GetProperty("parameters");

        foreach (var name in new[] { "From", "To" })
        {
            var parameter = parameters.EnumerateArray()
                .Single(p => string.Equals(p.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
            var schema = parameter.GetProperty("schema");

            Assert.Equal("string", schema.GetProperty("type").GetString());
            Assert.Equal("date", schema.GetProperty("format").GetString());
        }
    }

    [Fact]
    public async Task Swagger_DeclaresTheForbiddenResponse()
    {
        await using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var responses = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/accounts/net-worth-history")
            .GetProperty("get")
            .GetProperty("responses");

        Assert.True(responses.TryGetProperty("403", out _));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static async Task SeedAsync(ApiFactory factory, Action<OdysseyContext>? seed = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();
        seed?.Invoke(context);
        await context.SaveChangesAsync();
    }

    private static Account Account(Guid id, string name, AccountType type, string currency, DateTime opened) => new()
    {
        AccountId = id,
        Name = name,
        Description = name,
        Opened = opened,
        AccountType = type,
        CurrencyCode = currency,
    };

    private static Transaction Transaction(Guid accountId, decimal amount, DateTime at) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "tx",
        Amount = amount,
        TimeStamp = at,
        AccountId = accountId,
    };

    private static AccountEstimate Estimate(Guid accountId, decimal value, DateTime effectiveFrom) => new()
    {
        AccountEstimateId = Guid.NewGuid(),
        AccountId = accountId,
        Value = value,
        CurrencyCode = "USD",
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    private static ExchangeRate Rate(string from, string to, decimal rate, DateTime asOf) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = asOf,
    };
}
