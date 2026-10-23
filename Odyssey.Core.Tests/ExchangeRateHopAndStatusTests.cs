using System.Text.RegularExpressions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The conversion and status rules issue #349 §3 changed: the NOK hop and its same-date tie, the
/// future-dated <c>Upcoming</c> rows, the fetched-row write rules, the summary, and the one-ordering
/// source-lint.
/// </summary>
public class ExchangeRateHopAndStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ExchangeRateSyncTests.MutableClock Clock = new(Now);

    private static OdysseyContext Create()
    {
        var context = TestContextFactory.Create();
        context.Currencies.Add(new Currency { CurrencyCode = "NOK", Name = "Norwegian Krone", MinorUnits = 2, Symbol = "kr" });
        context.SaveChanges();
        return context;
    }

    private static ExchangeRate Rate(string from, string to, decimal rate, DateTime asOf, bool fetched = false) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = Now.AddDays(-30),
        Provider = fetched ? ExchangeRateProvider.NorgesBank : null,
    };

    [Fact]
    public async Task A_pair_with_no_direct_rate_converts_through_NOK_unrounded()
    {
        await using var context = Create();
        context.ExchangeRates.AddRange(
            Rate("USD", "NOK", 10.5m, Now.AddDays(-1), fetched: true),
            Rate("NOK", "EUR", 0.08547009m, Now.AddDays(-1), fetched: true));
        await context.SaveChangesAsync();
        var conversion = new CurrencyConversionService(context, Clock);

        Assert.Equal(100m * 10.5m * 0.08547009m, await conversion.ConvertAsync(100m, "USD", "EUR"));
        Assert.Equal(10.5m * 0.08547009m, (await conversion.GetLatestRatesToAsync("EUR", ["USD"]))["USD"]);
    }

    [Fact]
    public async Task A_stale_direct_rate_loses_to_a_newer_hop_and_a_same_day_one_wins()
    {
        await using var context = Create();
        context.ExchangeRates.AddRange(
            Rate("USD", "EUR", 0.5m, Now.AddDays(-10)),
            Rate("USD", "NOK", 10m, Now.AddDays(-1), fetched: true),
            Rate("NOK", "EUR", 0.1m, Now.AddDays(-1), fetched: true));
        await context.SaveChangesAsync();
        var conversion = new CurrencyConversionService(context, Clock);

        Assert.Equal(1m, (await conversion.GetLatestRatesToAsync("EUR", ["USD"]))["USD"]);

        // Same UTC date as the hop's OLDER leg: the direct rate wins the tie.
        context.ExchangeRates.Add(Rate("USD", "EUR", 0.7m, Now.AddDays(-1).Date.AddHours(1)));
        await context.SaveChangesAsync();
        Assert.Equal(0.7m, (await conversion.GetLatestRatesToAsync("EUR", ["USD"]))["USD"]);
    }

    [Fact]
    public async Task The_hop_takes_effect_from_its_older_leg()
    {
        await using var context = Create();
        context.ExchangeRates.AddRange(
            Rate("USD", "EUR", 0.5m, Now.AddDays(-5)),
            Rate("USD", "NOK", 10m, Now.AddDays(-1), fetched: true),
            // The older leg is older than the direct rate, so the hop is the staler candidate.
            Rate("NOK", "EUR", 0.1m, Now.AddDays(-8), fetched: true));
        await context.SaveChangesAsync();

        Assert.Equal(0.5m, (await new CurrencyConversionService(context, Clock).GetLatestRatesToAsync("EUR", ["USD"]))["USD"]);
    }

    [Fact]
    public async Task A_future_dated_rate_is_ignored_until_its_date_and_lists_as_Upcoming()
    {
        await using var context = Create();
        context.ExchangeRates.AddRange(
            Rate("USD", "EUR", 0.9m, Now.AddDays(-1)),
            Rate("USD", "EUR", 0.1m, Now.AddDays(3)));
        await context.SaveChangesAsync();

        Assert.Equal(90m, await new CurrencyConversionService(context, Clock).ConvertAsync(100m, "USD", "EUR"));

        var service = new ExchangeRateService(context, Clock);
        Assert.Equal(0.9m, (await service.GetLatest("USD", "EUR"))!.Rate);
        var page = await service.ListAsync(new ExchangeRatesQueryParams());
        Assert.Equal(
            [ExchangeRateStatus.Upcoming, ExchangeRateStatus.Current],
            page.Items.OrderByDescending(r => r.AsOf).Select(r => r.Status));

        var upcoming = await service.ListAsync(new ExchangeRatesQueryParams { Status = [ExchangeRateStatus.Upcoming] });
        Assert.Equal(0.1m, Assert.Single(upcoming.Items).Rate);
        var current = await service.ListAsync(new ExchangeRatesQueryParams { Status = [ExchangeRateStatus.Current, ExchangeRateStatus.Current] });
        Assert.Equal(0.9m, Assert.Single(current.Items).Rate);
        Assert.Empty((await service.ListAsync(new ExchangeRatesQueryParams { Status = [(ExchangeRateStatus)99] })).Items);
    }

    [Fact]
    public async Task The_timeline_switches_between_direct_and_hop_as_the_series_change()
    {
        await using var context = Create();
        var window = Now.AddDays(-20);
        context.ExchangeRates.AddRange(
            Rate("USD", "EUR", 0.5m, window.AddDays(-5)),
            Rate("USD", "NOK", 10m, window.AddDays(2), fetched: true),
            Rate("NOK", "EUR", 0.1m, window.AddDays(3), fetched: true),
            Rate("USD", "EUR", 0.7m, window.AddDays(6)));
        await context.SaveChangesAsync();

        var timeline = await new CurrencyConversionService(context, Clock).GetRateTimelineToAsync("EUR", ["USD"], window, Now);

        Assert.Equal(
            [(window.AddDays(-5), 0.5m), (window.AddDays(3), 1m), (window.AddDays(6), 0.7m)],
            timeline["USD"].Select(point => (point.AsOf, point.Rate)));
    }

    [Fact]
    public async Task A_fetched_rate_is_never_corrected_in_place_and_needs_the_settings_claim_to_delete()
    {
        await using var context = Create();
        var fetched = Rate("USD", "NOK", 10m, Now.AddDays(-1), fetched: true);
        var manual = Rate("USD", "NOK", 11m, Now.AddDays(-2));
        context.ExchangeRates.AddRange(fetched, manual);
        await context.SaveChangesAsync();
        var service = new ExchangeRateService(context, Clock);

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            service.Update(fetched.ExchangeRateId, new UpdateExchangeRate { Rate = 9m, AsOf = Now }));
        Assert.True(refused.Errors!.ContainsKey("source"));
        await Assert.ThrowsAsync<DomainForbiddenException>(() => service.Delete(fetched.ExchangeRateId, mayDeleteFetched: false));

        await service.Delete(manual.ExchangeRateId, mayDeleteFetched: false);
        await service.Delete(fetched.ExchangeRateId, mayDeleteFetched: true);
        Assert.Empty(context.ExchangeRates);
    }

    [Fact]
    public async Task The_source_filter_and_the_summary_read_the_provider_column()
    {
        await using var context = Create();
        context.ExchangeRates.AddRange(
            Rate("USD", "NOK", 10m, Now.AddDays(-2), fetched: true),
            Rate("USD", "NOK", 10.2m, Now.AddDays(-1), fetched: true),
            Rate("NOK", "USD", 0.1m, Now.AddDays(-1), fetched: true),
            Rate("USD", "EUR", 0.9m, Now.AddDays(-1)),
            Rate("USD", "EUR", 0.8m, Now.AddDays(2)));
        await context.SaveChangesAsync();
        var service = new ExchangeRateService(context, Clock);

        var manual = await service.ListAsync(new ExchangeRatesQueryParams { Source = [ExchangeRateSource.Manual] });
        Assert.All(manual.Items, item => Assert.Equal(ExchangeRateSource.Manual, item.Source));
        Assert.Equal(2, manual.TotalCount);

        var summary = await service.GetSummaryAsync();
        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(new ExchangeRateStatusCounts { Current = 3, Historical = 1, Upcoming = 1 }, summary.ByStatus);
        Assert.Equal(new ExchangeRateSourceCounts { Manual = 2, Fetched = 3 }, summary.BySource);
        Assert.Equal(3, summary.PairCount);
        Assert.Equal(["NOK", "USD"], summary.FromCurrencyCodes);
        Assert.Equal("Source: Norges Bank (NLOD 2.0)", summary.Attribution);
        Assert.Equal(Now.AddDays(-1), summary.LatestAsOfUtc);
        Assert.Equal("Norwegian time", summary.FetchedRateTimeZoneLabel);
    }

    [Fact]
    public async Task The_summary_has_no_attribution_without_a_fetched_row()
    {
        await using var context = Create();

        var summary = await new ExchangeRateService(context, Clock).GetSummaryAsync();

        Assert.Null(summary.Attribution);
        Assert.Null(summary.LatestAsOfUtc);
    }

    /// <summary>
    /// The "newest exchange rate" ordering is written once (issue #349 §3, architect B2). A second copy
    /// would let a status rule and the conversion pick different rows as current.
    /// </summary>
    [Fact]
    public void No_file_in_Odyssey_Core_restates_the_exchange_rate_ordering()
    {
        var root = Path.Combine(SolutionRoot(), "Odyssey.Core");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.Contains(files, path => path.EndsWith("ExchangeRateOrdering.cs", StringComparison.Ordinal));

        var pattern = new Regex(@"(OrderByDescending|ThenByDescending)\(\s*\w+\s*=>\s*\w+\.(AsOf|UpdatedAt \?\? \w+\.CreatedAt)\s*\)");
        var offenders = files
            .Where(path => !path.EndsWith("ExchangeRateOrdering.cs", StringComparison.Ordinal))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Use ExchangeRateOrdering instead of restating the newest-rate ordering: " + string.Join(", ", offenders));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the solution root from the test binary.");
    }
}
