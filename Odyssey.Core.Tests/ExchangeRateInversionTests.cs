using System.Text.Json;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Release 1 of issue #472: every reader inverts FETCHED rows (never manual ones), a stored row beats an
/// inverted one on a full tie, and the badge, the status filter and sort, and the summary agree. AC numbers
/// refer to the spec's §12.
/// </summary>
public class ExchangeRateInversionTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D = Now.AddDays(-3);
    private static readonly Guid Low = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid High = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private readonly ExchangeRateSyncTests.MutableClock Clock = new(Now);

    private static OdysseyContext Create()
    {
        var context = TestContextFactory.Create();
        context.Currencies.AddRange(
            new Currency { CurrencyCode = "NOK", Name = "Norwegian Krone", MinorUnits = 2, Symbol = "kr" },
            new Currency { CurrencyCode = "IDR", Name = "Indonesian Rupiah", MinorUnits = 2, Symbol = "Rp" });
        context.SaveChanges();
        return context;
    }

    private static ExchangeRate Rate(
        string from, string to, decimal rate, DateTime asOf, bool fetched = false, Guid? id = null, DateTime? createdAt = null) => new()
        {
            ExchangeRateId = id ?? Guid.NewGuid(),
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            AsOf = asOf,
            CreatedAt = createdAt ?? Now.AddDays(-30),
            Provider = fetched ? ExchangeRateProvider.NorgesBank : null,
        };

    private async Task<OdysseyContext> Seed(params ExchangeRate[] rates)
    {
        var context = Create();
        context.ExchangeRates.AddRange(rates);
        await context.SaveChangesAsync();
        return context;
    }

    private CurrencyConversionService Conversion(OdysseyContext context) => new(context, Clock);

    private ExchangeRateService Service(OdysseyContext context) => new(context, Clock);

    private async Task<Dictionary<Guid, ExchangeRateStatus>> StatusesAsync(OdysseyContext context) =>
        (await Service(context).ListAsync(new ExchangeRatesQueryParams { Limit = 100 })).Items
            .ToDictionary(row => row.ExchangeRateId, row => row.Status);

    [Fact] // AC 1
    public async Task A_fetched_rate_converts_the_reverse_way_as_its_exact_reciprocal()
    {
        await using var context = await Seed(Rate("USD", "NOK", 10m, D, fetched: true));

        Assert.Equal(10m, await Conversion(context).ConvertAsync(100m, "NOK", "USD"));
    }

    [Fact] // AC 2
    public async Task A_hop_can_use_an_inverted_leg()
    {
        await using var context = await Seed(
            Rate("USD", "NOK", 9.3601m, D, fetched: true),
            Rate("EUR", "NOK", 10.488m, D, fetched: true));

        Assert.Equal(9.3601m * (1m / 10.488m), await Conversion(context).ConvertAsync(1m, "USD", "EUR"));
    }

    [Fact] // AC 3
    public void Every_published_fixture_value_fits_eight_decimals_after_scaling()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NorgesBank");
        var checkedCount = 0;
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var observation in NorgesBankClient.ParseObservations(document.RootElement))
            {
                var value = decimal.Parse(observation.Value!, System.Globalization.CultureInfo.InvariantCulture);
                var scaled = value;
                for (var i = 0; i < int.Parse(observation.UnitMult!, System.Globalization.CultureInfo.InvariantCulture); i++)
                {
                    scaled /= 10m;
                }

                Assert.True(Math.Round(scaled, 8) == scaled,
                    $"{observation.CurrencyCode} {observation.Date} needs more than 8 decimals after scaling.");
                checkedCount++;
            }
        }

        Assert.True(checkedCount > 0);
    }

    [Fact] // AC 4
    public async Task A_low_value_currency_inverts_exactly()
    {
        await using var context = await Seed(Rate("IDR", "NOK", 0.00051783m, D, fetched: true));

        Assert.Equal(1_000_000m / 0.00051783m, await Conversion(context).ConvertAsync(1_000_000m, "NOK", "IDR"));
    }

    [Fact] // AC 5
    public async Task A_manual_rate_is_never_inverted()
    {
        await using var context = await Seed(Rate("USD", "NOK", 10m, D));

        Assert.Null(await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        Assert.Null(await Service(context).GetLatest("NOK", "USD"));
        Assert.Null(await Service(context).ResolveAsync("NOK", "USD"));
    }

    [Fact] // AC 6, AC 18 (case 6)
    public async Task A_newer_manual_row_of_the_same_stored_pair_does_not_hide_the_fetched_one()
    {
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true);
        var manual = Rate("USD", "NOK", 12m, D.AddDays(1));
        await using var context = await Seed(fetched, manual);

        Assert.Equal(0.1m, await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        Assert.Equal(12m, await Conversion(context).ConvertAsync(1m, "USD", "NOK"));

        var statuses = await StatusesAsync(context);
        Assert.Equal(ExchangeRateStatus.Current, statuses[manual.ExchangeRateId]);
        // Its inverse wins NOK → USD, but that never makes it Current inside its own pair (§8.12).
        Assert.Equal(ExchangeRateStatus.Historical, statuses[fetched.ExchangeRateId]);
        await AssertStatusSurfacesAgree(context);
    }

    [Fact] // AC 7
    public async Task A_newer_fetched_row_makes_an_older_manual_reverse_row_Historical()
    {
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true);
        var manual = Rate("NOK", "USD", 0.2m, D.AddDays(-1));
        await using var context = await Seed(fetched, manual);

        Assert.Equal(0.1m, await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        var statuses = await StatusesAsync(context);
        Assert.Equal(ExchangeRateStatus.Historical, statuses[manual.ExchangeRateId]);
        Assert.Equal(ExchangeRateStatus.Current, statuses[fetched.ExchangeRateId]);
        await AssertStatusSurfacesAgree(context);
    }

    [Fact] // AC 8
    public async Task A_newer_manual_reverse_row_wins_and_stays_Current()
    {
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true);
        var manual = Rate("NOK", "USD", 0.2m, D.AddDays(1));
        await using var context = await Seed(fetched, manual);

        Assert.Equal(0.2m, await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        Assert.Equal(ExchangeRateStatus.Current, (await StatusesAsync(context))[manual.ExchangeRateId]);
        await AssertStatusSurfacesAgree(context);
    }

    [Theory] // AC 9
    [InlineData(true)]
    [InlineData(false)]
    public async Task On_a_full_tie_the_stored_row_wins_whatever_the_ids(bool storedHasLowerId)
    {
        var created = Now.AddDays(-2);
        var manual = Rate("NOK", "USD", 0.2m, D, id: storedHasLowerId ? Low : High, createdAt: created);
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true, id: storedHasLowerId ? High : Low, createdAt: created);
        await using var context = await Seed(manual, fetched);

        Assert.Equal(0.2m, await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        var latest = await Service(context).GetLatest("NOK", "USD");
        Assert.False(latest!.Inverted);
        Assert.Equal(manual.ExchangeRateId, latest.ExchangeRateId);
        Assert.Equal(ExchangeRateStatus.Current, (await StatusesAsync(context))[manual.ExchangeRateId]);
        await AssertStatusSurfacesAgree(context);
    }

    [Fact] // AC 9, first half
    public async Task On_the_same_AsOf_the_later_tiebreak_wins()
    {
        var manual = Rate("NOK", "USD", 0.2m, D, createdAt: Now.AddDays(-5));
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true, createdAt: Now.AddDays(-4));
        await using var context = await Seed(manual, fetched);

        Assert.Equal(0.1m, await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        Assert.True((await Service(context).GetLatest("NOK", "USD"))!.Inverted);
        Assert.Equal(ExchangeRateStatus.Historical, (await StatusesAsync(context))[manual.ExchangeRateId]);
        await AssertStatusSurfacesAgree(context);
    }

    [Fact] // AC 10
    public async Task The_timeline_carries_an_inverted_row_in_from_before_the_window()
    {
        var window = Now.AddDays(-10);
        await using var context = await Seed(
            Rate("USD", "NOK", 8m, window.AddDays(-20), fetched: true),
            Rate("USD", "NOK", 10m, window.AddDays(-2), fetched: true));

        var timeline = await Conversion(context).GetRateTimelineToAsync("USD", ["NOK"], window, Now);

        var point = Assert.Single(timeline["NOK"]);
        Assert.Equal(0.1m, point.Rate);
        Assert.Equal(window.AddDays(-2), point.AsOf);
    }

    [Fact] // AC 10, cases 6 and 8 over time
    public async Task The_timeline_switches_between_inverted_and_stored_reverse_rows()
    {
        var window = Now.AddDays(-10);
        await using var context = await Seed(
            Rate("USD", "NOK", 10m, window.AddDays(1), fetched: true),
            Rate("NOK", "USD", 0.2m, window.AddDays(3)),
            Rate("USD", "NOK", 8m, window.AddDays(5), fetched: true));

        var points = (await Conversion(context).GetRateTimelineToAsync("USD", ["NOK"], window, Now))["NOK"];

        Assert.Equal([0.1m, 0.2m, 0.125m], points.Select(p => p.Rate));
    }

    [Fact] // AC 11
    public async Task The_timeline_and_the_latest_lookup_agree_at_every_instant()
    {
        var window = Now.AddDays(-10);
        var rates = new[]
        {
            Rate("USD", "NOK", 10m, window.AddDays(-3), fetched: true),
            Rate("EUR", "NOK", 11m, window.AddDays(1), fetched: true),
            Rate("NOK", "USD", 0.2m, window.AddDays(2)),
            Rate("USD", "NOK", 9m, window.AddDays(4), fetched: true),
            Rate("USD", "EUR", 0.95m, window.AddDays(6)),
            Rate("EUR", "NOK", 12m, window.AddDays(8), fetched: true),
        };
        await using var context = await Seed(rates);
        var conversion = Conversion(context);

        foreach (var (from, to) in new[] { ("NOK", "USD"), ("USD", "EUR"), ("EUR", "USD"), ("NOK", "EUR") })
        {
            var timeline = (await conversion.GetRateTimelineToAsync(to, [from], window, Now))
                .GetValueOrDefault(from) ?? [];
            for (var instant = window; instant < Now; instant = instant.AddHours(12))
            {
                var expected = timeline.LastOrDefault(p => p.AsOf < instant);
                var latest = (await conversion.GetLatestRatesToAsync(to, [from], instant)).GetValueOrDefault(from);
                Assert.True(expected.Rate == latest, $"{from} → {to} at {instant:O}: timeline {expected.Rate}, latest {latest}");
            }
        }
    }

    [Fact] // AC 12
    public async Task A_zero_rate_is_ignored_by_every_reader()
    {
        await using var context = await Seed(
            Rate("USD", "NOK", 0m, D, fetched: true),
            Rate("NOK", "EUR", 0m, D));

        Assert.Null(await Conversion(context).ConvertAsync(1m, "NOK", "USD"));
        Assert.Null(await Conversion(context).ConvertAsync(1m, "NOK", "EUR"));
        Assert.Empty(await Conversion(context).GetRateTimelineToAsync("USD", ["NOK"], D.AddDays(-1), Now));
        Assert.Null(await Service(context).GetLatest("NOK", "USD"));
        Assert.Null(await Service(context).ResolveAsync("USD", "EUR"));
        await Service(context).GetSummaryAsync();
    }

    [Fact] // AC 14
    public async Task Latest_returns_an_inverted_fetched_row_for_the_reverse_pair()
    {
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true);
        await using var context = await Seed(fetched);

        var latest = (await Service(context).GetLatest("nok", "usd"))!;

        Assert.True(latest.Inverted);
        Assert.Equal(("NOK", "USD", 0.1m), (latest.FromCurrencyCode, latest.ToCurrencyCode, latest.Rate));
        Assert.Equal(fetched.ExchangeRateId, latest.ExchangeRateId);
        Assert.Equal(fetched.AsOf, latest.AsOf);
        Assert.Equal(fetched.CreatedAt, latest.CreatedAt);
        Assert.Equal((ExchangeRateSource.Fetched, ExchangeRateStatus.Current), (latest.Source, latest.Status));
    }

    [Fact] // AC 15
    public async Task Latest_prefers_a_newer_manual_reverse_row()
    {
        var manual = Rate("NOK", "USD", 0.2m, D.AddDays(1));
        await using var context = await Seed(Rate("USD", "NOK", 10m, D, fetched: true), manual);

        var latest = (await Service(context).GetLatest("NOK", "USD"))!;

        Assert.False(latest.Inverted);
        Assert.Equal(manual.ExchangeRateId, latest.ExchangeRateId);
    }

    [Fact] // AC 16, AC 26
    public async Task Latest_never_hops_and_is_null_for_the_same_currency_or_an_unknown_one()
    {
        await using var context = await Seed(
            Rate("USD", "NOK", 10m, D, fetched: true),
            Rate("EUR", "NOK", 11m, D, fetched: true));
        var service = Service(context);

        Assert.Null(await service.GetLatest("USD", "EUR"));
        Assert.Null(await service.GetLatest("NOK", "NOK"));
        Assert.Null(await service.GetLatest("XXX", "NOK"));
    }

    [Fact] // AC 17
    public async Task Every_other_reader_reports_inverted_false()
    {
        var fetched = Rate("USD", "NOK", 10m, D, fetched: true);
        await using var context = await Seed(fetched);
        var service = Service(context);

        Assert.All((await service.ListAsync(new ExchangeRatesQueryParams())).Items, row => Assert.False(row.Inverted));
        Assert.False((await service.Get(fetched.ExchangeRateId))!.Inverted);
        Assert.False((await service.GetLatest("USD", "NOK"))!.Inverted);
    }

    [Fact] // AC 19, AC 20
    public async Task Resolve_reports_the_method_and_legs_the_conversion_uses()
    {
        var usd = Rate("USD", "NOK", 9.3601m, D, fetched: true);
        var eur = Rate("EUR", "NOK", 10.488m, D.AddDays(-1), fetched: true);
        var manual = Rate("SEK", "NOK", 0.95m, D);
        await using var context = await Seed(usd, eur, manual);
        var service = Service(context);
        var conversion = Conversion(context);

        var direct = (await service.ResolveAsync("SEK", "NOK"))!;
        Assert.Equal(ExchangeRateResolutionMethod.Direct, direct.Method);
        Assert.Equal(ExchangeRateSource.Manual, Assert.Single(direct.Legs).Source);

        var inverted = (await service.ResolveAsync("NOK", "USD"))!;
        Assert.Equal(ExchangeRateResolutionMethod.Inverted, inverted.Method);
        var leg = Assert.Single(inverted.Legs);
        Assert.Equal((usd.ExchangeRateId, "NOK", "USD", true), (leg.ExchangeRateId, leg.FromCurrencyCode, leg.ToCurrencyCode, leg.Inverted));

        var hop = (await service.ResolveAsync("USD", "EUR"))!;
        Assert.Equal(ExchangeRateResolutionMethod.ViaNok, hop.Method);
        Assert.Equal([usd.ExchangeRateId, eur.ExchangeRateId], hop.Legs.Select(l => l.ExchangeRateId));
        Assert.Equal([false, true], hop.Legs.Select(l => l.Inverted));
        Assert.Equal(eur.AsOf, hop.AsOf); // the older leg (AC 20)
        Assert.Equal((await conversion.GetLatestRatesToAsync("EUR", ["USD"]))["USD"], hop.Rate);
        Assert.Equal(9.3601m * (1m / 10.488m), hop.Rate);

        var same = (await service.ResolveAsync("NOK", "NOK"))!;
        Assert.Equal((ExchangeRateResolutionMethod.SameCurrency, 1m, 0), (same.Method, same.Rate, same.Legs.Count));
        Assert.Null(same.AsOf);

        foreach (var (from, to) in new[] { ("SEK", "NOK"), ("NOK", "USD") })
        {
            Assert.Equal((await conversion.GetLatestRatesToAsync(to, [from]))[from], (await service.ResolveAsync(from, to))!.Rate);
        }
    }

    [Fact] // AC 26
    public async Task Resolve_needs_both_currencies_to_exist_and_accepts_an_archived_one()
    {
        await using var context = await Seed(Rate("USD", "NOK", 10m, D, fetched: true));
        var usd = context.Currencies.Single(c => c.CurrencyCode == "USD");
        usd.Archived = Now.AddDays(-1);
        await context.SaveChangesAsync();
        var service = Service(context);

        Assert.Null(await service.ResolveAsync("XXX", "XXX"));
        Assert.Null(await service.ResolveAsync("XXX", "NOK"));
        Assert.Equal(ExchangeRateResolutionMethod.Inverted, (await service.ResolveAsync("NOK", "USD"))!.Method);
        Assert.True((await service.GetLatest("NOK", "USD"))!.Inverted);
    }

    [Theory] // AC 25
    [InlineData(true)]
    [InlineData(false)]
    public async Task While_both_directions_are_stored_every_reader_matches_the_stored_rows(bool storedHasLowerId)
    {
        // The release-1 sync: one observation, two rows, one save, so equal AsOf and CreatedAt.
        var created = Now.AddDays(-2);
        var toHub = Rate("USD", "NOK", 9.3601m, D, fetched: true, id: storedHasLowerId ? Low : High, createdAt: created);
        var fromHub = Rate("NOK", "USD", 0.10683647m, D, fetched: true, id: storedHasLowerId ? High : Low, createdAt: created);
        var eurToHub = Rate("EUR", "NOK", 10.488m, D, fetched: true, createdAt: created);
        var eurFromHub = Rate("NOK", "EUR", 0.09534706m, D, fetched: true, createdAt: created);
        await using var context = await Seed(toHub, fromHub, eurToHub, eurFromHub);
        var conversion = Conversion(context);
        var service = Service(context);

        // The stored, rounded NOK → X rows win — the numbers the build without inversion produced.
        Assert.Equal(0.10683647m, await conversion.ConvertAsync(1m, "NOK", "USD"));
        Assert.Equal(9.3601m, await conversion.ConvertAsync(1m, "USD", "NOK"));
        Assert.Equal(9.3601m * 0.09534706m, await conversion.ConvertAsync(1m, "USD", "EUR"));
        Assert.Equal(0.10683647m,
            Assert.Single((await conversion.GetRateTimelineToAsync("USD", ["NOK"], D.AddDays(-1), Now))["NOK"]).Rate);

        var latest = (await service.GetLatest("NOK", "USD"))!;
        Assert.Equal((false, fromHub.ExchangeRateId), (latest.Inverted, latest.ExchangeRateId));
        Assert.Equal(ExchangeRateResolutionMethod.Direct, (await service.ResolveAsync("NOK", "USD"))!.Method);

        Assert.All(await StatusesAsync(context), pair => Assert.Equal(ExchangeRateStatus.Current, pair.Value));
        var summary = await service.GetSummaryAsync();
        Assert.Equal((4, 4, 0, 0), (summary.TotalCount, summary.ByStatus.Current, summary.ByStatus.Historical, summary.ByStatus.Upcoming));
        await AssertStatusSurfacesAgree(context);
    }

    [Fact] // AC 18
    public async Task The_summary_buckets_sum_to_the_total_when_a_pair_has_no_Current_row()
    {
        await using var context = await Seed(
            Rate("NOK", "USD", 0.2m, D.AddDays(-1)),
            Rate("USD", "NOK", 10m, D, fetched: true),
            Rate("USD", "NOK", 12m, D.AddDays(1)),
            Rate("USD", "EUR", 0.9m, Now.AddDays(2)));

        var summary = await Service(context).GetSummaryAsync();

        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(1, summary.ByStatus.Current);
        Assert.Equal(1, summary.ByStatus.Upcoming);
        Assert.Equal(2, summary.ByStatus.Historical);
        await AssertStatusSurfacesAgree(context);
    }

    /// <summary>
    /// AC 18: each row's badge, its membership in the <c>status=</c> filter, its place under <c>sortBy=status</c>
    /// and the summary's counts all say the same thing.
    /// </summary>
    private async Task AssertStatusSurfacesAgree(OdysseyContext context)
    {
        var service = Service(context);
        var badges = await StatusesAsync(context);

        foreach (var status in Enum.GetValues<ExchangeRateStatus>())
        {
            var filtered = (await service.ListAsync(new ExchangeRatesQueryParams { Status = [status], Limit = 100 })).Items;
            Assert.Equal(
                badges.Where(pair => pair.Value == status).Select(pair => pair.Key).Order(),
                filtered.Select(row => row.ExchangeRateId).Order());
        }

        var sorted = (await service.ListAsync(new ExchangeRatesQueryParams
        {
            SortBy = ExchangeRateSortBy.Status,
            SortDir = SortDirection.Asc,
            Limit = 100,
        })).Items;
        Assert.Equal(sorted.Select(row => row.Status).Order(), sorted.Select(row => row.Status));

        var summary = await service.GetSummaryAsync();
        Assert.Equal(badges.Count(pair => pair.Value == ExchangeRateStatus.Current), summary.ByStatus.Current);
        Assert.Equal(badges.Count(pair => pair.Value == ExchangeRateStatus.Historical), summary.ByStatus.Historical);
        Assert.Equal(badges.Count(pair => pair.Value == ExchangeRateStatus.Upcoming), summary.ByStatus.Upcoming);
        Assert.Equal(summary.TotalCount, summary.ByStatus.Current + summary.ByStatus.Historical + summary.ByStatus.Upcoming);
    }
}
