using Odyssey.Core;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;
using Xunit;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

public class ExchangeRateServiceTests
{
    [Fact]
    public async Task Create_PersistsRate_WithAsOfAndCreatedAt()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var asOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var created = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "usd",
            ToCurrencyCode = "eur",
            Rate = 0.92m,
            AsOf = asOf,
        });

        Assert.Equal("USD", created.FromCurrencyCode);
        Assert.Equal("EUR", created.ToCurrencyCode);
        Assert.Equal(0.92m, created.Rate);
        Assert.Equal(asOf, created.AsOf);
        Assert.NotEqual(default, created.CreatedAt);

        var fetched = await service.Get(created.ExchangeRateId);
        Assert.NotNull(fetched);
        Assert.Equal(0.92m, fetched!.Rate);
    }

    [Fact]
    public async Task Create_DefaultsAsOf_ToNow_WhenOmitted()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var before = DateTime.UtcNow;
        var created = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "SEK",
            Rate = 10.5m,
        });

        Assert.InRange(created.AsOf, before.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Create_WithEqualFromAndTo_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "usd",
            Rate = 1m,
        }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_WithNonPositiveRate_Throws(decimal rate)
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = rate,
        }));
    }

    [Fact]
    public async Task Create_WithUnknownCurrency_Throws()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "ZZZ",
            Rate = 1m,
        }));
    }

    [Fact]
    public async Task GetLatest_ReturnsNewestByAsOf_AndRowsPersist()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var older = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.90m,
            AsOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var newer = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.95m,
            AsOf = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var latest = await service.GetLatest("USD", "EUR");

        Assert.NotNull(latest);
        Assert.Equal(0.95m, latest!.Rate);

        // The older row still exists — Create never overwrites a prior entry.
        Assert.NotNull(await service.Get(older.ExchangeRateId));
        Assert.NotNull(await service.Get(newer.ExchangeRateId));
    }

    [Fact]
    public async Task SearchFor_ReturnsNewestFirst()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 0.90m,
            AsOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD", ToCurrencyCode = "SEK", Rate = 10m,
            AsOf = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var results = (await service.ListAsync(new ExchangeRatesQueryParams())).Items;

        Assert.Equal(2, results.Count);
        Assert.True(results[0].AsOf >= results[1].AsOf);
    }

    // ── ListAsync (issue #277): derived current/historical filter + status/pair sort ──

    private static NewExchangeRate Rate(string from, string to, decimal rate, DateTime asOf) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
    };

    private static DateTime Utc(int month) => new(2026, month, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ListAsync_ReturnsPagedEnvelope_WithTotalCount()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);
        await service.Create(Rate("USD", "EUR", 0.9m, Utc(1)));
        await service.Create(Rate("USD", "SEK", 10m, Utc(1)));

        var page = await service.ListAsync(new ExchangeRatesQueryParams());

        Assert.Equal(0, page.Offset);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task ListAsync_StatusCurrent_KeepsOnlyNewestPerDirectedPair()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);
        var supersededUsdEur = await service.Create(Rate("USD", "EUR", 0.90m, Utc(1)));
        var currentUsdEur = await service.Create(Rate("USD", "EUR", 0.95m, Utc(3)));
        var onlyUsdSek = await service.Create(Rate("USD", "SEK", 10m, Utc(2)));

        var page = await service.ListAsync(new ExchangeRatesQueryParams { Status = ExchangeRateStatus.Current });

        Assert.Equal(
            new[] { currentUsdEur.ExchangeRateId, onlyUsdSek.ExchangeRateId }.OrderBy(id => id),
            page.Items.Select(r => r.ExchangeRateId).OrderBy(id => id));
        Assert.DoesNotContain(page.Items, r => r.ExchangeRateId == supersededUsdEur.ExchangeRateId);
    }

    [Fact]
    public async Task ListAsync_StatusHistorical_KeepsOnlySupersededRates()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);
        var superseded = await service.Create(Rate("USD", "EUR", 0.90m, Utc(1)));
        await service.Create(Rate("USD", "EUR", 0.95m, Utc(3)));   // current for USD→EUR
        await service.Create(Rate("USD", "SEK", 10m, Utc(2)));     // current (only one for its pair)

        var page = await service.ListAsync(new ExchangeRatesQueryParams { Status = ExchangeRateStatus.Historical });

        Assert.Equal(superseded.ExchangeRateId, Assert.Single(page.Items).ExchangeRateId);
    }

    [Fact]
    public async Task ListAsync_SortByStatus_OrdersCurrentBeforeHistorical()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);
        var historical = await service.Create(Rate("USD", "EUR", 0.90m, Utc(1)));
        var current = await service.Create(Rate("USD", "EUR", 0.95m, Utc(3)));

        var asc = await service.ListAsync(new ExchangeRatesQueryParams
        {
            SortBy = ExchangeRateSortBy.Status,
            SortDir = SortDirection.Asc,
        });

        // Current (no newer rate exists) sorts before historical, matching the client's status order.
        Assert.Equal(
            new[] { current.ExchangeRateId, historical.ExchangeRateId },
            asc.Items.Select(r => r.ExchangeRateId));
    }

    [Fact]
    public async Task ListAsync_SortByPair_OrdersByFromThenToCurrencyCode()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);
        await service.Create(Rate("USD", "SEK", 10m, Utc(1)));
        await service.Create(Rate("EUR", "USD", 1.1m, Utc(1)));
        await service.Create(Rate("USD", "EUR", 0.9m, Utc(1)));

        var asc = await service.ListAsync(new ExchangeRatesQueryParams
        {
            SortBy = ExchangeRateSortBy.Pair,
            SortDir = SortDirection.Asc,
        });

        Assert.Equal(
            new[] { ("EUR", "USD"), ("USD", "EUR"), ("USD", "SEK") },
            asc.Items.Select(r => (r.FromCurrencyCode, r.ToCurrencyCode)));
    }

    [Fact]
    public async Task ListAsync_ToCurrenciesFilter_MatchesTargetCodes()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);
        await service.Create(Rate("USD", "EUR", 0.9m, Utc(1)));
        await service.Create(Rate("USD", "SEK", 10m, Utc(1)));

        var page = await service.ListAsync(new ExchangeRatesQueryParams { ToCurrencies = ["EUR"] });

        Assert.Equal("EUR", Assert.Single(page.Items).ToCurrencyCode);
    }

    // ── Update (design-system update: in-place Rate/AsOf correction, pair locked) ──

    [Fact]
    public async Task Update_CorrectsRateAndAsOf()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var created = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.90m,
            AsOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var newAsOf = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var updated = await service.Update(created.ExchangeRateId, new UpdateExchangeRate { Rate = 0.93m, AsOf = newAsOf });

        Assert.NotNull(updated);
        Assert.Equal(0.93m, updated!.Rate);
        Assert.Equal(newAsOf, updated.AsOf);
        // Identity is untouched by the update DTO, which doesn't carry From/To.
        Assert.Equal("USD", updated.FromCurrencyCode);
        Assert.Equal("EUR", updated.ToCurrencyCode);

        var fetched = await service.Get(created.ExchangeRateId);
        Assert.Equal(0.93m, fetched!.Rate);
    }

    [Fact]
    public async Task Update_MovingAsOfPastAnotherRate_BecomesCurrent()
    {
        // Editing AsOf is the actually-new domain risk this design-system update introduced (the old
        // append-only model couldn't change which row conversions pick) — correcting the older row's
        // AsOf to be newer than the other row must flip which one GetLatest/ListAsync(Current) return.
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var older = await service.Create(Rate("USD", "EUR", 0.90m, Utc(1)));
        var newer = await service.Create(Rate("USD", "EUR", 0.95m, Utc(3)));

        var correctedAsOf = Utc(6);
        var updated = await service.Update(older.ExchangeRateId, new UpdateExchangeRate { Rate = 0.99m, AsOf = correctedAsOf });
        Assert.NotNull(updated);

        var latest = await service.GetLatest("USD", "EUR");
        Assert.NotNull(latest);
        Assert.Equal(older.ExchangeRateId, latest!.ExchangeRateId);
        Assert.Equal(0.99m, latest.Rate);

        var currentPage = await service.ListAsync(new ExchangeRatesQueryParams { Status = ExchangeRateStatus.Current });
        Assert.Equal(older.ExchangeRateId, Assert.Single(currentPage.Items).ExchangeRateId);

        var historicalPage = await service.ListAsync(new ExchangeRatesQueryParams { Status = ExchangeRateStatus.Historical });
        Assert.Equal(newer.ExchangeRateId, Assert.Single(historicalPage.Items).ExchangeRateId);
    }

    [Fact]
    public async Task Update_WithSameAsOfAsAnotherRate_BreaksTieByUpdatedAt()
    {
        // Two rates sharing an AsOf break the tie on (UpdatedAt ?? CreatedAt) — correcting the
        // earlier-created row (without touching AsOf) must still make it win the tiebreak, since
        // its UpdatedAt is now newer than the other row's CreatedAt. Before UpdatedAt existed, this
        // correction would have silently lost to the later-inserted row (both tied on CreatedAt order).
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var sameAsOf = Utc(2);
        var createdFirst = await service.Create(Rate("USD", "EUR", 0.90m, sameAsOf));
        var createdSecond = await service.Create(Rate("USD", "EUR", 0.91m, sameAsOf));

        var updated = await service.Update(createdFirst.ExchangeRateId, new UpdateExchangeRate { Rate = 0.99m, AsOf = sameAsOf });
        Assert.NotNull(updated);

        var latest = await service.GetLatest("USD", "EUR");
        Assert.NotNull(latest);
        Assert.Equal(createdFirst.ExchangeRateId, latest!.ExchangeRateId);
        Assert.Equal(0.99m, latest.Rate);

        var currentPage = await service.ListAsync(new ExchangeRatesQueryParams { Status = ExchangeRateStatus.Current });
        Assert.Equal(createdFirst.ExchangeRateId, Assert.Single(currentPage.Items).ExchangeRateId);
        Assert.DoesNotContain(currentPage.Items, r => r.ExchangeRateId == createdSecond.ExchangeRateId);
    }

    [Fact]
    public async Task Update_SetsUpdatedAt()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var created = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.90m,
            AsOf = Utc(1),
        });
        Assert.Null(created.UpdatedAt);

        var before = DateTime.UtcNow;
        var updated = await service.Update(created.ExchangeRateId, new UpdateExchangeRate { Rate = 0.91m, AsOf = Utc(1) });

        Assert.NotNull(updated);
        Assert.NotNull(updated!.UpdatedAt);
        Assert.InRange(updated.UpdatedAt!.Value, before.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        // CreatedAt is untouched by a correction — it stays the original insertion time.
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Update_WithUnknownId_ReturnsNull()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var updated = await service.Update(Guid.NewGuid(), new UpdateExchangeRate { Rate = 1m, AsOf = DateTime.UtcNow });

        Assert.Null(updated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Update_WithNonPositiveRate_Throws(decimal rate)
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var created = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.90m,
        });

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Update(created.ExchangeRateId, new UpdateExchangeRate { Rate = rate, AsOf = DateTime.UtcNow }));
    }

    [Fact]
    public async Task Delete_RemovesRate()
    {
        await using var context = TestContextFactory.Create();
        var service = new ExchangeRateService(context);

        var created = await service.Create(new NewExchangeRate
        {
            FromCurrencyCode = "USD",
            ToCurrencyCode = "EUR",
            Rate = 0.92m,
        });

        await service.Delete(created.ExchangeRateId);

        Assert.Null(await service.Get(created.ExchangeRateId));
        Assert.Null(await service.GetLatest("USD", "EUR"));
    }
}
