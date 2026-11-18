using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Finance.Instruments.Sync;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared.InstrumentPrices;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The instrument-price sync (issue #446 §3, §4): forward fill and backfill, fallback between bindings, the
/// source back-offs, the credential states, the daily cap, the structural batch check and the schedule — run
/// against the test-only fake sources through the framework's real transport and an InMemory store.
/// </summary>
public class InstrumentPriceSyncTests
{
    /// <summary>A Friday; Oslo is UTC+2, so the Oslo date is the same.</summary>
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 23);

    [Fact]
    public async Task An_empty_catalogue_writes_nothing_at_all()
    {
        await using var harness = new SyncHarness(registerSources: false);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);

        Assert.Null(await harness.Sync.NextRunAtUtcAsync());
        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());

        Assert.Empty(await harness.Store.JobRuns.ToListAsync());
        Assert.Empty(await harness.Store.InstrumentPrices.ToListAsync());
        Assert.Empty(await harness.Store.InstrumentPriceSourceStates.ToListAsync());
    }

    [Fact]
    public async Task No_enabled_binding_writes_nothing()
    {
        await using var harness = new SyncHarness();
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1, enabled: false);

        Assert.False(await harness.Sync.HasWorkAsync());
        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());
        Assert.Empty(await harness.Store.JobRuns.ToListAsync());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task An_archived_instrument_is_not_work()
    {
        await using var harness = new SyncHarness();
        var instrument = await harness.AddInstrumentAsync("EQNR", archived: true);
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);

        Assert.False(await harness.Sync.HasWorkAsync());
        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());
    }

    [Fact]
    public async Task A_new_binding_forward_fills_to_today_then_backfills_to_its_start()
    {
        await using var harness = new SyncHarness(historyDays: 40);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);

        var runId = await harness.Sync.RunScheduledIfDueAsync();

        Assert.NotNull(runId);
        var run = await harness.RunAsync(runId!.Value);
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.RequestsMade);
        Assert.Equal(1, run.InstrumentsAttempted);
        Assert.Equal(1, run.InstrumentsSucceeded);

        var binding = await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey);
        Assert.Equal(Today, binding.NewestFetched);
        Assert.Equal(Today.AddDays(-40), binding.BackfilledFrom);
        Assert.Equal(0, binding.ConsecutiveFailures);

        var prices = await harness.Store.InstrumentPrices.Where(p => p.InstrumentId == instrument).ToListAsync();
        var expected = Weekdays(Today.AddDays(-40), Today);
        Assert.Equal(expected.Count, run.PricesInserted);
        Assert.Equal(expected, prices.Select(p => p.TradingDate).Order().ToList());
        Assert.All(prices, p => Assert.Equal(FakePrimarySource.DeclaredKey, p.SourceKey));
        Assert.All(prices, p => Assert.Equal(100m + p.TradingDate.Day, p.Close));
    }

    [Fact]
    public async Task The_credential_travels_in_the_declared_query_parameter_over_https_to_the_pinned_host()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1, symbol: "EQNR.OL");

        await harness.Sync.RunScheduledIfDueAsync();

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal(FakePrimarySource.DeclaredHost, request.RequestUri.Host);
        Assert.Equal("/eod/EQNR.OL", request.RequestUri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        Assert.Equal(SyncHarness.Secret, query["api_token"]);
    }

    [Theory]
    [InlineData(SecretReadState.NotSet)]
    [InlineData(SecretReadState.Unreadable)]
    public async Task A_source_without_a_readable_credential_is_never_called(SecretReadState state)
    {
        await using var harness = new SyncHarness();
        harness.Secrets.Result = state == SecretReadState.NotSet ? SecretResult.NotSet : SecretResult.Unreadable;
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Empty(harness.Handler.Requests);
        Assert.Empty(await harness.Store.InstrumentPrices.ToListAsync());
    }

    [Fact]
    public async Task An_unreadable_credential_fails_the_run_instead_of_reporting_success()
    {
        await using var harness = new SyncHarness();
        harness.Secrets.Result = SecretResult.Unreadable;
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal(JobFailureCategory.Configuration, run.FailureCategory);
    }

    [Fact]
    public async Task An_unset_credential_is_not_a_failure()
    {
        await using var harness = new SyncHarness();
        harness.Secrets.Result = SecretResult.NotSet;
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.InstrumentsAttempted);
    }

    [Fact]
    public async Task A_binding_whose_symbol_changes_mid_run_keeps_its_new_state_and_gets_no_rows()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1, symbol: "OLD");
        harness.Handler.Respond = request =>
        {
            // An Admin replaces the binding while the request is in flight.
            harness.Mutate(store =>
            {
                var row = store.InstrumentPriceSourceBindings.Single();
                store.InstrumentPriceSourceBindings.Remove(row);
                store.SaveChanges();
                store.InstrumentPriceSourceBindings.Add(new InstrumentPriceSourceBinding
                {
                    InstrumentId = instrument, SourceKey = FakePrimarySource.DeclaredKey, SourceSymbol = "NEW", Priority = 1, Enabled = true,
                    UpdatedAtUtc = Now,
                });
                store.SaveChanges();
            });
            return ScriptedPriceHandler.WeekdayCloses("NOK")(request);
        };

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.NotEqual(JobFailureCategory.Internal, run.FailureCategory);
        Assert.Empty(await harness.Store.InstrumentPrices.ToListAsync());
        var binding = await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey);
        Assert.Equal("NEW", binding.SourceSymbol);
        Assert.Null(binding.NewestFetched);
        Assert.Null(binding.BackfilledFrom);
    }

    [Fact]
    public async Task A_binding_removed_mid_run_does_not_fail_the_run_for_other_instruments()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var removed = await harness.AddInstrumentAsync("AAA");
        var kept = await harness.AddInstrumentAsync("BBB");
        await harness.BindAsync(removed, FakePrimarySource.DeclaredKey, 1, symbol: "AAA");
        await harness.BindAsync(kept, FakePrimarySource.DeclaredKey, 1, symbol: "BBB");
        harness.Handler.Respond = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/AAA", StringComparison.Ordinal))
            {
                harness.Mutate(store =>
                {
                    store.InstrumentPriceSourceBindings.Remove(store.InstrumentPriceSourceBindings.Single(b => b.InstrumentId == removed));
                    store.SaveChanges();
                });
            }

            return ScriptedPriceHandler.WeekdayCloses("NOK")(request);
        };

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.NotEqual(JobFailureCategory.Internal, run.FailureCategory);
        Assert.Empty(await harness.Store.InstrumentPrices.Where(p => p.InstrumentId == removed).ToListAsync());
        Assert.NotEmpty(await harness.Store.InstrumentPrices.Where(p => p.InstrumentId == kept).ToListAsync());
        Assert.Equal(Today, (await harness.BindingAsync(kept, FakePrimarySource.DeclaredKey)).NewestFetched);
    }

    [Fact]
    public async Task Each_span_commits_its_watermark_so_a_run_that_dies_later_keeps_it()
    {
        await using var harness = new SyncHarness(historyDays: 40);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        using var stopping = new CancellationTokenSource();
        var calls = 0;
        harness.Handler.Respond = request =>
        {
            if (++calls == 1)
            {
                return ScriptedPriceHandler.WeekdayCloses("NOK")(request);
            }

            // The worker stops during the backfill span.
            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        };

        var runId = await harness.Sync.RunScheduledIfDueAsync(stopping.Token);

        var run = await harness.RunAsync(runId!.Value);
        Assert.Equal(JobFailureCategory.Cancelled, run.FailureCategory);
        var binding = await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey);
        Assert.Equal(Today, binding.NewestFetched);
        Assert.Equal(Today.AddDays(-29), binding.BackfilledFrom);
        Assert.NotEmpty(await harness.Store.InstrumentPrices.ToListAsync());
    }

    [Fact]
    public async Task A_failing_binding_falls_back_to_the_next_one()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = request => request.RequestUri!.Host == FakePrimarySource.DeclaredHost
            ? ScriptedPriceHandler.Status(HttpStatusCode.NotFound)
            : ScriptedPriceHandler.WeekdayCloses("NOK")(request);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        await harness.BindAsync(instrument, FakeSecondarySource.DeclaredKey, 2);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.FallbacksUsed);
        var prices = await harness.Store.InstrumentPrices.ToListAsync();
        Assert.NotEmpty(prices);
        Assert.All(prices, p => Assert.Equal(FakeSecondarySource.DeclaredKey, p.SourceKey));
        var primary = await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey);
        Assert.Equal(JobFailureCategory.UnsupportedInput, primary.LastFailure);
        Assert.Equal(1, primary.ConsecutiveFailures);
        Assert.Null(primary.NewestFetched);
    }

    [Fact]
    public async Task A_rate_limited_source_backs_off_for_its_retry_after_and_is_not_retried()
    {
        await using var harness = new SyncHarness();
        harness.Handler.Respond = _ => ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2));
        var first = await harness.AddInstrumentAsync("EQNR");
        var second = await harness.AddInstrumentAsync("DNB");
        await harness.BindAsync(first, FakePrimarySource.DeclaredKey, 1);
        await harness.BindAsync(second, FakePrimarySource.DeclaredKey, 1);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        // 429 is never retried in-request, and the source is unusable for the rest of the run.
        Assert.Single(harness.Handler.Requests);
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal(JobFailureCategory.ProviderRateLimited, run.FailureCategory);
        var state = await harness.Store.InstrumentPriceSourceStates.AsNoTracking().SingleAsync();
        Assert.Equal(Now.AddHours(2), state.BackOffUntilUtc);
        Assert.Equal(JobFailureCategory.ProviderRateLimited, state.BackOffReason);

        // Still inside the back-off: a later run makes no request.
        harness.Clock.Now = Now.AddHours(1);
        harness.Handler.Requests.Clear();
        await harness.ExecuteManualAsync();
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(100_000, 86_400)]
    public async Task A_retry_after_is_clamped_to_the_back_off_bounds(int retryAfterSeconds, int expectedSeconds)
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = _ => ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(retryAfterSeconds));
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);

        await harness.Sync.RunScheduledIfDueAsync();

        var state = await harness.Store.InstrumentPriceSourceStates.AsNoTracking().SingleAsync();
        Assert.Equal(Now.AddSeconds(expectedSeconds), state.BackOffUntilUtc);
    }

    [Fact]
    public async Task An_unauthorised_response_marks_the_source_unusable_for_the_run()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = _ => ScriptedPriceHandler.Status(HttpStatusCode.Unauthorized);
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);
        await harness.BindAsync(await harness.AddInstrumentAsync("DNB"), FakePrimarySource.DeclaredKey, 1);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Single(harness.Handler.Requests);
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal(JobFailureCategory.Configuration, run.FailureCategory);
    }

    [Fact]
    public async Task A_server_error_is_retried_twice_then_counts_towards_the_unavailable_back_off()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = _ => ScriptedPriceHandler.Status(HttpStatusCode.ServiceUnavailable);
        var instruments = new List<Guid>();
        foreach (var symbol in new[] { "AAA", "BBB", "CCC", "DDD" })
        {
            var id = await harness.AddInstrumentAsync(symbol);
            instruments.Add(id);
            await harness.BindAsync(id, FakePrimarySource.DeclaredKey, 1);
        }

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        // Three instruments × (1 + 2 retries); the third consecutive unavailable span backs the source off for 1 h,
        // so the fourth instrument is never requested.
        Assert.Equal(9, harness.Handler.Requests.Count);
        Assert.Equal(9, run.RequestsMade);
        Assert.Equal(3, run.InstrumentsFailed);
        Assert.Equal(JobFailureCategory.ProviderUnavailable, run.FailureCategory);
        var state = await harness.Store.InstrumentPriceSourceStates.AsNoTracking().SingleAsync();
        Assert.Equal(Now + InstrumentPriceSyncConstants.UnavailableBackoff, state.BackOffUntilUtc);
    }

    [Fact]
    public async Task The_daily_cap_stops_a_source_and_fails_closed()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Limits = harness.Primary.Limits with { MaxRequestsPerDay = 1, MaxRequestsPerRun = 1 };
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);
        await harness.BindAsync(await harness.AddInstrumentAsync("DNB"), FakePrimarySource.DeclaredKey, 1);

        await harness.Sync.RunScheduledIfDueAsync();
        harness.Clock.Now = Now.AddMinutes(30);
        await harness.ExecuteManualAsync();

        Assert.Single(harness.Handler.Requests);
        var state = await harness.Store.InstrumentPriceSourceStates.AsNoTracking().SingleAsync();
        Assert.Equal(Today, state.RequestDay);
        Assert.Equal(1, state.RequestCount);

        // A new Oslo day rolls the counter over.
        harness.Clock.Now = Now.AddDays(1);
        await harness.ExecuteManualAsync();
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("NOk")]
    [InlineData("GBp")]
    public async Task A_batch_in_another_currency_is_rejected_whole(string currency)
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = ScriptedPriceHandler.WeekdayCloses(currency);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Empty(await harness.Store.InstrumentPrices.ToListAsync());
        Assert.Null((await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey)).NewestFetched);
    }

    [Fact]
    public async Task A_batch_with_a_date_outside_the_window_is_rejected_whole()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = _ => ScriptedPriceHandler.Json(new[]
        {
            new { date = Today.ToString("yyyy-MM-dd"), close = 10m, currency = "NOK" },
            new { date = Today.AddDays(1).ToString("yyyy-MM-dd"), close = 11m, currency = "NOK" },
        });
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Empty(await harness.Store.InstrumentPrices.ToListAsync());
    }

    [Fact]
    public async Task A_source_that_throws_fails_the_instrument_not_the_run()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Throw = new InvalidOperationException("boom");
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        await harness.BindAsync(instrument, FakeSecondarySource.DeclaredKey, 2);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.FallbacksUsed);
        Assert.Equal(JobFailureCategory.Internal, (await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey)).LastFailure);
    }

    [Fact]
    public async Task A_repeat_run_inserts_nothing_twice_and_leaves_manual_prices_alone()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        harness.Store.InstrumentPrices.Add(new InstrumentPrice
        {
            InstrumentPriceId = Guid.NewGuid(), InstrumentId = instrument, TradingDate = Today, Close = 1m, CreatedAtUtc = Now,
        });
        await harness.Store.SaveChangesAsync();

        await harness.Sync.RunScheduledIfDueAsync();
        var afterFirst = await harness.Store.InstrumentPrices.CountAsync();

        // Rewind the watermark so the second run fetches the same window again.
        var binding = await harness.Store.InstrumentPriceSourceBindings.SingleAsync();
        binding.NewestFetched = null;
        binding.BackfilledFrom = null;
        await harness.Store.SaveChangesAsync();
        harness.Store.ChangeTracker.Clear();
        harness.Clock.Now = Now.AddMinutes(30);
        var run = await harness.RunAsync((await harness.ExecuteManualAsync())!.Value);

        Assert.Equal(0, run.PricesInserted);
        Assert.Equal(afterFirst, await harness.Store.InstrumentPrices.CountAsync());
        var manual = await harness.Store.InstrumentPrices.SingleAsync(p => p.SourceKey == null);
        Assert.Equal(1m, manual.Close);
        Assert.Single(await harness.Store.InstrumentPrices.Where(p => p.SourceKey != null && p.TradingDate == Today).ToListAsync());
    }

    [Fact]
    public async Task No_log_line_carries_the_credential_the_source_symbol_or_the_uri()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var calls = 0;
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1, symbol: "SENTINELSYMBOL");
        await harness.BindAsync(await harness.AddInstrumentAsync("DNB"), FakeSecondarySource.DeclaredKey, 1, symbol: "SENTINELSYMBOL");
        harness.Handler.Respond = request => request.RequestUri!.Host == FakeSecondarySource.DeclaredHost
            ? ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests)
            : (++calls == 1 ? ScriptedPriceHandler.Status(HttpStatusCode.ServiceUnavailable) : ScriptedPriceHandler.WeekdayCloses("NOK")(request));

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.NotEmpty(harness.Logs.Lines);
        Assert.All(harness.Logs.Lines, line =>
        {
            Assert.DoesNotContain(SyncHarness.Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINELSYMBOL", line, StringComparison.Ordinal);
            Assert.DoesNotContain("prices.example", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_schedule_is_due_at_once_then_after_the_interval()
    {
        await using var harness = new SyncHarness(historyDays: 5, intervalHours: 24);
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);

        Assert.Equal(Now, await harness.Sync.NextRunAtUtcAsync());
        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Equal(Now.AddHours(24), await harness.Sync.NextRunAtUtcAsync());
        Assert.Null(await harness.Sync.RunScheduledIfDueAsync());
    }

    [Fact]
    public async Task A_run_that_inserted_while_a_binding_is_still_behind_catches_up_in_five_minutes()
    {
        await using var harness = new SyncHarness(historyDays: 365, intervalHours: 24);
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Equal(Now.AddMinutes(InstrumentPriceSyncConstants.CatchUpDelayMinutes), await harness.Sync.NextRunAtUtcAsync());
    }

    [Fact]
    public async Task Three_failed_runs_double_the_interval_up_to_the_cap()
    {
        await using var harness = new SyncHarness(historyDays: 5, intervalHours: 24);
        await harness.BindAsync(await harness.AddInstrumentAsync("EQNR"), FakePrimarySource.DeclaredKey, 1);
        for (var i = 0; i < 5; i++)
        {
            harness.Store.JobRuns.Add(new InstrumentPriceSyncJobRun
            {
                JobRunId = Guid.NewGuid(),
                Trigger = JobRunTrigger.Scheduled,
                Status = JobRunStatus.Failed,
                StartedAtUtc = Now.AddHours(-i - 1),
                FinishedAtUtc = Now.AddHours(-i),
                LastActivityAtUtc = Now.AddHours(-i),
            });
        }

        await harness.Store.SaveChangesAsync();

        // Five consecutive failures: 24 h × 2^(5-3+1) = 192 h, capped at 168 h.
        Assert.Equal(Now.AddHours(InstrumentPriceSyncConstants.MaxBackoffHours), await harness.Sync.NextRunAtUtcAsync());
    }

    private static List<DateOnly> Weekdays(DateOnly from, DateOnly to)
    {
        var days = new List<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(day);
            }
        }

        return days;
    }

    /// <summary>A clock fixed at a settable instant whose timers are real, so the transport's short retry delays elapse.</summary>
    internal sealed class SettableClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    /// <summary>Captures every formatted log line, scopes and exception text included.</summary>
    internal sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                owner.Lines.Enqueue(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(formatter(state, exception) + " " + exception);
        }
    }

    internal sealed class SettableSecrets : ISecretSettingsReader
    {
        public SecretResult Result { get; set; } = SecretResult.Found(SyncHarness.Secret);

        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    internal sealed class FixedSettings(InstrumentPriceSettings value) : IInstrumentPriceSettingsLookup
    {
        public Task<InstrumentPriceSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
    }

    internal sealed class SyncHarness : IAsyncDisposable
    {
        public const string Secret = "fake-secret-value";

        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        public SyncHarness(bool registerSources = true, int historyDays = 30, int intervalHours = 24)
        {
            var services = new ServiceCollection();
            var store = Guid.NewGuid().ToString();
            services.AddScoped(_ => new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(store).Options));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ISecretSettingsReader>(Secrets);
            services.AddSingleton<IInstrumentPriceSettingsLookup>(new FixedSettings(new InstrumentPriceSettings(5, intervalHours, historyDays, false)));
            services.AddLogging(logging => logging.AddProvider(Logs));
            if (registerSources)
            {
                services.AddInstrumentPriceSource<FakePrimarySource>();
                services.AddInstrumentPriceSource<FakeSecondarySource>();
                foreach (var key in new[] { FakePrimarySource.DeclaredKey, FakeSecondarySource.DeclaredKey })
                {
                    services.AddHttpClient(SourceHttpLimits.ClientName(key)).ConfigurePrimaryHttpMessageHandler(() => Handler);
                }
            }
            else
            {
                services.AddInstrumentPriceSync();
            }

            provider = services.BuildServiceProvider();
            scope = provider.CreateAsyncScope();
        }

        public SettableClock Clock { get; } = new(Now);

        public SettableSecrets Secrets { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public ScriptedPriceHandler Handler { get; } = new();

        public FakePrimarySource Primary => provider.GetRequiredService<FakePrimarySource>();

        public OdysseyContext Store => scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        public InstrumentPriceSyncService Sync => scope.ServiceProvider.GetRequiredService<InstrumentPriceSyncService>();

        public async Task<Guid> AddInstrumentAsync(string symbol, bool archived = false)
        {
            var id = Guid.NewGuid();
            Store.Instruments.Add(new Instrument
            {
                InstrumentId = id,
                Symbol = symbol,
                MicCode = "XOSL",
                Name = symbol,
                Type = InstrumentType.Stock,
                CurrencyCode = "NOK",
                ListingKey = $"{symbol}@XOSL",
                Archived = archived ? Now : null,
                CreatedAtUtc = Now,
            });
            await Store.SaveChangesAsync();
            return id;
        }

        public async Task BindAsync(Guid instrumentId, string sourceKey, int priority, bool enabled = true, string? symbol = null)
        {
            Store.InstrumentPriceSourceBindings.Add(new InstrumentPriceSourceBinding
            {
                InstrumentId = instrumentId,
                SourceKey = sourceKey,
                SourceSymbol = symbol ?? "SYM",
                Priority = priority,
                Enabled = enabled,
                UpdatedAtUtc = Now,
            });
            await Store.SaveChangesAsync();
        }

        /// <summary>Changes the store from a fresh scope, as another request would while a run is in flight.</summary>
        public void Mutate(Action<OdysseyContext> change)
        {
            using var other = provider.CreateScope();
            change(other.ServiceProvider.GetRequiredService<OdysseyContext>());
        }

        public Task<InstrumentPriceSourceBinding> BindingAsync(Guid instrumentId, string sourceKey) =>
            Store.InstrumentPriceSourceBindings.AsNoTracking().SingleAsync(b => b.InstrumentId == instrumentId && b.SourceKey == sourceKey);

        public Task<InstrumentPriceSyncJobRun> RunAsync(Guid runId) =>
            Store.Set<InstrumentPriceSyncJobRun>().AsNoTracking().SingleAsync(r => r.JobRunId == runId);

        /// <summary>Starts a manual run the way the API does, then claims and executes it as the worker would.</summary>
        public async Task<Guid?> ExecuteManualAsync()
        {
            Store.ChangeTracker.Clear();
            var at = Clock.Now;
            var run = new InstrumentPriceSyncJobRun
            {
                JobRunId = Guid.NewGuid(),
                Trigger = JobRunTrigger.Manual,
                Status = JobRunStatus.Running,
                StartedAtUtc = at,
                LastActivityAtUtc = at,
            };
            Store.JobRuns.Add(run);
            await Store.SaveChangesAsync();
            Store.ChangeTracker.Clear();

            var claimed = await Sync.ClaimRequestedRunAsync();
            Assert.Equal(run.JobRunId, claimed);
            await Sync.ExecuteAsync(claimed!.Value, CancellationToken.None);
            Store.ChangeTracker.Clear();
            return claimed;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
