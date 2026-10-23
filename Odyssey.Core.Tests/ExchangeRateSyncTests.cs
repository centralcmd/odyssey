using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Finance.ExchangeRateSync.Providers;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The exchange-rate sync (issue #349): the Norges Bank adapter against recorded responses, the
/// structural checks, and the sync service's planning, writing and run log against a stubbed provider and
/// a fixed clock. The hosted scheduler is not under test here — it only asks this service what to do.
/// </summary>
public class ExchangeRateSyncTests
{
    /// <summary>A Friday in October, when Oslo is UTC+2: a 16:00 Oslo stamp is 14:00 UTC.</summary>
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NorgesBank", name));

    // ── The adapter ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseObservations_reads_the_recorded_range_with_its_unit_multipliers()
    {
        using var document = JsonDocument.Parse(Fixture("norgesbank-usd-sek-jpy-range.json"));

        var observations = NorgesBankClient.ParseObservations(document.RootElement);

        Assert.Equal(15, observations.Count);
        var jpy = Assert.Single(observations, o => o.CurrencyCode == "JPY" && o.Date == new DateOnly(2026, 10, 15));
        Assert.Equal("6.016", jpy.Value);
        Assert.Equal("2", jpy.UnitMult);
        Assert.All(observations.Where(o => o.CurrencyCode == "USD"), o => Assert.Equal("0", o.UnitMult));
        Assert.Equal(["JPY", "SEK", "USD"], observations.Select(o => o.CurrencyCode).Distinct().Order());
    }

    [Fact]
    public async Task GetSeriesAsync_pairs_each_series_newest_date_with_its_first()
    {
        var provider = new StubProvider();
        var client = new NorgesBankClient(new HttpClient(provider) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);

        var series = await client.GetSeriesAsync(CancellationToken.None);

        var usd = Assert.Single(series, s => s.CurrencyCode == "USD");
        Assert.Equal(new DateOnly(1980, 12, 10), usd.FirstObservation);
        Assert.Equal(new DateOnly(2026, 10, 22), usd.NewestObservation);
        Assert.Equal(new DateOnly(2022, 3, 1), Assert.Single(series, s => s.CurrencyCode == "RUB").NewestObservation);
        Assert.Null(Assert.Single(series, s => s.CurrencyCode == "SEK").FirstObservation);
    }

    [Fact]
    public void Only_series_published_within_thirty_days_are_offered()
    {
        using var document = JsonDocument.Parse(Fixture("norgesbank-last-observation.json"));
        var today = DateOnly.FromDateTime(Now);

        var fresh = NorgesBankClient.ParseObservations(document.RootElement)
            .Where(o => ExchangeRateSupportedListCache.IsFresh(new ProviderSeries(o.CurrencyCode, null, o.Date), today))
            .Select(o => o.CurrencyCode)
            .ToHashSet();

        Assert.Contains("USD", fresh);
        Assert.DoesNotContain("RUB", fresh);
        Assert.DoesNotContain("HRK", fresh);
        Assert.DoesNotContain("BGN", fresh);
    }

    [Fact]
    public void A_body_that_is_not_SDMX_is_malformed()
    {
        using var document = JsonDocument.Parse("""{"data":{"structure":{}}}""");

        var failure = Assert.Throws<ExchangeRateProviderException>(() => NorgesBankClient.ParseObservations(document.RootElement));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderMalformed, failure.Kind);
    }

    [Fact]
    public async Task A_redirect_is_malformed_and_never_followed()
    {
        var provider = new StubProvider { Respond = _ => new HttpResponseMessage(HttpStatusCode.Found) };
        var client = new NorgesBankClient(new HttpClient(provider) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);

        var failure = await Assert.ThrowsAsync<ExchangeRateProviderException>(() =>
            client.GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderMalformed, failure.Kind);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task A_server_error_is_retried_once_then_reported_unavailable()
    {
        var provider = new StubProvider { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        var client = new NorgesBankClient(new HttpClient(provider) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);

        var failure = await Assert.ThrowsAsync<ExchangeRateProviderException>(() =>
            client.GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderUnavailable, failure.Kind);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task A_rate_limit_without_a_usable_retry_after_is_not_retried_in_run()
    {
        var provider = new StubProvider
        {
            Respond = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("Retry-After", "3600");
                return response;
            },
        };
        var client = new NorgesBankClient(new HttpClient(provider) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);

        var failure = await Assert.ThrowsAsync<ExchangeRateProviderException>(() =>
            client.GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderRateLimited, failure.Kind);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_malformed()
    {
        var provider = new StubProvider
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[ExchangeRateSyncConstants.MaxResponseBytes + 1]),
            },
        };
        var client = new NorgesBankClient(new HttpClient(provider) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);

        var failure = await Assert.ThrowsAsync<ExchangeRateProviderException>(() =>
            client.GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderMalformed, failure.Kind);
    }

    // ── The structural checks ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalise_divides_by_the_unit_multiplier_and_stores_the_rounded_inverse()
    {
        Assert.True(ExchangeRateSyncService.TryNormalise(new ProviderObservation("JPY", new DateOnly(2026, 10, 15), "6.016", "2"), out var toHub, out var fromHub));

        Assert.Equal(0.06016m, toHub);
        Assert.Equal(16.62234043m, fromHub);
    }

    [Theory]
    [InlineData("abc", "0")]
    [InlineData("-1", "0")]
    [InlineData("0", "0")]
    [InlineData("9.5", "7")]
    [InlineData("9.5", null)]
    [InlineData(null, "0")]
    // Rounds to zero at 8 dp, outside the stored bounds.
    [InlineData("0.000000001", "0")]
    public void Normalise_rejects_a_structurally_unusable_observation(string? value, string? unitMult)
    {
        Assert.False(ExchangeRateSyncService.TryNormalise(new ProviderObservation("USD", new DateOnly(2026, 10, 15), value, unitMult), out _, out _));
    }

    [Fact]
    public void Normalise_accepts_a_large_genuine_move_without_a_plausibility_check()
    {
        Assert.True(ExchangeRateSyncService.TryNormalise(new ProviderObservation("USD", new DateOnly(2026, 10, 15), "9500.25", "0"), out var toHub, out _));
        Assert.Equal(9500.25m, toHub);
    }

    // ── The run ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_first_run_backfills_from_today_and_stores_both_directions_at_16_Oslo()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 19));
        await harness.EnableAsync("USD");

        var run = await harness.RunManualAsync();

        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, run.Status);
        Assert.Null(run.ErrorKind);
        // 2026-10-19 … 2026-10-23 is five business days, two rows each.
        Assert.Equal(10, run.RowsInserted);
        Assert.Equal("USD", run.CurrencyCodes);

        var rows = await harness.Context.ExchangeRates.AsNoTracking().ToListAsync();
        Assert.All(rows, row => Assert.Equal(ExchangeRateProvider.NorgesBank, row.Provider));
        Assert.All(rows, row => Assert.Equal(new TimeSpan(14, 0, 0), row.AsOf.TimeOfDay));
        var toNok = Assert.Single(rows, row => row.FromCurrencyCode == "USD" && row.AsOf.Date == new DateTime(2026, 10, 19));
        Assert.Equal(10.5m, toNok.Rate);
        var fromNok = Assert.Single(rows, row => row.FromCurrencyCode == "NOK" && row.AsOf.Date == new DateTime(2026, 10, 19));
        Assert.Equal(Math.Round(1m / 10.5m, 8), fromNok.Rate);

        var sync = await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 10, 19), sync.BackfilledFrom);
        Assert.Contains("startPeriod=2026-10-19&endPeriod=2026-10-23", harness.Provider.DataRequests.Single());
    }

    [Fact]
    public async Task A_row_already_present_is_skipped_and_a_manual_row_is_never_touched()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        var asOf = ExchangeRateSyncClock.FetchedAsOf(new DateOnly(2026, 10, 22), ExchangeRateSyncClock.Oslo!);
        harness.Context.ExchangeRates.AddRange(
            // The NOK → USD leg only, so the currency still has no fetched extent and is backfilled whole.
            new ExchangeRate { FromCurrencyCode = "NOK", ToCurrencyCode = "USD", Rate = 1m, AsOf = asOf, CreatedAt = Now, Provider = ExchangeRateProvider.NorgesBank },
            new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "NOK", Rate = 2m, AsOf = asOf, CreatedAt = Now });
        await harness.Context.SaveChangesAsync();

        var run = await harness.RunManualAsync();

        Assert.Equal(1, run.RowsSkipped);
        Assert.Equal(3, run.RowsInserted);
        Assert.Equal(2m, (await harness.Context.ExchangeRates.AsNoTracking().SingleAsync(r => r.Provider == null)).Rate);
    }

    [Fact]
    public async Task A_currency_the_provider_no_longer_publishes_is_skipped_and_named()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        await harness.EnableAsync("RUB");

        var run = await harness.RunManualAsync();

        Assert.Equal(ExchangeRateSyncRunStatus.PartiallySucceeded, run.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.UnsupportedCurrency, run.ErrorKind);
        Assert.DoesNotContain(harness.Provider.DataRequests, uri => uri.Contains("RUB"));
        Assert.Equal(["RUB"], (await harness.Sync.GetStatusAsync()).UnsupportedEnabledCodes);
    }

    [Fact]
    public async Task A_failed_request_fails_the_run_and_leaves_the_watermark_alone()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        harness.Provider.FailData = true;

        var run = await harness.RunManualAsync();

        Assert.Equal(ExchangeRateSyncRunStatus.Failed, run.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.ProviderUnavailable, run.ErrorKind);
        Assert.Null((await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync()).BackfilledFrom);
    }

    [Fact]
    public async Task Backfill_walks_newest_to_oldest_in_chunks_and_completes_at_the_start()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2024, 1, 1));
        await harness.EnableAsync("USD");

        var run = await harness.RunManualAsync();

        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, run.Status);
        var spans = harness.Provider.DataRequests.Select(Span).ToList();
        Assert.Equal(new DateOnly(2026, 10, 23), spans[0].To);
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.Equal(spans[i - 1].From.AddDays(-1), spans[i].To);
        }

        Assert.Equal(new DateOnly(2024, 1, 1), spans[^1].From);
        Assert.Equal(new DateOnly(2024, 1, 1), (await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync()).BackfilledFrom);
        Assert.True((await harness.Sync.GetStatusAsync()).BackfillComplete);
    }

    [Fact]
    public async Task A_later_run_forward_fills_only_the_missing_days()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 19));
        await harness.EnableAsync("USD");
        await harness.RunManualAsync();

        harness.Clock.Advance(TimeSpan.FromDays(4));
        harness.Provider.DataRequests.Clear();
        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal("startPeriod=2026-10-24&endPeriod=2026-10-27", Regex.Match(harness.Provider.DataRequests.Single(), "startPeriod=.*").Value);
        // Monday and Tuesday.
        Assert.Equal(4, run.RowsInserted);
    }

    [Fact]
    public async Task A_manual_run_with_nothing_enabled_is_recorded_as_skipped()
    {
        await using var harness = await Harness.CreateAsync();

        var run = await harness.Sync.StartManualAsync("admin");

        Assert.Equal(ExchangeRateSyncRunStatus.Skipped, run.Status);
        Assert.Equal(ExchangeRateSyncState.Idle, (await harness.Sync.GetStatusAsync()).State);
    }

    [Fact]
    public async Task A_scheduled_tick_with_nothing_enabled_writes_nothing()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());
        Assert.Empty(await harness.Context.ExchangeRateSyncRuns.ToListAsync());
    }

    [Fact]
    public async Task A_second_manual_run_within_the_cooldown_is_refused()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Sync.StartManualAsync("admin");

        var refused = await Assert.ThrowsAsync<ExchangeRateSyncCooldownException>(() => harness.Sync.StartManualAsync("admin"));

        Assert.Equal(Now.AddSeconds(ExchangeRateSyncConstants.ManualRunCooldownSeconds), refused.AvailableAtUtc);
        Assert.Equal(Now.AddSeconds(ExchangeRateSyncConstants.ManualRunCooldownSeconds), (await harness.Sync.GetStatusAsync()).ManualRunAvailableAtUtc);
    }

    [Fact]
    public async Task A_run_cannot_start_while_another_holds_the_guard_and_a_stale_one_is_abandoned()
    {
        await using var harness = await Harness.CreateAsync();
        var held = await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null);
        Assert.NotNull(held);
        Assert.Null(await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Purge, null));
        Assert.Equal(held!.ExchangeRateSyncRunId, (await harness.Sync.GetStatusAsync()).CurrentRun?.Id);

        harness.Clock.Advance(TimeSpan.FromMinutes(2 * ExchangeRateSyncConstants.RunTimeoutMinutes + 1));
        var next = await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null);

        Assert.NotNull(next);
        var abandoned = await harness.Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == held.ExchangeRateSyncRunId);
        Assert.Equal(ExchangeRateSyncRunStatus.Failed, abandoned.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.Abandoned, abandoned.ErrorKind);
    }

    [Fact]
    public async Task Three_consecutive_failures_back_off_and_a_success_resets_it()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        harness.Provider.FailData = true;
        for (var i = 0; i < 3; i++)
        {
            await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var status = await harness.Sync.GetStatusAsync();
        Assert.Equal(ExchangeRateSyncState.BackingOff, status.State);
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.Equal(harness.Clock.UtcNow.AddMinutes(-1).AddHours(48), status.NextRunAtUtc);

        harness.Provider.FailData = false;
        await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);
        Assert.Equal(0, (await harness.Sync.GetStatusAsync()).ConsecutiveFailures);
    }

    [Fact]
    public async Task The_run_log_is_pruned_to_its_retention()
    {
        await using var harness = await Harness.CreateAsync();
        for (var i = 0; i < ExchangeRateSyncConstants.RunRetention + 5; i++)
        {
            harness.Context.ExchangeRateSyncRuns.Add(new ExchangeRateSyncRun
            {
                Trigger = ExchangeRateSyncTrigger.Scheduled,
                Provider = ExchangeRateProvider.NorgesBank,
                Status = ExchangeRateSyncRunStatus.Succeeded,
                StartedAtUtc = Now.AddDays(-1).AddMinutes(i),
                FinishedAtUtc = Now.AddDays(-1).AddMinutes(i),
            });
        }

        await harness.Context.SaveChangesAsync();
        await harness.Sync.StartManualAsync("admin");

        Assert.Equal(ExchangeRateSyncConstants.RunRetention, await harness.Context.ExchangeRateSyncRuns.CountAsync());
    }

    // ── Configuration and purge ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enabling_needs_a_published_currency_and_NOK_cannot_be_changed()
    {
        await using var harness = await Harness.CreateAsync();

        var unsupported = await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            harness.Currencies.UpdateAsync("rub", new ExchangeRateSyncCurrencyUpdate { Enabled = true }, "admin"));
        Assert.True(unsupported.Errors!.ContainsKey("enabled"));

        await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            harness.Currencies.UpdateAsync("NOK", new ExchangeRateSyncCurrencyUpdate { Enabled = false }, "admin"));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            harness.Currencies.UpdateAsync("US1", new ExchangeRateSyncCurrencyUpdate { Enabled = true }, "admin"));
        await Assert.ThrowsAsync<DomainNotFoundException>(() =>
            harness.Currencies.UpdateAsync("XYZ", new ExchangeRateSyncCurrencyUpdate { Enabled = true }, "admin"));

        var future = await Assert.ThrowsAsync<DomainUnprocessableException>(() =>
            harness.Currencies.UpdateAsync("USD", new ExchangeRateSyncCurrencyUpdate { Enabled = true, HistoryStartDate = new DateOnly(2026, 10, 24) }, "admin"));
        Assert.True(future.Errors!.ContainsKey("historyStartDate"));

        // Disabling is never refused for support reasons — it is the remedy UnsupportedCurrency names.
        var disabled = await harness.Currencies.UpdateAsync("RUB", new ExchangeRateSyncCurrencyUpdate { Enabled = false }, "admin");
        Assert.False(disabled.Enabled);
        Assert.False(disabled.Supported);
    }

    [Fact]
    public async Task Enabling_while_the_provider_list_is_down_is_a_503_and_disabling_still_works()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Provider.FailSeries = true;

        await Assert.ThrowsAsync<ExchangeRateSupportedListUnavailableException>(() =>
            harness.Currencies.UpdateAsync("USD", new ExchangeRateSyncCurrencyUpdate { Enabled = true }, "admin"));

        var disabled = await harness.Currencies.UpdateAsync("USD", new ExchangeRateSyncCurrencyUpdate { Enabled = false }, "admin");
        Assert.Null(disabled.Supported);
    }

    [Fact]
    public async Task The_currency_list_reports_NOK_as_locked_and_counts_a_purge()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        await harness.RunManualAsync();

        var page = await harness.Currencies.ListAsync(new ExchangeRateSyncCurrenciesQueryParams());

        var nok = Assert.Single(page.Items, item => item.CurrencyCode == "NOK");
        Assert.True(nok.Locked);
        Assert.True(nok.Enabled);
        var usd = Assert.Single(page.Items, item => item.CurrencyCode == "USD");
        Assert.Equal(4, usd.FetchedRowCount);
        Assert.Equal(new DateOnly(1980, 12, 10), usd.FirstAvailableDate);
        Assert.Equal(new DateOnly(2026, 10, 23), usd.NewestFetchedDate);

        var supportedOnly = await harness.Currencies.ListAsync(new ExchangeRateSyncCurrenciesQueryParams { Supported = true });
        Assert.DoesNotContain(supportedOnly.Items, item => item.CurrencyCode == "RUB");
    }

    [Fact]
    public async Task A_purge_deletes_only_fetched_rows_resets_the_watermark_and_is_logged()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        await harness.RunManualAsync();
        harness.Context.ExchangeRates.Add(new ExchangeRate { FromCurrencyCode = "USD", ToCurrencyCode = "EUR", Rate = 0.9m, AsOf = Now, CreatedAt = Now });
        await harness.Context.SaveChangesAsync();

        var stillEnabled = await Assert.ThrowsAsync<DomainUnprocessableException>(() => harness.Currencies.PurgeAsync("USD", "admin"));
        Assert.True(stillEnabled.Errors!.ContainsKey("enabled"));
        await Assert.ThrowsAsync<DomainUnprocessableException>(() => harness.Currencies.PurgeAsync("NOK", "admin"));

        await harness.Currencies.UpdateAsync("USD", new ExchangeRateSyncCurrencyUpdate { Enabled = false }, "admin");
        var result = await harness.Currencies.PurgeAsync("usd", "admin");

        Assert.Equal(4, result.RowsDeleted);
        var left = Assert.Single(await harness.Context.ExchangeRates.AsNoTracking().ToListAsync());
        Assert.Null(left.Provider);
        Assert.Null((await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync()).BackfilledFrom);
        var purge = await harness.Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.Trigger == ExchangeRateSyncTrigger.Purge);
        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, purge.Status);
        Assert.Equal(4, purge.RowsDeleted);
    }

    [Fact]
    public async Task A_purge_cannot_overlap_a_run()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null);

        await Assert.ThrowsAsync<DomainConflictException>(() => harness.Currencies.PurgeAsync("USD", "admin"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static (DateOnly From, DateOnly To) Span(string uri)
    {
        var from = Regex.Match(uri, "startPeriod=([0-9-]+)").Groups[1].Value;
        var to = Regex.Match(uri, "endPeriod=([0-9-]+)").Groups[1].Value;
        return (DateOnly.Parse(from, CultureInfo.InvariantCulture), DateOnly.Parse(to, CultureInfo.InvariantCulture));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(OdysseyContext context, StubProvider provider, MutableClock clock, FakeSettings settings)
        {
            Context = context;
            Provider = provider;
            Clock = clock;
            var client = new NorgesBankClient(
                new HttpClient(provider) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);
            var cache = new ExchangeRateSupportedListCache(clock);
            Sync = new ExchangeRateSyncService(
                context, client, cache, settings, ExchangeRateSyncPacing.None, clock, NullLogger<ExchangeRateSyncService>.Instance);
            Currencies = new ExchangeRateSyncCurrencyService(context, client, cache, settings, Sync, clock, NullLoggerFactory.Instance);
        }

        public OdysseyContext Context { get; }

        public StubProvider Provider { get; }

        public MutableClock Clock { get; }

        public ExchangeRateSyncService Sync { get; }

        public ExchangeRateSyncCurrencyService Currencies { get; }

        public static async Task<Harness> CreateAsync(DateOnly? start = null)
        {
            var context = TestContextFactory.Create();
            context.Currencies.AddRange(
                new Currency { CurrencyCode = "NOK", Name = "Norwegian Krone", MinorUnits = 2, Symbol = "kr" },
                new Currency { CurrencyCode = "RUB", Name = "Russian Ruble", MinorUnits = 2, Symbol = "₽" });
            await context.SaveChangesAsync();
            return new Harness(context, new StubProvider(), new MutableClock(Now), new FakeSettings(start));
        }

        public async Task EnableAsync(string code)
        {
            Context.ExchangeRateSyncCurrencies.Add(new ExchangeRateSyncCurrency { CurrencyCode = code, Enabled = true, UpdatedAtUtc = Clock.UtcNow });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task<ExchangeRateSyncRun> RunManualAsync()
        {
            var run = await Sync.StartManualAsync("admin");
            await Sync.ExecuteAsync(run.ExchangeRateSyncRunId);
            return await Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == run.ExchangeRateSyncRunId);
        }

        public async Task<ExchangeRateSyncRun> RunAsync(ExchangeRateSyncTrigger trigger)
        {
            var run = (await Sync.TryStartAsync(trigger, null))!;
            await Sync.ExecuteAsync(run.ExchangeRateSyncRunId);
            return await Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == run.ExchangeRateSyncRunId);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FakeSettings(DateOnly? start) : IExchangeRateSyncSettingsLookup
    {
        public Task<ExchangeRateSyncSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExchangeRateSyncSettings(start, 24));
    }

    internal sealed class MutableClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset now = new(start, TimeSpan.Zero);

        public DateTime UtcNow => now.UtcDateTime;

        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Norges Bank in-process: the series list from the recorded fixtures, and for a data request one
    /// observation per weekday in the span for each requested code — USD at 10.5 NOK, the rest at 1.
    /// </summary>
    private sealed class StubProvider : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> DataRequests { get; } = [];

        public bool FailData { get; set; }

        public bool FailSeries { get; set; }

        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            if (Respond is not null)
            {
                return Task.FromResult(Respond(request));
            }

            if (uri.Contains("lastNObservations") || uri.Contains("firstNObservations"))
            {
                return Task.FromResult(FailSeries
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(Fixture(uri.Contains("lastNObservations")
                        ? "norgesbank-last-observation.json"
                        : "norgesbank-first-observation-usd-eur.json")));
            }

            DataRequests.Add(uri);
            if (FailData)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var codes = Regex.Match(uri, @"EXR/B\.([A-Z+]+)\.NOK").Groups[1].Value.Split('+');
            var (from, to) = Span(uri);
            return Task.FromResult(Json(Sdmx(codes, from, to)));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string Sdmx(string[] codes, DateOnly from, DateOnly to)
        {
            var days = new List<DateOnly>();
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    days.Add(day);
                }
            }

            var series = new Dictionary<string, object>();
            for (var i = 0; i < codes.Length; i++)
            {
                var value = codes[i] == "USD" ? "10.5" : "1";
                series[$"0:{i}:0:0"] = new
                {
                    attributes = new[] { 0 },
                    observations = days.Select((_, index) => (index, value))
                        .ToDictionary(x => x.index.ToString(CultureInfo.InvariantCulture), x => new[] { x.value }),
                };
            }

            return JsonSerializer.Serialize(new
            {
                data = new
                {
                    dataSets = new[] { new { series } },
                    structure = new
                    {
                        dimensions = new
                        {
                            series = new object[]
                            {
                                new { id = "FREQ", values = new[] { new { id = "B" } } },
                                new { id = "BASE_CUR", values = codes.Select(code => new { id = code }).ToArray() },
                                new { id = "QUOTE_CUR", values = new[] { new { id = "NOK" } } },
                                new { id = "TENOR", values = new[] { new { id = "SP" } } },
                            },
                            observation = new[]
                            {
                                new { id = "TIME_PERIOD", values = days.Select(day => new { id = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }).ToArray() },
                            },
                        },
                        attributes = new
                        {
                            series = new[] { new { id = "UNIT_MULT", values = new[] { new { id = "0" } } } },
                        },
                    },
                },
            });
        }
    }
}
