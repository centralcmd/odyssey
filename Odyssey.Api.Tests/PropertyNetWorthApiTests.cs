using System.Net.Http.Json;
using System.Text.RegularExpressions;
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
/// Issue #214 over HTTP: which callers' net worth includes property value. The rule is claim-decided —
/// <c>properties.read</c> <b>and</b> <c>properties.estimates.read</c> — and for anyone else both
/// endpoints answer exactly the accounts-only figure, byte for byte, whatever the property tables hold.
/// </summary>
public class PropertyNetWorthApiTests
{
    private const string ActorUserId = "property-net-worth-actor-id";
    private const string TotalsPath = "/api/accounts/totals?mainCurrency=USD";
    private const string HistoryPath = "/api/accounts/net-worth-history?mainCurrency=USD&from=2026-01-01&to=2026-04-30";

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

    private static readonly string[] Entitled =
        [PermissionClaims.AccountsRead, PermissionClaims.PropertiesRead, PermissionClaims.PropertiesEstimatesRead];

    public static TheoryData<string[]> Unentitled() => new()
    {
        new[] { PermissionClaims.AccountsRead },
        new[] { PermissionClaims.AccountsRead, PermissionClaims.PropertiesRead },
        new[] { PermissionClaims.AccountsRead, PermissionClaims.PropertiesEstimatesRead },
    };

    // ── AC1 — the entitled caller ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithBothPropertyClaims_BothEndpointsIncludePropertyValue_AndAgree()
    {
        await using var factory = new ApiFactory(Entitled);
        await SeedAsync(factory, withProperties: true);
        using var client = factory.CreateClient();

        var totals = await client.GetFromJsonAsync<AccountTotals>(TotalsPath);
        var history = await client.GetFromJsonAsync<NetWorthHistory>(
            "/api/accounts/net-worth-history?mainCurrency=USD");

        Assert.NotNull(totals);
        Assert.NotNull(history);
        Assert.True(totals.PropertiesIncluded);
        Assert.Equal(500_000m, totals.PropertyValue);
        Assert.Equal(501_000m, totals.TotalAssets);
        Assert.Equal(1, totals.ContributingPropertyCount);
        Assert.Equal(1, totals.UnvaluedPropertyCount);
        Assert.Equal("Boat", Assert.Single(totals.UnconvertedProperties).Name);
        var propertyRow = Assert.Single(totals.Allocations, row => row.Kind == NetWorthAllocationKind.Property);
        Assert.Equal(("House", 500_000m), (propertyRow.Name, propertyRow.Value));
        Assert.Equal(totals.NetWorth, totals.Allocations.Sum(row => row.Value));

        Assert.True(history.PropertiesIncluded);
        var last = history.Points[^1];
        Assert.Equal(totals.NetWorth, last.NetWorth);
        Assert.Equal(totals.PropertyValue, last.PropertyValue);
    }

    // ── AC2, AC3 (Api tier) — the unentitled caller sees today's bytes ────────────────────────

    [Theory]
    [MemberData(nameof(Unentitled))]
    public async Task WithoutEitherPropertyClaim_TheResponseIsByteIdentical_WithOrWithoutProperties(string[] permissions)
    {
        await using var empty = new ApiFactory(permissions);
        await SeedAsync(empty, withProperties: false);
        await using var populated = new ApiFactory(permissions);
        await SeedAsync(populated, withProperties: true);

        using var emptyClient = empty.CreateClient();
        using var populatedClient = populated.CreateClient();

        foreach (var path in new[] { TotalsPath, HistoryPath })
        {
            var baseline = await emptyClient.GetStringAsync(path);
            var body = await populatedClient.GetStringAsync(path);
            Assert.Equal(baseline, body);
        }

        var totals = await populatedClient.GetFromJsonAsync<AccountTotals>(TotalsPath);
        Assert.NotNull(totals);
        Assert.False(totals.PropertiesIncluded);
        Assert.Null(totals.PropertyValue);
        Assert.Equal(1000m, totals.TotalAssets);
    }

    // ── AC4 — one helper decides, for both actions ────────────────────────────────────────────

    [Fact]
    public void BothNetWorthActions_ReadTheOneHelper_AndNeitherInlinesTheClaim()
    {
        var source = RepositoryRoot.ReadAllText(System.IO.Path.Combine("Odyssey.Api", "Controllers", "AccountController.cs"));

        foreach (var action in new[] { "GetTotals", "GetNetWorthHistory" })
        {
            var body = ActionBody(source, action);
            Assert.Contains("CanIncludeProperties()", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PropertiesEstimatesRead", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PropertiesRead", body, StringComparison.Ordinal);
        }
    }

    private static string ActionBody(string source, string action)
    {
        var start = Regex.Match(source, $@"public async Task<IActionResult> {action}\(");
        Assert.True(start.Success, $"{action} not found");
        var open = source.IndexOf('{', start.Index);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{action}'s body is unbalanced.");
    }

    // ── Seed ──────────────────────────────────────────────────────────────────────────────────

    private static readonly Guid CheckingId = Guid.Parse("00000000-0000-0000-0000-00000000c0de");
    private static readonly Guid HouseAccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static async Task SeedAsync(ApiFactory factory, bool withProperties)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        await context.Database.EnsureCreatedAsync();

        var opened = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Accounts.AddRange(
            new Account
            {
                AccountId = CheckingId, Name = "Checking", Description = "Checking", Opened = opened,
                AccountType = AccountType.CheckingAccount, CurrencyCode = "USD",
            },
            new Account
            {
                AccountId = HouseAccountId, Name = "House (account)", Description = "House", Opened = opened,
                AccountType = AccountType.OtherAsset, CurrencyCode = "USD",
            });
        context.Transactions.Add(new Transaction
        {
            TransactionId = Guid.Parse("00000000-0000-0000-0000-0000000000f1"),
            Description = "Salary",
            Amount = 1000m,
            TimeStamp = opened.AddMonths(1),
            AccountId = CheckingId,
        });

        if (withProperties)
        {
            var house = NewProperty("House", "USD");
            var boat = NewProperty("Boat", "GBP");
            context.Properties.AddRange(house, boat, NewProperty("Lakeside plot", "USD"));
            context.PropertyEstimates.AddRange(
                NewEstimate(house, 500_000m, opened),
                NewEstimate(boat, 20_000m, opened));
        }

        await context.SaveChangesAsync();
    }

    private static Property NewProperty(string name, string currency) => new()
    {
        PropertyId = Guid.NewGuid(),
        Name = name,
        Description = name,
        Type = PropertyType.RealEstate,
        CurrencyCode = currency,
    };

    private static PropertyEstimate NewEstimate(Property property, decimal value, DateTime effectiveFrom) => new()
    {
        PropertyEstimateId = Guid.NewGuid(),
        PropertyId = property.PropertyId,
        Value = value,
        CurrencyCode = property.CurrencyCode,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };
}
