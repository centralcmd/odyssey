using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    // Below decimal's reciprocal range once scaled: 10 / 1e-28 would overflow rather than reject.
    [InlineData("0.0000000000000000000000000001", "1")]
    [InlineData("0.0000000000000000000000000001", "6")]
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
    public async Task A_manual_run_is_left_for_the_worker_to_claim_and_every_other_run_is_claimed_by_its_starter()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("RUB");

        var manual = await harness.Sync.StartManualAsync("admin");
        Assert.Null(manual.ClaimedAtUtc);

        Assert.Equal(manual.ExchangeRateSyncRunId, await harness.Sync.ClaimRequestedRunAsync());
        Assert.Null(await harness.Sync.ClaimRequestedRunAsync());
        await harness.Sync.ExecuteAsync(manual.ExchangeRateSyncRunId);

        var scheduled = (await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null))!;
        Assert.Equal(Now, scheduled.ClaimedAtUtc);
        Assert.Null(await harness.Sync.ClaimRequestedRunAsync());
    }

    [Fact]
    public async Task A_manual_run_with_nothing_to_do_is_inserted_claimed_so_no_worker_takes_it()
    {
        await using var harness = await Harness.CreateAsync();

        var run = await harness.Sync.StartManualAsync("admin");

        Assert.NotNull(run.ClaimedAtUtc);
        Assert.Null(await harness.Sync.ClaimRequestedRunAsync());
    }

    [Fact]
    public async Task A_request_no_worker_took_in_time_is_abandoned_not_executed_late()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("RUB");
        var manual = await harness.Sync.StartManualAsync("admin");

        harness.Clock.Advance(TimeSpan.FromMinutes(2 * ExchangeRateSyncConstants.RunTimeoutMinutes + 1));

        Assert.Null(await harness.Sync.ClaimRequestedRunAsync());
        var abandoned = await harness.Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == manual.ExchangeRateSyncRunId);
        Assert.Equal(ExchangeRateSyncErrorKind.Abandoned, abandoned.ErrorKind);
    }

    [Fact]
    public async Task The_job_executes_a_requested_run_before_looking_for_a_scheduled_one()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("USD");
        var manual = await harness.Sync.StartManualAsync("admin");
        var services = new ServiceCollection()
            .AddScoped(_ => harness.Sync)
            .BuildServiceProvider();

        await new ExchangeRateSyncJob(services.GetRequiredService<IServiceScopeFactory>())
            .RunOnceAsync(CancellationToken.None);

        var runs = await harness.Context.ExchangeRateSyncRuns.AsNoTracking().ToListAsync();
        var only = Assert.Single(runs);
        Assert.Equal(manual.ExchangeRateSyncRunId, only.ExchangeRateSyncRunId);
        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, only.Status);
        Assert.Equal(ExchangeRateSyncTrigger.Manual, only.Trigger);
    }

    [Fact]
    public async Task With_no_request_the_job_starts_and_finishes_a_due_scheduled_run()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("USD");
        var services = new ServiceCollection().AddScoped(_ => harness.Sync).BuildServiceProvider();

        await new ExchangeRateSyncJob(services.GetRequiredService<IServiceScopeFactory>()).RunOnceAsync(CancellationToken.None);

        var only = Assert.Single(await harness.Context.ExchangeRateSyncRuns.AsNoTracking().ToListAsync());
        Assert.Equal(ExchangeRateSyncTrigger.Scheduled, only.Trigger);
        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, only.Status);
        Assert.NotNull(only.ClaimedAtUtc);
    }

    /// <summary>
    /// The claim's conflict path, forced rather than raced: a second claimer takes the row through another
    /// context in the instant before this one saves, so the <c>ClaimedAtUtc</c> concurrency token is the
    /// only thing that can stop both from executing it.
    /// </summary>
    [Fact]
    public async Task A_claim_that_loses_to_another_replica_returns_nothing_and_keeps_the_winners_claim()
    {
        var store = Guid.NewGuid().ToString();
        var rivalAt = Now.AddSeconds(-3);
        var rival = new ClaimFirst(store, rivalAt);
        DbContextOptions<OdysseyContext> Options(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(store).AddInterceptors(interceptors).Options;

        Guid runId;
        await using (var seed = new OdysseyContext(Options()))
        {
            var run = new ExchangeRateSyncRun
            {
                Trigger = ExchangeRateSyncTrigger.Manual, Provider = ExchangeRateProvider.NorgesBank,
                Status = ExchangeRateSyncRunStatus.Running, StartedAtUtc = Now,
            };
            seed.ExchangeRateSyncRuns.Add(run);
            await seed.SaveChangesAsync();
            runId = run.ExchangeRateSyncRunId;
        }

        await using var context = new OdysseyContext(Options(rival));
        var clock = new MutableClock(Now);
        var sync = new ExchangeRateSyncService(
            context,
            new NorgesBankClient(new HttpClient(new StubProvider()) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None),
            new ExchangeRateSupportedListCache(clock), new FakeSettings(), ExchangeRateSyncPacing.None, clock,
            NullLogger<ExchangeRateSyncService>.Instance);

        Assert.Null(await sync.ClaimRequestedRunAsync());

        Assert.True(rival.Claimed);
        Assert.Empty(context.ChangeTracker.Entries<ExchangeRateSyncRun>());
        await using var verify = new OdysseyContext(Options());
        Assert.Equal(rivalAt, (await verify.ExchangeRateSyncRuns.SingleAsync(r => r.ExchangeRateSyncRunId == runId)).ClaimedAtUtc);
    }

    /// <summary>
    /// Abandoning an unclaimed request while a worker claims it in the same instant: the claim wins, the
    /// abandonment is dropped, and the caller is refused as "in progress" rather than failing.
    /// </summary>
    [Fact]
    public async Task An_abandonment_that_races_a_workers_claim_is_dropped_and_the_run_stays_live()
    {
        var store = Guid.NewGuid().ToString();
        var claimedAt = Now.AddMinutes(3);
        var rival = new ClaimFirst(store, claimedAt);
        DbContextOptions<OdysseyContext> Options(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(store).AddInterceptors(interceptors).Options;

        await using (var seed = new OdysseyContext(Options()))
        {
            seed.ExchangeRateSyncRuns.Add(new ExchangeRateSyncRun
            {
                Trigger = ExchangeRateSyncTrigger.Manual, Provider = ExchangeRateProvider.NorgesBank,
                Status = ExchangeRateSyncRunStatus.Running, StartedAtUtc = Now,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new OdysseyContext(Options(rival));
        var clock = new MutableClock(Now + ExchangeRateSyncConstants.UnclaimedRequestTimeout + TimeSpan.FromSeconds(1));
        var sync = new ExchangeRateSyncService(
            context,
            new NorgesBankClient(new HttpClient(new StubProvider()) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None),
            new ExchangeRateSupportedListCache(clock), new FakeSettings(), ExchangeRateSyncPacing.None, clock,
            NullLogger<ExchangeRateSyncService>.Instance);

        Assert.Null(await sync.TryStartAsync(ExchangeRateSyncTrigger.Purge, null));

        Assert.True(rival.Claimed);
        await using var verify = new OdysseyContext(Options());
        var run = await verify.ExchangeRateSyncRuns.SingleAsync();
        Assert.Equal(ExchangeRateSyncRunStatus.Running, run.Status);
        Assert.Equal(claimedAt, run.ClaimedAtUtc);
    }

    /// <summary>Claims the run through its own context the first time the service is about to save.</summary>
    private sealed class ClaimFirst(string store, DateTime claimedAt) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Claimed { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Claimed)
            {
                Claimed = true;
                await using var other = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(store).Options);
                var run = await other.ExchangeRateSyncRuns.SingleAsync(cancellationToken);
                run.ClaimedAtUtc = claimedAt;
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task A_request_no_worker_takes_is_given_up_after_the_unclaimed_timeout_and_frees_the_guard()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("RUB");
        var manual = await harness.Sync.StartManualAsync("admin");

        harness.Clock.Advance(ExchangeRateSyncConstants.UnclaimedRequestTimeout - TimeSpan.FromSeconds(1));
        Assert.Null(await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Purge, null));

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Purge, null));
        var abandoned = await harness.Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == manual.ExchangeRateSyncRunId);
        Assert.Equal(ExchangeRateSyncErrorKind.Abandoned, abandoned.ErrorKind);
        Assert.Equal(manual.StartedAtUtc + ExchangeRateSyncConstants.UnclaimedRequestTimeout, abandoned.FinishedAtUtc);
    }

    [Fact]
    public async Task A_claimed_run_is_not_abandoned_by_the_unclaimed_timeout()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("RUB");
        var manual = await harness.Sync.StartManualAsync("admin");
        Assert.NotNull(await harness.Sync.ClaimRequestedRunAsync());

        harness.Clock.Advance(TimeSpan.FromMinutes(2 * ExchangeRateSyncConstants.RunTimeoutMinutes - 1));

        Assert.Null(await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Purge, null));
        Assert.Equal(ExchangeRateSyncRunStatus.Running,
            (await harness.Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == manual.ExchangeRateSyncRunId)).Status);
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
    public async Task The_currency_list_reports_NOK_as_enabled_with_no_other_currency_enabled()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));

        var page = await harness.Currencies.ListAsync(new ExchangeRateSyncCurrenciesQueryParams());

        var nok = Assert.Single(page.Items, item => item.CurrencyCode == "NOK");
        Assert.True(nok.Locked);
        Assert.True(nok.Enabled);
    }

    [Fact]
    public async Task The_currencies_list_derives_filters_and_sorts_by_status_and_attaches_sync_only_when_asked()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        // Enabled with no back-fill yet: Fetching history.
        await harness.EnableAsync("USD");
        harness.Context.Currencies.Add(new Currency { CurrencyCode = "CHF", Name = "Swiss Franc", MinorUnits = 2, Symbol = "Fr", Archived = Now });
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();
        var currencies = new CurrencyService(harness.Context);
        var sync = await harness.Currencies.ProjectAllAsync();

        var synced = await currencies.ListAsync(
            new CurrenciesQueryParams { Statuses = [CurrencyStatus.Synced, CurrencyStatus.FetchingHistory] }, sync);
        Assert.Equal(["NOK", "USD"], synced.Items.Select(item => item.CurrencyCode).Order());
        Assert.Equal(CurrencyStatus.Synced, Assert.Single(synced.Items, item => item.CurrencyCode == "NOK").Status);
        Assert.Equal(CurrencyStatus.FetchingHistory, Assert.Single(synced.Items, item => item.CurrencyCode == "USD").Status);
        // The status is everyone's; the projection only when the caller may see it.
        Assert.All(synced.Items, item => Assert.Null(item.Sync));

        var notPublished = await currencies.ListAsync(new CurrenciesQueryParams { Statuses = [CurrencyStatus.NotPublished] }, sync);
        Assert.Equal(["RUB"], notPublished.Items.Select(item => item.CurrencyCode));

        var archived = await currencies.ListAsync(new CurrenciesQueryParams { Statuses = [CurrencyStatus.Archived] }, sync);
        Assert.Equal(["CHF"], archived.Items.Select(item => item.CurrencyCode));

        var all = await currencies.ListAsync(new CurrenciesQueryParams { SortBy = CurrencySortBy.Status }, sync, attachSync: true);
        // Reading order: synced, fetching history, …, not published, archived last.
        Assert.Equal(["NOK", "USD"], all.Items.Take(2).Select(item => item.CurrencyCode));
        Assert.Equal("RUB", all.Items[^2].CurrencyCode);
        Assert.Equal("CHF", all.Items[^1].CurrencyCode);
        Assert.Equal(CurrencyStatus.Archived, all.Items[^1].Status);
        Assert.Null(all.Items[^1].Sync);
        Assert.All(all.Items.Where(item => item.Archived is null), item => Assert.NotNull(item.Sync));
        Assert.All(all.Items.Where(item => item.Status == CurrencyStatus.Manual), item => Assert.False(item.Sync!.Enabled));

        // With no projection at all, every non-archived currency reads Manual.
        var plain = await currencies.ListAsync(new CurrenciesQueryParams { Statuses = [CurrencyStatus.Manual] });
        Assert.Equal(all.TotalCount - 1, plain.TotalCount);
        Assert.All(plain.Items, item => Assert.Equal(CurrencyStatus.Manual, item.Status));
    }

    private static NewCurrency Put(string code, string name, bool archived) =>
        new() { CurrencyCode = code, Name = name, MinorUnits = 2, Symbol = code, Archived = archived };

    [Fact]
    public async Task Archiving_a_synced_currency_switches_its_sync_off_and_restoring_leaves_it_off()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD", start: new DateOnly(2020, 1, 1));
        var currencies = new CurrencyService(harness.Context);

        var archived = await currencies.Update("USD", Put("USD", "US Dollar", archived: true));

        Assert.True(archived!.SyncStopped);
        var row = await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync(r => r.CurrencyCode == "USD");
        Assert.False(row.Enabled);
        // Kept for when someone restarts it.
        Assert.Equal(new DateOnly(2020, 1, 1), row.HistoryStartDate);

        var restored = await currencies.Update("USD", Put("USD", "US Dollar", archived: false));

        Assert.False(restored!.SyncStopped);
        Assert.False((await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync(r => r.CurrencyCode == "USD")).Enabled);
    }

    [Fact]
    public async Task Archiving_an_unsynced_currency_or_editing_a_synced_one_leaves_the_sync_alone()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        var currencies = new CurrencyService(harness.Context);

        Assert.False((await currencies.Update("EUR", Put("EUR", "Euro", archived: true)))!.SyncStopped);
        Assert.False((await currencies.Update("USD", Put("USD", "Dollar", archived: false)))!.SyncStopped);
        Assert.True((await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync(r => r.CurrencyCode == "USD")).Enabled);
    }

    [Fact]
    public async Task The_base_cannot_be_archived_while_any_currency_is_synced()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        var currencies = new CurrencyService(harness.Context);

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => currencies.Update("NOK", Put("NOK", "Norwegian Krone", archived: true)));
        Assert.Contains("Stop syncing every currency", refused.Message);
        harness.Context.ChangeTracker.Clear();
        Assert.Null((await harness.Context.Currencies.AsNoTracking().SingleAsync(c => c.CurrencyCode == "NOK")).Archived);

        await harness.Currencies.UpdateAsync("USD", new ExchangeRateSyncCurrencyUpdate { Enabled = false }, "admin");
        harness.Context.ChangeTracker.Clear();
        Assert.NotNull((await currencies.Update("NOK", Put("NOK", "Norwegian Krone", archived: true)))!.Currency.Archived);
    }

    [Fact]
    public async Task An_archived_currency_cannot_be_synced_and_says_why()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await new CurrencyService(harness.Context).Update("USD", Put("USD", "US Dollar", archived: true));
        harness.Context.ChangeTracker.Clear();

        var refused = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => harness.Currencies.UpdateAsync("USD", new ExchangeRateSyncCurrencyUpdate { Enabled = true }, "admin"));

        Assert.Equal("USD is archived. Restore it before syncing it.", refused.Message);
    }

    [Theory]
    [InlineData(true, false, true, true, true, CurrencyStatus.Archived)]
    [InlineData(false, true, false, false, true, CurrencyStatus.NoLongerPublished)]
    [InlineData(false, true, false, true, false, CurrencyStatus.FetchingHistory)]
    [InlineData(false, true, false, true, true, CurrencyStatus.Synced)]
    [InlineData(false, true, false, null, true, CurrencyStatus.Synced)]
    [InlineData(false, false, true, false, true, CurrencyStatus.Synced)]
    [InlineData(false, false, false, false, false, CurrencyStatus.NotPublished)]
    [InlineData(false, false, false, true, false, CurrencyStatus.Manual)]
    [InlineData(false, false, false, null, false, CurrencyStatus.Manual)]
    public void A_currencys_status_follows_its_precedence(
        bool archived, bool enabled, bool locked, bool? supported, bool backfillComplete, CurrencyStatus expected) =>
        Assert.Equal(expected, CurrencyStatuses.Of(archived, new ExistingExchangeRateSyncCurrency
        {
            CurrencyCode = "USD", Name = "US Dollar", Enabled = enabled, Locked = locked,
            Supported = supported, BackfillComplete = backfillComplete,
        }));

    [Fact]
    public void A_currency_with_no_projection_is_manual_unless_archived()
    {
        Assert.Equal(CurrencyStatus.Manual, CurrencyStatuses.Of(archived: false, null));
        Assert.Equal(CurrencyStatus.Archived, CurrencyStatuses.Of(archived: true, null));
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

    // ── Review round 1: recovery, faults and the run's limits ────────────────────────────────────

    [Fact]
    public async Task The_scheduler_abandons_a_stale_running_row_and_runs()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        harness.Context.ExchangeRateSyncRuns.Add(new ExchangeRateSyncRun
        {
            Trigger = ExchangeRateSyncTrigger.Scheduled,
            Provider = ExchangeRateProvider.NorgesBank,
            Status = ExchangeRateSyncRunStatus.Running,
            StartedAtUtc = Now.AddMinutes(-(2 * ExchangeRateSyncConstants.RunTimeoutMinutes) - 1),
        });
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();

        var ran = await harness.Sync.RunScheduledIfDueAsync();

        Assert.NotNull(ran);
        var runs = await harness.Context.ExchangeRateSyncRuns.AsNoTracking().ToListAsync();
        Assert.Contains(runs, run => run.ErrorKind == ExchangeRateSyncErrorKind.Abandoned && run.Status == ExchangeRateSyncRunStatus.Failed);
        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, runs.Single(run => run.ExchangeRateSyncRunId == ran).Status);
    }

    [Fact]
    public async Task A_fresh_running_row_is_left_alone_by_the_scheduler()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Manual, "admin");

        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());
    }

    [Fact]
    public async Task An_unforeseen_fault_finishes_the_run_as_internal_and_releases_the_guard()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        harness.Settings.ThrowOnRead = true;

        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal(ExchangeRateSyncRunStatus.Failed, run.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.Internal, run.ErrorKind);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.NotNull(await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null));
    }

    [Fact]
    public async Task A_settings_read_failure_fails_the_run_as_settings_unusable()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        harness.Settings.ReadFailed = true;

        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal(ExchangeRateSyncRunStatus.Failed, run.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.SettingsUnusable, run.ErrorKind);
        Assert.Empty(harness.Provider.DataRequests);
    }

    [Fact]
    public async Task A_shutdown_mid_run_ends_it_failed_and_cancelled()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        harness.Provider.Hang = true;
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled, stopping.Token);

        Assert.Equal(ExchangeRateSyncRunStatus.Failed, run.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.Cancelled, run.ErrorKind);
        Assert.NotNull(await harness.Sync.TryStartAsync(ExchangeRateSyncTrigger.Scheduled, null));
    }

    [Fact]
    public async Task A_failed_chunk_stops_the_watermark_at_the_last_committed_one_and_the_next_run_resumes()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2024, 1, 1));
        await harness.EnableAsync("USD");
        harness.Provider.FailWhen = uri => uri.Contains("endPeriod=2025-10-23");

        var first = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal(ExchangeRateSyncRunStatus.PartiallySucceeded, first.Status);
        Assert.Equal(ExchangeRateSyncErrorKind.ProviderUnavailable, first.ErrorKind);
        Assert.Equal(new DateOnly(2025, 10, 24), await WatermarkAsync(harness, "USD"));
        // The chunk after the failed one was never asked for: the watermark cannot jump a hole.
        Assert.DoesNotContain(harness.Provider.DataRequests, uri => uri.Contains("endPeriod=2024-10-23"));

        harness.Provider.FailWhen = null;
        harness.Provider.DataRequests.Clear();
        var second = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, second.Status);
        Assert.Equal(new DateOnly(2025, 10, 23), Span(harness.Provider.DataRequests[0]).To);
        Assert.Equal(new DateOnly(2024, 1, 1), await WatermarkAsync(harness, "USD"));
    }

    [Fact]
    public async Task A_run_makes_at_most_ten_requests_and_the_status_reports_catching_up()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(1990, 1, 1));
        await harness.EnableAsync("USD");

        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal(ExchangeRateSyncRunStatus.Succeeded, run.Status);
        Assert.Equal(ExchangeRateSyncConstants.RequestsPerRun, harness.Provider.DataRequests.Count);
        var today = new DateOnly(2026, 10, 23);
        Assert.Equal(today.AddDays(-(ExchangeRateSyncConstants.RequestsPerRun * ExchangeRateSyncConstants.DaysPerRequest) + 1),
            await WatermarkAsync(harness, "USD"));

        var status = await harness.Sync.GetStatusAsync();
        Assert.Equal(ExchangeRateSyncState.CatchingUp, status.State);
        Assert.False(status.BackfillComplete);
        Assert.Equal(run.FinishedAtUtc!.Value.AddMinutes(ExchangeRateSyncConstants.CatchUpDelayMinutes), status.NextRunAtUtc);
    }

    [Fact]
    public async Task Forward_fill_uses_at_most_five_requests_oldest_first_and_keeps_catching_up()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2019, 1, 1));
        await harness.EnableAsync("USD", backfilledFrom: new DateOnly(2019, 1, 1));
        harness.Context.ExchangeRates.Add(new ExchangeRate
        {
            FromCurrencyCode = "USD", ToCurrencyCode = "NOK", Rate = 10m, CreatedAt = Now,
            AsOf = ExchangeRateSyncClock.FetchedAsOf(new DateOnly(2019, 1, 2), ExchangeRateSyncClock.Oslo!),
            Provider = ExchangeRateProvider.NorgesBank,
        });
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();

        await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        var spans = harness.Provider.DataRequests.Select(Span).ToList();
        Assert.Equal(ExchangeRateSyncConstants.ForwardFillRequestsPerRun, spans.Count);
        Assert.Equal(new DateOnly(2019, 1, 3), spans[0].From);
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.Equal(spans[i - 1].To.AddDays(1), spans[i].From);
        }

        Assert.Equal(ExchangeRateSyncState.CatchingUp, (await harness.Sync.GetStatusAsync()).State);
    }

    [Fact]
    public async Task A_provider_outage_longer_than_the_gap_does_not_loop_on_the_catch_up_delay()
    {
        await using var harness = await CaughtUpAsync();
        harness.Clock.Advance(TimeSpan.FromDays(20));
        var finished = harness.Clock.UtcNow.AddMinutes(-1);

        // The newest fetched date is 20 days behind and the last run found nothing: an outage, not a
        // capped forward fill, so the ordinary interval applies.
        harness.Context.ExchangeRateSyncRuns.Add(FinishedRun(ExchangeRateSyncRunStatus.Succeeded, finished));
        await harness.Context.SaveChangesAsync();
        var outage = await harness.Sync.GetStatusAsync();
        Assert.Equal(ExchangeRateSyncState.Scheduled, outage.State);
        Assert.Equal(finished.AddHours(24), outage.NextRunAtUtc);

        // The same gap after a run that DID insert rows is a capped forward fill: catch up in 5 minutes.
        var capped = FinishedRun(ExchangeRateSyncRunStatus.Succeeded, finished.AddSeconds(30));
        capped.RowsInserted = 10;
        harness.Context.ExchangeRateSyncRuns.Add(capped);
        await harness.Context.SaveChangesAsync();
        Assert.Equal(ExchangeRateSyncState.CatchingUp, (await harness.Sync.GetStatusAsync()).State);
    }

    [Fact]
    public async Task One_currency_failing_does_not_hold_back_another()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.EnableAsync("USD", start: new DateOnly(2026, 10, 19));
        await harness.EnableAsync("EUR", start: new DateOnly(2026, 10, 12));
        harness.Provider.FailWhen = uri => uri.Contains("EUR");

        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Equal(ExchangeRateSyncRunStatus.PartiallySucceeded, run.Status);
        Assert.Equal(new DateOnly(2026, 10, 19), await WatermarkAsync(harness, "USD"));
        Assert.Null(await WatermarkAsync(harness, "EUR"));
        Assert.Equal(10, run.RowsInserted);
    }

    [Fact]
    public async Task Currencies_sharing_a_span_share_one_request()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 19));
        await harness.EnableAsync("USD");
        await harness.EnableAsync("EUR");

        var run = await harness.RunAsync(ExchangeRateSyncTrigger.Scheduled);

        Assert.Contains("EXR/B.EUR+USD.NOK.SP", Assert.Single(harness.Provider.DataRequests));
        Assert.Equal(20, run.RowsInserted);
    }

    [Theory]
    // A start before the series begins completes once the watermark reaches the first observation.
    [InlineData("1999-01-04", "1990-01-01", "1999-01-04", true)]
    [InlineData("1999-01-04", "1990-01-01", "1999-01-05", false)]
    [InlineData(null, "2020-01-01", "2020-01-01", true)]
    [InlineData(null, "2020-01-01", "2020-01-02", false)]
    [InlineData("1999-01-04", "2020-01-01", "2019-12-31", true)]
    [InlineData("1999-01-04", "2020-01-01", null, false)]
    public void Backfill_completion_reaches_the_later_of_the_start_and_the_first_observation(
        string? first, string start, string? watermark, bool complete)
    {
        static DateOnly? Date(string? value) => value is null ? null : DateOnly.Parse(value, CultureInfo.InvariantCulture);

        Assert.Equal(complete, ExchangeRateSyncService.IsBackfillComplete(Date(watermark), Date(start)!.Value, Date(first)));
    }

    [Fact]
    public async Task A_scheduled_run_waits_for_the_interval_after_a_success()
    {
        await using var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 22));
        await harness.EnableAsync("USD");
        Assert.NotNull(await harness.Sync.RunScheduledIfDueAsync());

        harness.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());

        harness.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await harness.Sync.RunScheduledIfDueAsync());
    }

    [Theory]
    [InlineData(2, 24, 24, false)]
    [InlineData(3, 24, 48, true)]
    [InlineData(4, 24, 96, true)]
    [InlineData(6, 24, 168, true)]
    [InlineData(3, 168, 168, true)]
    public async Task Consecutive_failures_back_off_doubling_to_the_cap(int failures, int interval, int expectedHours, bool backingOff)
    {
        await using var harness = await CaughtUpAsync();
        harness.Settings.IntervalHours = interval;
        var finished = Now.AddHours(-1);
        for (var i = 0; i < failures; i++)
        {
            harness.Context.ExchangeRateSyncRuns.Add(FinishedRun(ExchangeRateSyncRunStatus.Failed, finished.AddMinutes(-i)));
        }

        await harness.Context.SaveChangesAsync();

        var status = await harness.Sync.GetStatusAsync();

        Assert.Equal(failures, status.ConsecutiveFailures);
        Assert.Equal(backingOff ? ExchangeRateSyncState.BackingOff : ExchangeRateSyncState.Scheduled, status.State);
        Assert.Equal(finished.AddHours(expectedHours), status.NextRunAtUtc);
    }

    [Fact]
    public async Task A_success_breaks_the_failure_streak_and_skipped_runs_and_purges_do_not_count()
    {
        await using var harness = await CaughtUpAsync();
        var at = Now.AddHours(-1);
        harness.Context.ExchangeRateSyncRuns.AddRange(
            FinishedRun(ExchangeRateSyncRunStatus.Failed, at),
            FinishedRun(ExchangeRateSyncRunStatus.Skipped, at.AddMinutes(-1)),
            FinishedRun(ExchangeRateSyncRunStatus.Failed, at.AddMinutes(-2), ExchangeRateSyncTrigger.Purge),
            FinishedRun(ExchangeRateSyncRunStatus.Failed, at.AddMinutes(-3)),
            FinishedRun(ExchangeRateSyncRunStatus.PartiallySucceeded, at.AddMinutes(-4)),
            FinishedRun(ExchangeRateSyncRunStatus.Failed, at.AddMinutes(-5)),
            FinishedRun(ExchangeRateSyncRunStatus.Failed, at.AddMinutes(-6)));
        await harness.Context.SaveChangesAsync();

        Assert.Equal(2, (await harness.Sync.GetStatusAsync()).ConsecutiveFailures);
    }

    [Fact]
    public async Task The_run_log_is_newest_first_filterable_and_capped_at_a_hundred_a_page()
    {
        await using var harness = await Harness.CreateAsync();
        for (var i = 0; i < 150; i++)
        {
            harness.Context.ExchangeRateSyncRuns.Add(FinishedRun(
                i % 3 == 0 ? ExchangeRateSyncRunStatus.Failed : ExchangeRateSyncRunStatus.Succeeded, Now.AddMinutes(-i)));
        }

        await harness.Context.SaveChangesAsync();

        var page = await harness.Sync.ListRunsAsync(new ExchangeRateSyncRunsQueryParams { Limit = 500 });
        Assert.Equal(ExchangeRateSyncService.MaxRunsPageSize, page.Items.Count);
        Assert.Equal(150, page.TotalCount);
        Assert.Equal(page.Items.Select(r => r.StartedAtUtc).OrderByDescending(t => t), page.Items.Select(r => r.StartedAtUtc));

        var failed = await harness.Sync.ListRunsAsync(new ExchangeRateSyncRunsQueryParams { Status = ExchangeRateSyncRunStatus.Failed });
        Assert.Equal(50, failed.TotalCount);
        Assert.All(failed.Items, r => Assert.Equal(ExchangeRateSyncRunStatus.Failed, r.Status));
    }

    [Fact]
    public async Task A_purge_larger_than_one_batch_deletes_every_fetched_row()
    {
        await using var harness = await Harness.CreateAsync();
        var start = new DateTime(2000, 1, 1, 14, 0, 0, DateTimeKind.Utc);
        var count = ExchangeRateSyncConstants.PurgeBatchSize + 250;
        for (var i = 0; i < count; i++)
        {
            harness.Context.ExchangeRates.Add(new ExchangeRate
            {
                FromCurrencyCode = i % 2 == 0 ? "USD" : "NOK", ToCurrencyCode = i % 2 == 0 ? "NOK" : "USD",
                Rate = 1m, AsOf = start.AddDays(i / 2), CreatedAt = Now, Provider = ExchangeRateProvider.NorgesBank,
            });
        }

        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();

        var result = await harness.Currencies.PurgeAsync("USD", "admin");

        Assert.Equal(count, result.RowsDeleted);
        Assert.Empty(await harness.Context.ExchangeRates.ToListAsync());
    }

    // ── Norwegian time ────────────────────────────────────────────────────────────────────────────

    [Theory]
    // Summer (CEST, UTC+2), winter (CET, UTC+1), and the DST change itself: on 2026-10-25 the clocks go
    // back at 03:00, so 16:00 that afternoon is already CET.
    [InlineData("2026-10-22", "2026-10-22T14:00:00Z")]
    [InlineData("2026-01-15", "2026-01-15T15:00:00Z")]
    [InlineData("2026-10-25", "2026-10-25T15:00:00Z")]
    [InlineData("2026-03-29", "2026-03-29T14:00:00Z")]
    public void A_fetched_rate_takes_effect_at_16_Oslo_in_any_season(string date, string expectedUtc)
    {
        var asOf = ExchangeRateSyncClock.FetchedAsOf(DateOnly.Parse(date, CultureInfo.InvariantCulture), ExchangeRateSyncClock.Oslo!);

        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), asOf);
        Assert.Equal(DateTimeKind.Utc, asOf.Kind);
    }

    [Fact]
    public void Today_is_the_Oslo_date_even_when_the_UTC_date_is_still_yesterday()
    {
        var lateEvening = new DateTime(2026, 10, 23, 22, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 10, 24), ExchangeRateSyncClock.TodayIn(ExchangeRateSyncClock.Oslo!, lateEvening));
        Assert.Equal(new DateOnly(2026, 10, 23), ExchangeRateSyncClock.DateIn(null, lateEvening));
    }

    // ── The adapter's remaining failure shapes ────────────────────────────────────────────────────

    [Fact]
    public async Task A_404_is_a_span_with_no_observations_not_a_failure()
    {
        var client = Client(new StubProvider { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) });

        Assert.Empty(await client.GetObservationsAsync(["USD"], new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 25), CancellationToken.None));
    }

    [Fact]
    public async Task A_short_retry_after_is_honoured_once()
    {
        var calls = 0;
        var provider = new StubProvider
        {
            Respond = _ =>
            {
                if (++calls == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.TryAddWithoutValidation("Retry-After", "2");
                    return limited;
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };

        Assert.Empty(await Client(provider).GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_network_failure_is_retried_once_then_unavailable()
    {
        var calls = 0;
        var provider = new StubProvider { Respond = _ => { calls++; throw new HttpRequestException("down"); } };

        var failure = await Assert.ThrowsAsync<ExchangeRateProviderException>(() =>
            Client(provider).GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderUnavailable, failure.Kind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_body_that_is_not_JSON_is_malformed()
    {
        var provider = new StubProvider
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") },
        };

        var failure = await Assert.ThrowsAsync<ExchangeRateProviderException>(() =>
            Client(provider).GetObservationsAsync(["USD"], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), CancellationToken.None));

        Assert.Equal(ExchangeRateSyncErrorKind.ProviderMalformed, failure.Kind);
    }

    private static NorgesBankClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(NorgesBankClient.BaseAddress) }, ExchangeRateSyncPacing.None);

    private static async Task<DateOnly?> WatermarkAsync(Harness harness, string code) =>
        (await harness.Context.ExchangeRateSyncCurrencies.AsNoTracking().SingleAsync(row => row.CurrencyCode == code)).BackfilledFrom;

    /// <summary>USD enabled with its backfill complete and fetched through today, so nothing is catching up.</summary>
    private static async Task<Harness> CaughtUpAsync()
    {
        var harness = await Harness.CreateAsync(start: new DateOnly(2026, 10, 1));
        await harness.EnableAsync("USD", backfilledFrom: new DateOnly(2026, 10, 1));
        harness.Context.ExchangeRates.Add(new ExchangeRate
        {
            FromCurrencyCode = "USD", ToCurrencyCode = "NOK", Rate = 10m, CreatedAt = Now,
            AsOf = ExchangeRateSyncClock.FetchedAsOf(new DateOnly(2026, 10, 22), ExchangeRateSyncClock.Oslo!),
            Provider = ExchangeRateProvider.NorgesBank,
        });
        await harness.Context.SaveChangesAsync();
        harness.Context.ChangeTracker.Clear();
        return harness;
    }

    private static ExchangeRateSyncRun FinishedRun(
        ExchangeRateSyncRunStatus status, DateTime finished, ExchangeRateSyncTrigger trigger = ExchangeRateSyncTrigger.Scheduled) => new()
    {
        Trigger = trigger,
        Provider = ExchangeRateProvider.NorgesBank,
        Status = status,
        StartedAtUtc = finished.AddSeconds(-5),
        FinishedAtUtc = finished,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static (DateOnly From, DateOnly To) Span(string uri)
    {
        var from = Regex.Match(uri, "startPeriod=([0-9-]+)").Groups[1].Value;
        var to = Regex.Match(uri, "endPeriod=([0-9-]+)").Groups[1].Value;
        return (DateOnly.Parse(from, CultureInfo.InvariantCulture), DateOnly.Parse(to, CultureInfo.InvariantCulture));
    }

    // ── The provider list's non-waiting read (the currency list's) ─────────────────────────────────

    private static readonly IReadOnlyList<ProviderSeries> SeriesA =
        [new ProviderSeries("USD", new DateOnly(1980, 12, 10), new DateOnly(2026, 10, 22))];

    private static readonly IReadOnlyList<ProviderSeries> SeriesB =
        [new ProviderSeries("EUR", new DateOnly(1999, 1, 4), new DateOnly(2026, 10, 22))];

    [Fact]
    public async Task An_expired_list_is_answered_at_once_while_one_background_refresh_replaces_it()
    {
        var clock = new MutableClock(Now);
        var cache = new ExchangeRateSupportedListCache(clock);
        await cache.GetAsync(_ => Task.FromResult(SeriesA), CancellationToken.None);
        clock.Advance(ExchangeRateSyncConstants.SupportedListTtl + TimeSpan.FromMinutes(1));
        var pending = new TaskCompletionSource<IReadOnlyList<ProviderSeries>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        Task<IReadOnlyList<ProviderSeries>> Fetch(CancellationToken _) { Interlocked.Increment(ref fetches); return pending.Task; }

        // The refresh never completes on its own, so a read that waited on it would hang here.
        Assert.Same(SeriesA, cache.GetWithoutWaiting(Fetch));
        Assert.Same(SeriesA, cache.GetWithoutWaiting(Fetch));
        await WaitUntil(() => Volatile.Read(ref fetches) == 1);
        Assert.Equal(1, Volatile.Read(ref fetches));

        pending.SetResult(SeriesB);
        await WaitUntil(() => ReferenceEquals(cache.GetWithoutWaiting(Fetch), SeriesB));
        Assert.Equal(1, Volatile.Read(ref fetches));
    }

    [Fact]
    public async Task A_provider_outage_keeps_answering_the_last_good_list_and_retries_only_after_the_failure_entry()
    {
        var clock = new MutableClock(Now);
        var cache = new ExchangeRateSupportedListCache(clock);
        await cache.GetAsync(_ => Task.FromResult(SeriesA), CancellationToken.None);
        clock.Advance(ExchangeRateSyncConstants.SupportedListTtl + TimeSpan.FromMinutes(1));
        var fetches = 0;
        Task<IReadOnlyList<ProviderSeries>> Down(CancellationToken _)
        {
            Interlocked.Increment(ref fetches);
            throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderUnavailable);
        }

        Assert.Same(SeriesA, cache.GetWithoutWaiting(Down));
        await WaitUntil(() => cache.Peek() is null && Volatile.Read(ref fetches) == 1);

        // Inside the failure entry: still the last good list, and no new attempt.
        Assert.Same(SeriesA, cache.GetWithoutWaiting(Down));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref fetches));

        clock.Advance(ExchangeRateSyncConstants.SupportedListFailureTtl + TimeSpan.FromSeconds(1));
        Assert.Same(SeriesA, cache.GetWithoutWaiting(Down));
        await WaitUntil(() => Volatile.Read(ref fetches) == 2);
    }

    [Fact]
    public async Task With_no_list_yet_the_read_answers_null_and_starts_the_first_fetch()
    {
        var cache = new ExchangeRateSupportedListCache(new MutableClock(Now));

        Assert.Null(cache.GetWithoutWaiting(_ => Task.FromResult(SeriesA)));

        await WaitUntil(() => ReferenceEquals(cache.GetWithoutWaiting(_ => Task.FromResult(SeriesB)), SeriesA));
    }

    [Fact]
    public async Task An_unexpected_failure_in_the_background_is_cached_as_a_failure_not_thrown()
    {
        var cache = new ExchangeRateSupportedListCache(new MutableClock(Now));

        Assert.Null(cache.GetWithoutWaiting(_ => throw new InvalidOperationException("boom")));

        // Cached as a failure: a waiting read inside the failure entry answers null without fetching.
        await Task.Delay(50);
        var fetched = false;
        Assert.Null(await cache.GetAsync(_ => { fetched = true; return Task.FromResult(SeriesA); }, CancellationToken.None));
        Assert.False(fetched);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The condition did not hold within two seconds.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(OdysseyContext context, StubProvider provider, MutableClock clock, FakeSettings settings)
        {
            Context = context;
            Settings = settings;
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

        public FakeSettings Settings { get; }

        public ExchangeRateSyncService Sync { get; }

        public ExchangeRateSyncCurrencyService Currencies { get; }

        public static async Task<Harness> CreateAsync(DateOnly? start = null)
        {
            var context = TestContextFactory.Create();
            context.Currencies.AddRange(
                new Currency { CurrencyCode = "NOK", Name = "Norwegian Krone", MinorUnits = 2, Symbol = "kr" },
                new Currency { CurrencyCode = "RUB", Name = "Russian Ruble", MinorUnits = 2, Symbol = "₽" });
            await context.SaveChangesAsync();
            return new Harness(context, new StubProvider(), new MutableClock(Now), new FakeSettings { Start = start });
        }

        public async Task EnableAsync(string code, DateOnly? start = null, DateOnly? backfilledFrom = null)
        {
            Context.ExchangeRateSyncCurrencies.Add(new ExchangeRateSyncCurrency
            {
                CurrencyCode = code, Enabled = true, HistoryStartDate = start, BackfilledFrom = backfilledFrom,
                UpdatedAtUtc = Clock.UtcNow,
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task<ExchangeRateSyncRun> RunManualAsync()
        {
            var run = await Sync.StartManualAsync("admin");
            await Sync.ExecuteAsync(run.ExchangeRateSyncRunId);
            return await Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == run.ExchangeRateSyncRunId);
        }

        public async Task<ExchangeRateSyncRun> RunAsync(ExchangeRateSyncTrigger trigger, CancellationToken cancellationToken = default)
        {
            var run = (await Sync.TryStartAsync(trigger, null))!;
            await Sync.ExecuteAsync(run.ExchangeRateSyncRunId, cancellationToken);
            return await Context.ExchangeRateSyncRuns.AsNoTracking().SingleAsync(r => r.ExchangeRateSyncRunId == run.ExchangeRateSyncRunId);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class FakeSettings : IExchangeRateSyncSettingsLookup
    {
        public DateOnly? Start { get; set; }

        public int IntervalHours { get; set; } = 24;

        public bool ReadFailed { get; set; }

        /// <summary>Throws from inside the run — the stand-in for any unforeseen fault.</summary>
        public bool ThrowOnRead { get; set; }

        public int Reads { get; private set; }

        public Task<ExchangeRateSyncSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return ThrowOnRead
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new ExchangeRateSyncSettings(Start, IntervalHours, ReadFailed));
        }
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

        /// <summary>A data request whose URI matches answers 503 (on both attempts).</summary>
        public Func<string, bool>? FailWhen { get; set; }

        /// <summary>Data requests never answer until cancelled.</summary>
        public bool Hang { get; set; }

        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hang && !request.RequestUri!.ToString().Contains("NObservations"))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Answer(request);
        }

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            if (Respond is not null)
            {
                return Respond(request);
            }

            if (uri.Contains("lastNObservations") || uri.Contains("firstNObservations"))
            {
                return FailSeries
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(Fixture(uri.Contains("lastNObservations")
                        ? "norgesbank-last-observation.json"
                        : "norgesbank-first-observation-usd-eur.json"));
            }

            DataRequests.Add(uri);
            if (FailData || FailWhen?.Invoke(uri) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var codes = Regex.Match(uri, @"EXR/B\.([A-Z+]+)\.NOK").Groups[1].Value.Split('+');
            var (from, to) = Span(uri);
            return Json(Sdmx(codes, from, to));
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
