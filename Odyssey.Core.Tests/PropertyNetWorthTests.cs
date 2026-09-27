using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #214: the estimated value of held properties in <c>/accounts/totals</c> and
/// <c>/accounts/net-worth-history</c> — the valuation rules (V1–V11), the empty reasons, the
/// "not included" shape, the no-query guarantee, and the two parity rules (history vs totals, and
/// <see cref="PropertyMembership"/>'s two bodies).
/// </summary>
/// <remarks>
/// Every test pins the clock at <see cref="FixedNow"/>. A monthly query over January–April 2026 yields
/// points dated 2026-02-01, 03-01, 04-01 and 05-01, each measured at that instant, exclusively.
/// </remarks>
public class PropertyNetWorthTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static NetWorthHistoryService History(OdysseyContext context, DateTime? now = null) =>
        new(context, new CurrencyConversionService(context), new FixedTimeProvider(now ?? FixedNow));

    private static AccountTotalsService Totals(OdysseyContext context, DateTime? now = null) =>
        new(context, new CurrencyConversionService(context), new FixedTimeProvider(now ?? FixedNow));

    private static NetWorthHistoryQuery Query(DateOnly? from = null, DateOnly? to = null, NetWorthInterval interval = NetWorthInterval.Monthly) =>
        new() { Interval = interval, MainCurrency = "USD", From = from, To = to };

    private static NetWorthHistoryQuery JanToApr() => Query(D(2026, 1, 1), D(2026, 4, 30));

    private static Account NewAccount(string name, AccountType type, string currency, DateTime opened, DateTime? closed = null) => new()
    {
        AccountId = Guid.NewGuid(),
        Name = name,
        Description = name,
        Opened = opened,
        AccountType = type,
        CurrencyCode = currency,
        Closed = closed,
    };

    private static Property NewProperty(
        string name,
        string currency = "USD",
        DateTime? acquired = null,
        DateTime? disposed = null,
        DateTime? archived = null) => new()
    {
        PropertyId = Guid.NewGuid(),
        Name = name,
        Description = "Street address 1, 0150 Oslo",
        Type = PropertyType.RealEstate,
        CurrencyCode = currency,
        AcquiredDate = acquired,
        DisposedDate = disposed,
        Archived = archived,
        Notes = "Private note",
    };

    private static PropertyEstimate NewEstimate(Property property, decimal value, DateTime effectiveFrom, DateTime? created = null) => new()
    {
        PropertyEstimateId = Guid.NewGuid(),
        PropertyId = property.PropertyId,
        Value = value,
        CurrencyCode = property.CurrencyCode,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = created ?? effectiveFrom,
    };

    private static ExchangeRate NewRate(string from, string to, decimal rate, DateTime asOf) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = asOf,
    };

    private static Transaction NewTransaction(Account account, decimal amount, DateTime at) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "tx",
        Amount = amount,
        TimeStamp = at,
        AccountId = account.AccountId,
    };

    /// <summary>A USD checking account opened long before the window, holding 1 000.</summary>
    private static Account SeedChecking(OdysseyContext context)
    {
        var checking = NewAccount("Checking", AccountType.CheckingAccount, "USD", Utc(2024, 1, 1));
        context.Accounts.Add(checking);
        context.Transactions.Add(NewTransaction(checking, 1000m, Utc(2024, 2, 1)));
        return checking;
    }

    // ── AC1, AC19 — /totals with properties ───────────────────────────────────────────────────

    [Fact]
    public async Task Totals_IncludeEveryHeldPropertysInForceEstimate_AsASeparateAssetLine()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);

        var apartment = NewProperty("City apartment", "EUR", acquired: Utc(2020, 1, 1));
        var cabin = NewProperty("Cabin", archived: Utc(2025, 1, 1));   // archived but held → counts (D3)
        var plot = NewProperty("Lakeside plot");                       // held, never estimated → unvalued
        var boat = NewProperty("Boat", "GBP");                         // held, valued, no rate → unconverted
        var sold = NewProperty("Sold car", disposed: Utc(2026, 1, 1)); // disposed before now → not held
        var future = NewProperty("Next house", acquired: FixedNow.AddDays(10)); // not yet acquired
        context.Properties.AddRange(apartment, cabin, plot, boat, sold, future);
        context.PropertyEstimates.AddRange(
            NewEstimate(apartment, 100_000m, Utc(2025, 1, 1)),
            NewEstimate(apartment, 999_999m, FixedNow.AddDays(1)),    // future → not in force
            NewEstimate(cabin, 50_000m, Utc(2025, 6, 1)),
            NewEstimate(boat, 20_000m, Utc(2025, 1, 1)),
            NewEstimate(sold, 7_000m, Utc(2024, 1, 1)),
            NewEstimate(future, 1_000_000m, Utc(2025, 1, 1)));
        context.ExchangeRates.Add(NewRate("EUR", "USD", 1.1m, Utc(2025, 1, 1)));
        await context.SaveChangesAsync();

        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);

        Assert.True(totals.PropertiesIncluded);
        Assert.Equal(110_000m + 50_000m, totals.PropertyValue);
        Assert.Equal(1000m + 160_000m, totals.TotalAssets);
        Assert.Equal(totals.TotalAssets - totals.TotalLiabilities, totals.NetWorth);
        Assert.Equal(2, totals.ContributingPropertyCount);
        Assert.Equal(1, totals.UnvaluedPropertyCount);
        var named = Assert.Single(totals.UnconvertedProperties);
        Assert.Equal((boat.PropertyId, "Boat", "GBP"), (named.PropertyId, named.Name, named.CurrencyCode));
    }

    [Fact]
    public async Task Totals_WithAPropertyThatHasALegalZeroEstimate_ContributesZero_AndIsCounted()
    {
        await using var context = TestContextFactory.Create();
        var plot = NewProperty("Plot");
        context.Properties.Add(plot);
        context.PropertyEstimates.Add(NewEstimate(plot, 0m, Utc(2025, 1, 1)));
        await context.SaveChangesAsync();

        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);

        Assert.Equal(0m, totals.PropertyValue);
        Assert.Equal(1, totals.ContributingPropertyCount);
        Assert.Equal(0, totals.UnvaluedPropertyCount);
    }

    // ── AC2 — not included is exactly today's figure ──────────────────────────────────────────

    [Fact]
    public async Task NotIncluded_BothEndpointsEqualTheAccountsOnlyFigure_WithEveryMemberAtItsNotIncludedValue()
    {
        await using var accountsOnly = TestContextFactory.Create();
        SeedChecking(accountsOnly);
        accountsOnly.Accounts.Add(NewAccount("House (account)", AccountType.OtherAsset, "USD", Utc(2024, 1, 1)));
        await accountsOnly.SaveChangesAsync();

        await using var withProperties = TestContextFactory.Create();
        SeedChecking(withProperties);
        withProperties.Accounts.Add(NewAccount("House (account)", AccountType.OtherAsset, "USD", Utc(2024, 1, 1)));
        var house = NewProperty("House");
        var boat = NewProperty("Boat", "GBP");
        withProperties.Properties.AddRange(house, boat, NewProperty("Plot"));
        withProperties.PropertyEstimates.AddRange(
            NewEstimate(house, 500_000m, Utc(2025, 1, 1)),
            NewEstimate(boat, 20_000m, Utc(2025, 1, 1)));
        await withProperties.SaveChangesAsync();

        var baseline = await Totals(accountsOnly).ComputeAsync("USD", includeProperties: false);
        var totals = await Totals(withProperties).ComputeAsync("USD", includeProperties: false);

        Assert.Equal(baseline.TotalAssets, totals.TotalAssets);
        Assert.Equal(baseline.NetWorth, totals.NetWorth);
        Assert.False(totals.PropertiesIncluded);
        Assert.Null(totals.PropertyValue);
        Assert.Equal(0, totals.ContributingPropertyCount);
        Assert.Equal(0, totals.UnvaluedPropertyCount);
        Assert.Empty(totals.UnconvertedProperties);

        var baselineHistory = await History(accountsOnly).ComputeAsync(JanToApr(), includeProperties: false);
        var history = await History(withProperties).ComputeAsync(JanToApr(), includeProperties: false);

        Assert.False(history.PropertiesIncluded);
        Assert.Empty(history.UnconvertedProperties);
        Assert.Equal(baselineHistory.Points.Select(p => p.NetWorth), history.Points.Select(p => p.NetWorth));
        Assert.All(history.Points, point =>
        {
            Assert.Null(point.PropertyValue);
            Assert.Equal(0, point.ContributingPropertyCount + point.UnconvertedPropertyCount
                            + point.RevaluedPropertyCount + point.UnvaluedPropertyCount);
        });
    }

    // ── AC3 — the no-query guarantee, with a positive control ─────────────────────────────────

    [Fact]
    public async Task NotIncluded_NeitherServiceQueriesThePropertyTables()
    {
        var store = await SeedPoisonStoreAsync();

        await using (var poisoned = Poisoned<Property>(store))
        {
            await Totals(poisoned).ComputeAsync("USD", includeProperties: false);
            await History(poisoned).ComputeAsync(JanToApr(), includeProperties: false);
        }

        await using (var poisoned = Poisoned<PropertyEstimate>(store))
        {
            await Totals(poisoned).ComputeAsync("USD", includeProperties: false);
            await History(poisoned).ComputeAsync(JanToApr(), includeProperties: false);
        }
    }

    [Fact]
    public async Task Included_ThePoisonedContextFires_SoTheNegativeTestIsNotVacuous()
    {
        var store = await SeedPoisonStoreAsync();

        await using (var poisoned = Poisoned<Property>(store))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Totals(poisoned).ComputeAsync("USD", includeProperties: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                History(poisoned).ComputeAsync(JanToApr(), includeProperties: true));
        }

        await using (var poisoned = Poisoned<PropertyEstimate>(store))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Totals(poisoned).ComputeAsync("USD", includeProperties: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                History(poisoned).ComputeAsync(JanToApr(), includeProperties: true));
        }
    }

    // ── AC5 — the final point equals /totals, properties included ─────────────────────────────

    [Fact]
    public async Task TheFinalHistoryPoint_EqualsTotals_WithPropertiesIncluded()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        var apartment = NewProperty("Apartment", "EUR", acquired: Utc(2020, 1, 1));
        var boat = NewProperty("Boat", "GBP");
        context.Properties.AddRange(apartment, boat, NewProperty("Plot"));
        context.PropertyEstimates.AddRange(
            NewEstimate(apartment, 100_000m, Utc(2025, 1, 1)),
            NewEstimate(apartment, 120_000m, Utc(2026, 5, 1)),
            NewEstimate(apartment, 130_000m, FixedNow),               // exactly now → not yet in force
            NewEstimate(boat, 20_000m, Utc(2025, 1, 1)));
        context.ExchangeRates.AddRange(
            NewRate("EUR", "USD", 1.1m, Utc(2025, 1, 1)),
            NewRate("EUR", "USD", 1.2m, Utc(2026, 6, 1)));
        await context.SaveChangesAsync();

        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);
        var history = await History(context).ComputeAsync(Query(), includeProperties: true);

        var last = history.Points[^1];
        Assert.Equal(DateOnly.FromDateTime(FixedNow), last.Date);
        Assert.Equal(totals.TotalAssets, last.TotalAssets);
        Assert.Equal(totals.NetWorth, last.NetWorth);
        Assert.Equal(totals.PropertyValue, last.PropertyValue);
        Assert.Equal(144_000m, last.PropertyValue);
        Assert.Equal(totals.ContributingPropertyCount, last.ContributingPropertyCount);
        Assert.Equal(totals.UnvaluedPropertyCount, last.UnvaluedPropertyCount);
        Assert.Equal(totals.UnconvertedProperties.Count, last.UnconvertedPropertyCount);
    }

    /// <summary>AC21 — the fold's "last estimate before b" equals <c>MostEffective()</c> over <c>EffectiveFrom &lt; b</c>.</summary>
    [Fact]
    public async Task ForRandomEstimateSets_TheFinalPointAgreesWithTotals_AtEveryInstant()
    {
        var random = new Random(214);
        for (var run = 0; run < 25; run++)
        {
            await using var context = TestContextFactory.Create();
            var property = NewProperty("House", acquired: Utc(2024, 1, 1));
            context.Properties.Add(property);
            for (var i = 0; i < 6; i++)
            {
                // Coarse dates so ties on EffectiveFrom are common; creation order decides them. The
                // creation instants are distinct, as real ones are — a row identical in both keys has
                // no defined order under any rule.
                var effective = Utc(2025, 1, 1).AddDays(random.Next(0, 12) * 30);
                context.PropertyEstimates.Add(NewEstimate(
                    property, random.Next(1, 1000) * 1000m, effective, effective.AddMinutes(random.Next(0, 5)).AddTicks(i * 10)));
            }

            await context.SaveChangesAsync();

            var now = Utc(2025, 1, 1).AddDays(random.Next(0, 400)).AddHours(random.Next(0, 24));
            var totals = await Totals(context, now).ComputeAsync("USD", includeProperties: true);
            var history = await History(context, now).ComputeAsync(
                Query(interval: NetWorthInterval.Monthly), includeProperties: true);

            var expected = context.PropertyEstimates.AsNoTracking().ToList()
                .Where(estimate => estimate.EffectiveFrom < now)
                .MostEffective()?.Value;

            Assert.Equal(expected ?? 0m, totals.PropertyValue);
            if (history.Points.Count > 0)
            {
                Assert.Equal(totals.PropertyValue, history.Points[^1].PropertyValue);
            }
        }
    }

    [Fact]
    public void OrderByEffectiveAscending_LastBeforeABound_IsMostEffective()
    {
        var random = new Random(90);
        for (var run = 0; run < 200; run++)
        {
            var rows = Enumerable.Range(0, random.Next(0, 8))
                .Select(i =>
                {
                    var effective = Utc(2025, 1, 1).AddDays(random.Next(0, 6));
                    return new PropertyEstimate
                    {
                        PropertyEstimateId = Guid.NewGuid(),
                        Value = random.Next(0, 100),
                        EffectiveFrom = effective,
                        // Distinct creation instants: a row identical in both keys has no defined order.
                        CreatedAtUtc = effective.AddMinutes(random.Next(0, 3)).AddTicks(i * 10),
                    };
                })
                .ToList();
            var bound = Utc(2025, 1, 1).AddDays(random.Next(0, 7));

            var viaFold = rows.OrderByEffectiveAscending().LastOrDefault(row => row.EffectiveFrom < bound);
            var viaTotals = rows.Where(row => row.EffectiveFrom < bound).MostEffective();

            Assert.Equal(viaTotals?.Value, viaFold?.Value);
            Assert.Equal(viaTotals?.EffectiveFrom, viaFold?.EffectiveFrom);
            Assert.Equal(viaTotals?.CreatedAtUtc, viaFold?.CreatedAtUtc);
        }
    }

    // ── AC6, AC7, AC8 — membership is held-at-the-bound, never archive ────────────────────────

    [Fact]
    public async Task APropertyContributesOnlyFromTheFirstBoundAfterItsAcquisition()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        var house = NewProperty("House", acquired: Utc(2026, 3, 1));
        context.Properties.Add(house);
        // Valued at the contract date, before acquisition (V5).
        context.PropertyEstimates.Add(NewEstimate(house, 500_000m, Utc(2025, 12, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        // Feb 1 and Mar 1 are at or before the acquisition instant; Apr 1 is the first bound after it.
        Assert.Equal([0m, 0m, 500_000m, 500_000m], history.Points.Select(p => p.PropertyValue!.Value));
        Assert.Equal([0, 0, 1, 1], history.Points.Select(p => p.ContributingPropertyCount));
        // Before acquisition it is counted nowhere — not even as unvalued.
        Assert.Equal([0, 0, 0, 0], history.Points.Select(p => p.UnvaluedPropertyCount));
        // Its first held point is not a revaluation, although the estimate takes effect there.
        Assert.All(history.Points, p => Assert.Equal(0, p.RevaluedPropertyCount));
        Assert.Equal([1000m, 1000m, 501_000m, 501_000m], history.Points.Select(p => p.NetWorth));
    }

    [Fact]
    public async Task ADisposedPropertyContributesStrictlyBeforeItsDisposal_AndTotalsExcludeIt()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        var car = NewProperty("Car", acquired: Utc(2020, 1, 1), disposed: Utc(2026, 3, 1));
        context.Properties.Add(car);
        context.PropertyEstimates.AddRange(
            NewEstimate(car, 10_000m, Utc(2020, 1, 1)),
            NewEstimate(car, 9_000m, Utc(2026, 3, 1)));   // sale price on the disposal day — never in force
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);
        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);

        Assert.Equal([10_000m, 0m, 0m, 0m], history.Points.Select(p => p.PropertyValue!.Value));
        Assert.Equal([1, 0, 0, 0], history.Points.Select(p => p.ContributingPropertyCount));
        // Disposal is not a revaluation (V10).
        Assert.All(history.Points, p => Assert.Equal(0, p.RevaluedPropertyCount));
        Assert.Equal(0m, totals.PropertyValue);
        Assert.Equal(0, totals.ContributingPropertyCount);
    }

    [Fact]
    public async Task AnArchivedHeldProperty_IsCountedInBothEndpoints()
    {
        await using var context = TestContextFactory.Create();
        var cabin = NewProperty("Cabin", archived: Utc(2026, 2, 1));
        context.Properties.Add(cabin);
        context.PropertyEstimates.Add(NewEstimate(cabin, 300_000m, Utc(2025, 1, 1)));
        await context.SaveChangesAsync();

        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);
        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        Assert.Equal(300_000m, totals.PropertyValue);
        Assert.All(history.Points, p => Assert.Equal(300_000m, p.PropertyValue));
    }

    // ── AC9 — the estimate in force at the bound ──────────────────────────────────────────────

    [Fact]
    public async Task TheEstimateInForceAtABound_IsExclusive_AndTiesResolveToTheNewerRow()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        var house = NewProperty("House");
        context.Properties.Add(house);
        context.PropertyEstimates.AddRange(
            NewEstimate(house, 100m, Utc(2026, 1, 15)),
            NewEstimate(house, 200m, Utc(2026, 3, 1)),                                   // == the Mar 1 bound
            NewEstimate(house, 300m, Utc(2026, 4, 10), created: Utc(2026, 4, 10).AddHours(2)),
            NewEstimate(house, 400m, Utc(2026, 4, 10), created: Utc(2026, 4, 10).AddHours(1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        Assert.Equal([100m, 100m, 200m, 300m], history.Points.Select(p => p.PropertyValue!.Value));
        // AC12 — a change of in-force estimate while held is a revaluation; the first held point never is.
        Assert.Equal([0, 0, 1, 1], history.Points.Select(p => p.RevaluedPropertyCount));
    }

    // ── AC10, AC11 — unvalued and unconverted are different things ────────────────────────────

    [Fact]
    public async Task AHeldPropertyWithNoEstimate_IsUnvalued_NotUnconverted_AndNotPartial()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        context.Properties.Add(NewProperty("Lakeside plot", "GBP"));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);
        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);

        Assert.All(history.Points, p =>
        {
            Assert.Equal(1, p.UnvaluedPropertyCount);
            Assert.Equal(0, p.UnconvertedPropertyCount);
            Assert.Equal(0, p.ContributingPropertyCount);
            Assert.Equal(0m, p.PropertyValue);
        });
        Assert.Empty(history.UnconvertedProperties);
        Assert.Equal(1, totals.UnvaluedPropertyCount);
        Assert.Empty(totals.UnconvertedProperties);
    }

    [Fact]
    public async Task AValuedPropertyWithNoRate_IsNamedOnce_AndCountedOnEveryPointItUnderstated()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        var boat = NewProperty("Boat", "EUR");
        context.Properties.Add(boat);
        context.PropertyEstimates.Add(NewEstimate(boat, 20_000m, Utc(2025, 1, 1)));
        // A rate arrives mid-window: before it the point is understated, never folded in at 1:1.
        context.ExchangeRates.Add(NewRate("EUR", "USD", 1.5m, Utc(2026, 3, 15)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        Assert.Equal([1, 1, 0, 0], history.Points.Select(p => p.UnconvertedPropertyCount));
        Assert.Equal([0m, 0m, 30_000m, 30_000m], history.Points.Select(p => p.PropertyValue!.Value));
        var named = Assert.Single(history.UnconvertedProperties);
        Assert.Equal(boat.PropertyId, named.PropertyId);
    }

    // ── AC12 — revaluation matches AccountCursor ──────────────────────────────────────────────

    [Fact]
    public async Task AnUnvaluedHeldProperty_IsRevaluedWhereItsFirstEstimateTakesEffect()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        var house = NewProperty("House");
        context.Properties.Add(house);
        context.PropertyEstimates.Add(NewEstimate(house, 1_000m, Utc(2026, 2, 15)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        Assert.Equal([1, 0, 0, 0], history.Points.Select(p => p.UnvaluedPropertyCount));
        Assert.Equal([0, 1, 0, 0], history.Points.Select(p => p.RevaluedPropertyCount));
    }

    // ── AC13 — a property-only portfolio charts ───────────────────────────────────────────────

    [Fact]
    public async Task APropertyOnlyPortfolio_Charts_FromTheFirstContributionPeriod()
    {
        await using var context = TestContextFactory.Create();
        var house = NewProperty("House", acquired: Utc(2026, 2, 10));
        context.Properties.Add(house);
        context.PropertyEstimates.Add(NewEstimate(house, 400_000m, Utc(2025, 6, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        Assert.Null(history.EmptyReason);
        Assert.True(history.PropertiesIncluded);
        Assert.Equal(D(2026, 2, 1), history.From);
        Assert.Equal([D(2026, 3, 1), D(2026, 4, 1), D(2026, 5, 1)], history.Points.Select(p => p.Date));
        Assert.All(history.Points, p => Assert.Equal(0, p.ContributingAccountCount));
        Assert.All(history.Points, p => Assert.Equal(400_000m, p.NetWorth));
    }

    // ── AC14, AC15, AC26 — empty reasons and the flag on every return path ────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoMembers_IsNoAccounts_AndCarriesTheClaimDecidedFlag(bool included)
    {
        await using var context = TestContextFactory.Create();
        // Never estimated, acquired == disposed, only estimate on the disposal day: none is eligible.
        var never = NewProperty("Plot");
        var sameDay = NewProperty("Flip", acquired: Utc(2025, 5, 1), disposed: Utc(2025, 5, 1));
        var saleOnly = NewProperty("Sale only", acquired: Utc(2025, 1, 1), disposed: Utc(2025, 6, 1));
        context.Properties.AddRange(never, sameDay, saleOnly);
        context.PropertyEstimates.AddRange(
            NewEstimate(sameDay, 5m, Utc(2025, 1, 1)),
            NewEstimate(saleOnly, 5m, Utc(2025, 6, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: included);

        Assert.Equal(NetWorthEmptyReason.NoAccounts, history.EmptyReason);
        Assert.Equal(included, history.PropertiesIncluded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWindowBeforeTheFirstContribution_IsWindowBeforeFirstAccount_WithTheFlag(bool included)
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount("Checking", AccountType.CheckingAccount, "USD", Utc(2026, 5, 1)));
        var house = NewProperty("House", acquired: Utc(2026, 5, 10));
        context.Properties.Add(house);
        context.PropertyEstimates.Add(NewEstimate(house, 1m, Utc(2026, 5, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: included);

        Assert.Equal(NetWorthEmptyReason.WindowBeforeFirstAccount, history.EmptyReason);
        Assert.Equal(included, history.PropertiesIncluded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosedAccountsPlusANeverEstimatedProperty_IsWindowAfterAllAccountsClosed(bool included)
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount("Old", AccountType.CheckingAccount, "USD", Utc(2024, 1, 1), closed: Utc(2025, 1, 1)));
        context.Properties.Add(NewProperty("Lakeside plot"));
        var sold = NewProperty("Sold", acquired: Utc(2024, 1, 1), disposed: Utc(2025, 1, 1));
        context.Properties.Add(sold);
        context.PropertyEstimates.Add(NewEstimate(sold, 1m, Utc(2024, 2, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: included);

        // Not NothingConvertible: nothing had a value to convert, so no rate is missing.
        Assert.Equal(NetWorthEmptyReason.WindowAfterAllAccountsClosed, history.EmptyReason);
        Assert.Equal(included, history.PropertiesIncluded);
    }

    [Fact]
    public async Task OnlyAValuedPropertyWithNoRate_IsNothingConvertible_AndNamesIt()
    {
        await using var context = TestContextFactory.Create();
        var boat = NewProperty("Boat", "GBP");
        context.Properties.Add(boat);
        context.PropertyEstimates.Add(NewEstimate(boat, 1m, Utc(2025, 1, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: true);

        Assert.Equal(NetWorthEmptyReason.NothingConvertible, history.EmptyReason);
        Assert.True(history.PropertiesIncluded);
        Assert.Equal(boat.PropertyId, Assert.Single(history.UnconvertedProperties).PropertyId);
    }

    [Fact]
    public async Task NothingConvertible_WithPropertiesNotIncluded_CarriesTheFalseFlag()
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount("GBP", AccountType.CheckingAccount, "GBP", Utc(2024, 1, 1)));
        await context.SaveChangesAsync();

        var history = await History(context).ComputeAsync(JanToApr(), includeProperties: false);

        Assert.Equal(NetWorthEmptyReason.NothingConvertible, history.EmptyReason);
        Assert.False(history.PropertiesIncluded);
    }

    [Fact]
    public async Task APopulatedSeries_CarriesTheFlag()
    {
        await using var context = TestContextFactory.Create();
        SeedChecking(context);
        await context.SaveChangesAsync();

        Assert.True((await History(context).ComputeAsync(JanToApr(), includeProperties: true)).PropertiesIncluded);
        Assert.False((await History(context).ComputeAsync(JanToApr(), includeProperties: false)).PropertiesIncluded);
    }

    // ── AC18 — the named record carries exactly three members ─────────────────────────────────

    [Fact]
    public async Task UnconvertedProperties_SerializeExactlyIdNameAndCurrency_AndNoPersonalData()
    {
        await using var context = TestContextFactory.Create();
        var boat = NewProperty("Boat", "GBP");
        context.Properties.Add(boat);
        context.PropertyEstimates.Add(NewEstimate(boat, 1m, Utc(2025, 1, 1)));
        await context.SaveChangesAsync();

        var totals = await Totals(context).ComputeAsync("USD", includeProperties: true);
        var json = JsonSerializer.Serialize(totals, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("unconvertedProperties").EnumerateArray().Single();
        Assert.Equal(["propertyId", "name", "currencyCode"], entry.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("Oslo", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Private note", json, StringComparison.Ordinal);
    }

    // ── AC24 — an older payload reads as accounts-only ────────────────────────────────────────

    [Fact]
    public void APayloadLackingEveryNewMember_DeserializesAsNotIncluded()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var totals = JsonSerializer.Deserialize<AccountTotals>(
            """{"mainCurrencyCode":"NOK","totalAssets":1,"totalLiabilities":0,"netWorth":1,"unconvertedAccounts":[]}""",
            options)!;
        var history = JsonSerializer.Deserialize<NetWorthHistory>(
            """{"mainCurrencyCode":"NOK","interval":2,"from":"2026-01-01","to":"2026-02-01","points":[{"date":"2026-02-01","totalAssets":1,"totalLiabilities":0,"netWorth":1,"unconvertedAccountCount":0,"revaluedAccountCount":0,"contributingAccountCount":1}]}""",
            options)!;

        Assert.False(totals.PropertiesIncluded);
        Assert.Null(totals.PropertyValue);
        Assert.False(history.PropertiesIncluded);
        Assert.Null(history.Points[0].PropertyValue);
    }

    // ── AC27 — PropertyMembership's two bodies agree ──────────────────────────────────────────

    public static TheoryData<DateTime?, DateTime?> MembershipCases()
    {
        var b = Utc(2026, 3, 1);
        return new TheoryData<DateTime?, DateTime?>
        {
            { b, null },                            // acquired exactly at b
            { null, b },                            // disposed exactly at b
            { b.AddDays(-1), b.AddDays(-1) },       // acquired == disposed, before b
            { b, b },                               // acquired == disposed, at b
            { b.AddDays(1), b.AddDays(1) },         // acquired == disposed, after b
            { null, null },                         // both null
            { b.AddDays(-1), null },                // disposal null on its own
            { null, b.AddDays(1) },                 // acquisition null on its own
            { b.AddDays(1), null },                 // not yet acquired
            { null, b.AddDays(-1) },                // already disposed
            { b.AddDays(-1), b.AddDays(1) },        // held
        };
    }

    [Theory]
    [MemberData(nameof(MembershipCases))]
    public async Task TheTranslatableRule_AndTheInMemoryRule_Agree(DateTime? acquired, DateTime? disposed)
    {
        var b = Utc(2026, 3, 1);
        await using var context = TestContextFactory.Create();
        var property = NewProperty("Subject", acquired: acquired, disposed: disposed);
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        var viaQuery = await context.Properties.Where(PropertyMembership.HeldAt(b)).AnyAsync();
        var viaFold = PropertyMembership.IsHeldAt(acquired, disposed, b);

        Assert.Equal(viaQuery, viaFold);
    }

    [Fact]
    public void TheFirstContributionRule_ExcludesContributionAtDisposalOrNow()
    {
        var now = Utc(2026, 6, 1);
        var disposed = Utc(2026, 3, 1);

        Assert.Null(PropertyMembership.FirstContribution(null, disposed, disposed, now));       // c == DisposedDate
        Assert.Null(PropertyMembership.FirstContribution(null, null, now, now));                 // c == now
        Assert.Null(PropertyMembership.FirstContribution(disposed, disposed, Utc(2025, 1, 1), now)); // acquired == disposed
        Assert.Null(PropertyMembership.FirstContribution(Utc(2025, 1, 1), null, null, now));    // never estimated
        Assert.Equal(Utc(2025, 2, 1), PropertyMembership.FirstContribution(Utc(2025, 2, 1), null, Utc(2025, 1, 1), now));
        Assert.Equal(Utc(2025, 1, 1), PropertyMembership.FirstContribution(null, disposed, Utc(2025, 1, 1), now));
    }

    // ── Poisoned context (the DataExportStreamingTests pattern) ───────────────────────────────

    private static async Task<string> SeedPoisonStoreAsync()
    {
        var store = Guid.NewGuid().ToString();
        await using var seed = new OdysseyContext(StoreOptions(store).Options);
        seed.Currencies.Add(new Currency { CurrencyCode = "USD", Name = "US Dollar", MinorUnits = 2, Symbol = "$" });
        var checking = NewAccount("Checking", AccountType.CheckingAccount, "USD", Utc(2024, 1, 1));
        seed.Accounts.Add(checking);
        seed.Transactions.Add(NewTransaction(checking, 1000m, Utc(2024, 2, 1)));
        var house = NewProperty("House");
        seed.Properties.Add(house);
        seed.PropertyEstimates.Add(NewEstimate(house, 5m, Utc(2025, 1, 1)));
        await seed.SaveChangesAsync();
        return store;
    }

    private static DbContextOptionsBuilder<OdysseyContext> StoreOptions(string store) =>
        new DbContextOptionsBuilder<OdysseyContext>()
            .UseInMemoryDatabase(store)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

    /// <remarks>
    /// The <see cref="PoisonedContext{TEntity}"/> subclass is load-bearing: the compiled-query cache is
    /// keyed partly on the context type and <c>QueryCompilationStarting</c> fires only on a cache miss,
    /// so on the shared <see cref="OdysseyContext"/> a sibling test that had already compiled the
    /// property query would make the negative assertion pass vacuously.
    /// </remarks>
    private static OdysseyContext Poisoned<TEntity>(string store) =>
        new PoisonedContext<TEntity>(StoreOptions(store).AddInterceptors(new FailOnQueriesOf(typeof(TEntity))).Options);

    private sealed class PoisonedContext<TEntity>(DbContextOptions<OdysseyContext> options) : OdysseyContext(options);

    private sealed class FailOnQueriesOf(Type entityType) : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) =>
            RootFinder.Queries(queryExpression, entityType)
                ? throw new InvalidOperationException($"Poisoned {entityType.Name} query.")
                : queryExpression;

        private sealed class RootFinder(Type entityType) : ExpressionVisitor
        {
            private bool found;

            public static bool Queries(Expression expression, Type entityType)
            {
                var finder = new RootFinder(entityType);
                finder.Visit(expression);
                return finder.found;
            }

            public override Expression? Visit(Expression? node)
            {
                if (node is EntityQueryRootExpression { EntityType.ClrType: var clrType } && clrType == entityType)
                {
                    found = true;
                }

                return base.Visit(node);
            }
        }
    }
}
