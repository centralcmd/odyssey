using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Finance.Instruments.Sources.Tiingo;
using Odyssey.Core.Finance.Instruments.Sync;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared.InstrumentPrices;
using Xunit;
using SyncHarness = Odyssey.Core.Tests.InstrumentPriceSyncTests.SyncHarness;

namespace Odyssey.Core.Tests;

/// <summary>
/// The Phase 1 amendments issue #447 lands (A1, A6–A9), and the Tiingo source driven end to end through the sync: the
/// hourly cap, the returned-date watermark, request spacing, deferral, the same-day skip, and the credential's reach.
/// </summary>
public class InstrumentPriceSyncAmendmentTests
{
    /// <summary>A Friday, 10:00 UTC (12:00 in Oslo).</summary>
    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 23);

    // ── A1: the hourly cap ─────────────────────────────────────────────────────────────────────

    private sealed class QuotaHarness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        public QuotaHarness()
        {
            var store = Guid.NewGuid().ToString();
            provider = new ServiceCollection()
                .AddScoped(_ => new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(store).Options))
                .BuildServiceProvider();
            Quota = new SourceRequestQuota(provider.GetRequiredService<IServiceScopeFactory>(), Clock);
        }

        public InstrumentPriceSyncTests.SettableClock Clock { get; } = new(Now);

        public SourceRequestQuota Quota { get; }

        public async Task<InstrumentPriceSourceState> StateAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().InstrumentPriceSourceStates.AsNoTracking().SingleAsync();
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private static readonly InstrumentPriceSourceLimits HourlyLimits = new(30, TimeSpan.Zero, MaxRequestsPerRun: 3, MaxRequestsPerDay: 5, MaxRequestsPerHour: 3);

    [Fact]
    public async Task One_reservation_counts_both_caps_and_names_the_hourly_one_when_reached()
    {
        await using var harness = new QuotaHarness();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(QuotaReservation.Reserved, await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None));
        }

        Assert.Equal(QuotaReservation.HourCapReached, await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None));
        var state = await harness.StateAsync();
        Assert.Equal(3, state.RequestCount);
        Assert.Equal(3, state.RequestHourCount);
        Assert.Equal(Now, state.RequestHourUtc);
    }

    [Fact]
    public async Task The_daily_cap_is_named_when_it_is_the_one_reached()
    {
        await using var harness = new QuotaHarness();
        for (var hour = 0; hour < 2; hour++)
        {
            harness.Clock.Now = Now.AddHours(hour);
            while (await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None) == QuotaReservation.Reserved)
            {
            }
        }

        Assert.Equal(QuotaReservation.DayCapReached, await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None));
        Assert.Equal(5, (await harness.StateAsync()).RequestCount);
    }

    [Fact]
    public async Task The_hour_bucket_only_moves_forward()
    {
        await using var harness = new QuotaHarness();
        harness.Clock.Now = Now.AddMinutes(30);
        await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None);
        await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None);

        // A replica whose clock lags by an hour counts against the newer bucket instead of resetting it.
        harness.Clock.Now = Now.AddMinutes(-30);
        Assert.Equal(QuotaReservation.Reserved, await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None));
        Assert.Equal(QuotaReservation.HourCapReached, await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None));
        var state = await harness.StateAsync();
        Assert.Equal(Now, state.RequestHourUtc);
        Assert.Equal(3, state.RequestHourCount);

        // The next hour rolls the bucket over.
        harness.Clock.Now = Now.AddHours(1);
        Assert.Equal(QuotaReservation.Reserved, await harness.Quota.TryReserveAsync("s", HourlyLimits, CancellationToken.None));
        state = await harness.StateAsync();
        Assert.Equal(Now.AddHours(1), state.RequestHourUtc);
        Assert.Equal(1, state.RequestHourCount);
    }

    [Fact]
    public async Task Consecutive_catch_up_runs_inside_one_hour_never_exceed_the_hourly_cap()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Limits = new InstrumentPriceSourceLimits(30, TimeSpan.Zero, MaxRequestsPerRun: 4, MaxRequestsPerDay: 100, MaxRequestsPerHour: 4);
        for (var i = 0; i < 12; i++)
        {
            await harness.BindAsync(await harness.AddInstrumentAsync($"I{i:D2}"), FakePrimarySource.DeclaredKey, 1, symbol: $"I{i:D2}");
        }

        await harness.Sync.RunScheduledIfDueAsync();
        for (var minutes = 5; minutes < 60; minutes += 5)
        {
            harness.Clock.Now = Now.AddMinutes(minutes);
            await harness.ExecuteManualAsync();
        }

        Assert.Equal(4, harness.Handler.Requests.Count);

        // The next hour's bucket is fresh. One run spends at most the forward half of its budget on new bindings.
        harness.Clock.Now = Now.AddHours(1);
        await harness.ExecuteManualAsync();
        Assert.Equal(6, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task The_status_reports_the_hourly_count_and_when_a_reached_cap_resets()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Limits = new InstrumentPriceSourceLimits(30, TimeSpan.Zero, MaxRequestsPerRun: 2, MaxRequestsPerDay: 100, MaxRequestsPerHour: 2);
        for (var i = 0; i < 5; i++)
        {
            await harness.BindAsync(await harness.AddInstrumentAsync($"I{i}"), FakePrimarySource.DeclaredKey, 1, symbol: $"I{i}");
        }

        var before = (await harness.Admin.GetStatusAsync()).Sources.Single(s => s.Key == FakePrimarySource.DeclaredKey);
        Assert.Equal(2, before.MaxRequestsPerHour);
        Assert.Equal(0, before.RequestsThisHour);
        Assert.Null(before.HourlyLimitResetsAtUtc);

        await harness.Sync.RunScheduledIfDueAsync();
        harness.Clock.Now = Now.AddMinutes(5);
        await harness.ExecuteManualAsync();

        var capped = (await harness.Admin.GetStatusAsync()).Sources.Single(s => s.Key == FakePrimarySource.DeclaredKey);
        Assert.Equal(2, capped.RequestsThisHour);
        Assert.Equal(Now.AddHours(1), capped.HourlyLimitResetsAtUtc);

        harness.Clock.Now = Now.AddHours(1);
        var next = (await harness.Admin.GetStatusAsync()).Sources.Single(s => s.Key == FakePrimarySource.DeclaredKey);
        Assert.Equal(0, next.RequestsThisHour);
        Assert.Null(next.HourlyLimitResetsAtUtc);
    }

    [Fact]
    public async Task The_status_carries_tiingos_symbol_example_and_data_processing_note()
    {
        await using var harness = new SyncHarness(tiingo: true);

        var tiingo = (await harness.Admin.GetStatusAsync()).Sources.Single(s => s.Key == TiingoPriceSource.SourceKey);

        Assert.Equal("BRK-B", tiingo.SymbolExample);
        Assert.StartsWith("Tiingo is a US company.", tiingo.DataProcessingNote, StringComparison.Ordinal);
        Assert.Equal(25, tiingo.MaxRequestsPerHour);
        Assert.Null((await harness.Admin.GetStatusAsync()).Sources.Single(s => s.Key == FakePrimarySource.DeclaredKey).SymbolExample);
    }

    [Fact]
    public void The_catalogue_refuses_an_hourly_cap_below_the_per_run_budget()
    {
        var source = new FakePrimarySource { Limits = new InstrumentPriceSourceLimits(30, TimeSpan.Zero, 10, 100, MaxRequestsPerHour: 5) };
        Assert.Throws<InvalidOperationException>(() => new InstrumentPriceSourceCatalogue([source]));
    }

    // ── A4: a header credential's compiled prefix ──────────────────────────────────────────────

    [Theory]
    [InlineData(null, "s3cret")]
    [InlineData("Token ", "Token s3cret")]
    [InlineData("Bearer ", "Bearer s3cret")]
    public async Task A_header_credential_is_the_prefix_then_the_stored_value(string? prefix, string expected)
    {
        var source = new HeaderSource(prefix);
        var handler = new ScriptedPriceHandler { Respond = _ => ScriptedPriceHandler.Json(Array.Empty<object>()) };
        var http = new SourceHttp(source, new HttpClient(handler) { BaseAddress = new Uri("https://header.example/") },
            new FixedSecret("s3cret"), new Unlimited(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System);

        await http.GetAsync(http.BuildUri(["x"]), CancellationToken.None);

        Assert.Equal([expected], handler.Requests.Single().Headers.GetValues("X-Key"));
    }

    private sealed class HeaderSource(string? prefix) : IInstrumentPriceSource
    {
        public string Key => "header-source";

        public string DisplayName => "Header";

        public string Attribution => "Header";

        public string Host => "header.example";

        public CredentialPlacement CredentialPlacement => new CredentialPlacement.Header("X-Key", prefix);

        public IReadOnlySet<InstrumentType> SupportedTypes { get; } = new HashSet<InstrumentType> { InstrumentType.Stock };

        public InstrumentPriceSourceLimits Limits { get; } = new(30, TimeSpan.Zero, 10, 10);

        public IReadOnlyList<string> SecretKeys { get; } = ["k"];

        public Task<SourceFetchResult> FetchClosesAsync(SourceFetchRequest request, ISourceHttp http, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedSecret(string value) : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(SecretResult.Found(value));
    }

    private sealed class Unlimited : ISourceRequestQuota
    {
        public Task<QuotaReservation> TryReserveAsync(string sourceKey, InstrumentPriceSourceLimits limits, CancellationToken cancellationToken) =>
            Task.FromResult(QuotaReservation.Reserved);
    }

    // ── A6 (#468): the watermark records what came back ────────────────────────────────────────

    [Fact]
    public async Task A_close_published_after_the_run_is_fetched_by_the_next_one()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        var omitToday = true;
        harness.Handler.Respond = request =>
        {
            var full = ScriptedPriceHandler.WeekdayCloses("NOK")(request);
            if (!omitToday)
            {
                return full;
            }

            var rows = JsonSerializer.Deserialize<List<FakeInstrumentPriceSourceBase.Row>>(full.Content.ReadAsStream(), JsonSerializerOptions.Web)!;
            return ScriptedPriceHandler.Json(rows.Where(r => r.Date != Today).Select(r => new { date = r.Date.ToString("yyyy-MM-dd"), close = r.Close, currency = r.Currency }));
        };

        await harness.Sync.RunScheduledIfDueAsync();
        Assert.Equal(Today.AddDays(-1), (await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey)).NewestFetched);

        omitToday = false;
        harness.Clock.Now = Now.AddHours(10);
        await harness.ExecuteManualAsync();

        Assert.Equal(Today, (await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey)).NewestFetched);
        Assert.Contains(await harness.Store.InstrumentPrices.ToListAsync(), p => p.TradingDate == Today);
    }

    [Fact]
    public async Task An_empty_batch_leaves_the_watermark_unchanged()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        await SetWatermarkAsync(harness, Today.AddDays(-1));
        harness.Handler.Respond = _ => ScriptedPriceHandler.Json(Array.Empty<object>());

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Single(harness.Handler.Requests);
        Assert.Equal(Today.AddDays(-1), (await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey)).NewestFetched);
    }

    [Fact]
    public async Task A_window_older_than_five_days_still_advances()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Limits = harness.Primary.Limits with { MaxDaysPerRequest = 4 };
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        await SetWatermarkAsync(harness, Today.AddDays(-20));
        harness.Handler.Respond = _ => ScriptedPriceHandler.Json(Array.Empty<object>());

        await harness.Sync.RunScheduledIfDueAsync();

        // Every day more than five days old counts as fetched; the last five are asked for again later.
        Assert.Equal(Today.AddDays(-6), (await harness.BindingAsync(instrument, FakePrimarySource.DeclaredKey)).NewestFetched);
        // [-19,-16], [-15,-12], [-11,-8] settle whole; [-7,-4] settles up to -6 and stops the forward fill.
        Assert.Equal(4, harness.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, new int[0], -5, 0, null)]
    [InlineData(null, new[] { -2 }, -5, 0, -2)]
    [InlineData(-10, new int[0], -9, 0, -6)]
    [InlineData(-30, new int[0], -29, -20, -20)]
    [InlineData(-30, new[] { -25 }, -29, -20, -20)]
    [InlineData(-3, new int[0], -2, 0, null)]
    [InlineData(-3, new[] { -1 }, -2, 0, -1)]
    public void The_forward_watermark_rule(int? current, int[] returned, int from, int to, int? expected)
    {
        var closes = returned.Select(d => new FetchedClose(Today.AddDays(d), 1m, "NOK")).ToList();

        var mark = InstrumentPriceSyncService.ForwardWatermark(
            current is { } c ? Today.AddDays(c) : null, closes, Today.AddDays(from), Today.AddDays(to), Today);

        Assert.Equal(expected is { } e ? Today.AddDays(e) : null, mark);
    }

    private static async Task SetWatermarkAsync(SyncHarness harness, DateOnly newest)
    {
        var binding = await harness.Store.InstrumentPriceSourceBindings.SingleAsync();
        binding.NewestFetched = newest;
        binding.BackfilledFrom = Today.AddDays(-30);
        await harness.Store.SaveChangesAsync();
        harness.Store.ChangeTracker.Clear();
    }

    // ── A7 (#469): request spacing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Consecutive_requests_to_one_source_are_spaced_across_bindings_and_retries()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Clock.Jumping = true;
        harness.Primary.Limits = harness.Primary.Limits with { MinRequestInterval = TimeSpan.FromSeconds(2) };
        var sentAt = new List<DateTime>();
        var calls = 0;
        harness.Handler.Respond = request =>
        {
            sentAt.Add(harness.Clock.Now);
            // The first request fails once and is retried after a back-off far shorter than the spacing.
            return ++calls == 1 ? ScriptedPriceHandler.Status(HttpStatusCode.ServiceUnavailable) : ScriptedPriceHandler.WeekdayCloses("NOK")(request);
        };
        for (var i = 0; i < 4; i++)
        {
            await harness.BindAsync(await harness.AddInstrumentAsync($"I{i}"), FakePrimarySource.DeclaredKey, 1, symbol: $"I{i}");
        }

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Equal(5, sentAt.Count);
        foreach (var (earlier, later) in sentAt.Zip(sentAt.Skip(1)))
        {
            Assert.True(later - earlier >= TimeSpan.FromSeconds(2), $"Requests {earlier:O} and {later:O} are closer than 2 s.");
        }
    }

    [Fact]
    public async Task The_pacer_waits_on_the_time_provider()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Now));
        var pacer = new SourceRequestPacer(TimeSpan.FromSeconds(2), clock);
        await pacer.WaitTurnAsync(CancellationToken.None);
        pacer.MarkSent();

        var second = pacer.WaitTurnAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(0.1));
        await second;
        Assert.Equal(1, pacer.Sent);
    }

    // ── A8: a cap defers ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_capped_run_defers_without_failing_or_falling_back_and_every_binding_is_filled_over_later_runs()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Limits = new InstrumentPriceSourceLimits(30, TimeSpan.Zero, MaxRequestsPerRun: 25, MaxRequestsPerDay: 600, MaxRequestsPerHour: 25);
        var instruments = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            var id = await harness.AddInstrumentAsync($"I{i:D2}");
            instruments.Add(id);
            await harness.BindAsync(id, FakePrimarySource.DeclaredKey, 1, symbol: $"I{i:D2}");
            await harness.BindAsync(id, FakeSecondarySource.DeclaredKey, 2, symbol: $"I{i:D2}");
        }

        var first = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.PartiallySucceeded, first.Status);
        Assert.Null(first.FailureCategory);
        Assert.Equal(0, first.InstrumentsFailed);
        Assert.Equal(0, first.FallbacksUsed);
        Assert.Equal(13, first.InstrumentsSucceeded);
        Assert.Equal(37, first.InstrumentsDeferred);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.RequestUri!.Host == FakeSecondarySource.DeclaredHost);
        Assert.All(await harness.Store.InstrumentPriceSourceBindings.AsNoTracking().Where(b => b.NewestFetched == null).ToListAsync(),
            b => Assert.Null(b.LastFailure));

        // The next run is due at the start of the next quota hour, not after the 24 h interval.
        Assert.Equal(new DateTime(2026, 10, 23, 11, 0, 0, DateTimeKind.Utc), await harness.Sync.NextRunAtUtcAsync());

        var runs = 1;
        while (await harness.Store.InstrumentPriceSourceBindings.AnyAsync(b => b.SourceKey == FakePrimarySource.DeclaredKey && b.NewestFetched == null))
        {
            Assert.True(++runs <= 6, "The watchlist never filled.");
            harness.Clock.Now = (await harness.Sync.NextRunAtUtcAsync())!.Value;
            var before = harness.Handler.Requests.Count;
            var runId = await harness.Sync.RunScheduledIfDueAsync();
            Assert.NotNull(runId);
            var run = await harness.RunAsync(runId!.Value);
            Assert.True(run.RequestsMade <= 25);
            Assert.Equal(run.RequestsMade, harness.Handler.Requests.Count - before);
            Assert.Equal(0, run.InstrumentsFailed);
            harness.Store.ChangeTracker.Clear();
        }

        Assert.Equal(4, runs);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.RequestUri!.Host == FakeSecondarySource.DeclaredHost);
    }

    [Fact]
    public async Task A_run_with_only_deferrals_and_nothing_attempted_succeeds()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Primary.Limits = new InstrumentPriceSourceLimits(30, TimeSpan.Zero, MaxRequestsPerRun: 2, MaxRequestsPerDay: 2, MaxRequestsPerHour: 2);
        await harness.BindAsync(await harness.AddInstrumentAsync("A"), FakePrimarySource.DeclaredKey, 1, symbol: "A");
        await harness.BindAsync(await harness.AddInstrumentAsync("B"), FakePrimarySource.DeclaredKey, 1, symbol: "B");
        // Another replica already spent today's requests.
        harness.Store.InstrumentPriceSourceStates.Add(new InstrumentPriceSourceState
        {
            SourceKey = FakePrimarySource.DeclaredKey, RequestDay = Today, RequestCount = 2, UpdatedAtUtc = Now,
        });
        await harness.Store.SaveChangesAsync();

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.InstrumentsDeferred);
        Assert.Equal(0, run.InstrumentsAttempted);
        Assert.Empty(harness.Handler.Requests);
        Assert.All(await harness.Store.InstrumentPriceSourceBindings.AsNoTracking().ToListAsync(), b =>
        {
            Assert.Null(b.LastFailure);
            Assert.Null(b.NewestFetched);
        });
    }

    // ── A9: a definitive failure waits for tomorrow ────────────────────────────────────────────

    [Fact]
    public async Task An_unsupported_binding_is_skipped_until_the_next_oslo_day_and_an_edit_clears_the_skip()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = _ => ScriptedPriceHandler.Status(HttpStatusCode.NotFound);
        var instrument = await harness.AddInstrumentAsync("BAD");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1, symbol: "BAD");

        await harness.Sync.RunScheduledIfDueAsync();
        Assert.Single(harness.Handler.Requests);

        harness.Clock.Now = Now.AddHours(6);
        await harness.ExecuteManualAsync();
        Assert.Single(harness.Handler.Requests);
        // The Admin surface says when it is asked again.
        Assert.Equal(Today.AddDays(1), (await harness.Admin.GetBindingsAsync(instrument))!.Single().RetryOn);

        // Re-saving an untouched list keeps the skip.
        var list = new InstrumentPriceBindingsUpdate
        {
            Bindings = [new InstrumentPriceBindingInput { SourceKey = FakePrimarySource.DeclaredKey, SourceSymbol = "BAD", Enabled = true }],
        };
        await harness.Admin.ReplaceBindingsAsync(instrument, list, "admin");
        harness.Clock.Now = Now.AddHours(7);
        await harness.ExecuteManualAsync();
        Assert.Single(harness.Handler.Requests);

        // Disabling and re-enabling is an edit: the next run asks again.
        list.Bindings[0].Enabled = false;
        await harness.Admin.ReplaceBindingsAsync(instrument, list, "admin");
        harness.Clock.Now = Now.AddHours(8);
        list.Bindings[0].Enabled = true;
        await harness.Admin.ReplaceBindingsAsync(instrument, list, "admin");
        harness.Store.ChangeTracker.Clear();
        Assert.Null((await harness.Admin.GetBindingsAsync(instrument))!.Single().RetryOn);
        await harness.ExecuteManualAsync();
        Assert.Equal(2, harness.Handler.Requests.Count);

        // And a new Oslo day does too (Oslo is UTC+2 here, so 22:00 UTC is midnight).
        harness.Clock.Now = new DateTime(2026, 10, 23, 22, 0, 1, DateTimeKind.Utc);
        await harness.ExecuteManualAsync();
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_skipped_binding_still_falls_back_to_the_next_one()
    {
        await using var harness = new SyncHarness(historyDays: 5);
        harness.Handler.Respond = request => request.RequestUri!.Host == FakePrimarySource.DeclaredHost
            ? ScriptedPriceHandler.Status(HttpStatusCode.NotFound)
            : ScriptedPriceHandler.WeekdayCloses("NOK")(request);
        var instrument = await harness.AddInstrumentAsync("EQNR");
        await harness.BindAsync(instrument, FakePrimarySource.DeclaredKey, 1);
        await harness.BindAsync(instrument, FakeSecondarySource.DeclaredKey, 2);
        await harness.Sync.RunScheduledIfDueAsync();
        harness.Handler.Requests.Clear();

        harness.Clock.Now = Now.AddHours(1);
        await SetSecondaryWatermarkBackAsync(harness, instrument);
        await harness.ExecuteManualAsync();

        Assert.NotEmpty(harness.Handler.Requests);
        Assert.All(harness.Handler.Requests, r => Assert.Equal(FakeSecondarySource.DeclaredHost, r.RequestUri!.Host));
    }

    private static async Task SetSecondaryWatermarkBackAsync(SyncHarness harness, Guid instrument)
    {
        var binding = await harness.Store.InstrumentPriceSourceBindings.SingleAsync(b => b.InstrumentId == instrument && b.SourceKey == FakeSecondarySource.DeclaredKey);
        binding.NewestFetched = Today.AddDays(-2);
        await harness.Store.SaveChangesAsync();
        harness.Store.ChangeTracker.Clear();
    }

    // ── Tiingo through the sync ────────────────────────────────────────────────────────────────

    private static async Task<SyncHarness> TiingoHarnessAsync(params string[] symbols)
    {
        var harness = new SyncHarness(historyDays: 5, tiingo: true);
        harness.Clock.Jumping = true;
        harness.Handler.Respond = TiingoFixtures.Healthy;
        foreach (var symbol in symbols)
        {
            await harness.BindAsync(await harness.AddInstrumentAsync(symbol, currency: "USD"), TiingoPriceSource.SourceKey, 1, symbol: symbol);
        }

        return harness;
    }

    [Fact]
    public async Task Tiingo_fetches_a_new_binding_with_two_requests_then_one_per_span()
    {
        await using var harness = await TiingoHarnessAsync("AAPL");

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.RequestsMade);
        Assert.Equal(Weekdays(Today.AddDays(-5), Today), run.PricesInserted);
        Assert.All(await harness.Store.InstrumentPrices.ToListAsync(), p => Assert.Equal("tiingo", p.SourceKey));

        harness.Clock.Now = Now.AddDays(1).AddHours(8);
        await harness.ExecuteManualAsync();
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Tiingo_never_overshoots_the_run_budget_in_the_middle_of_a_span()
    {
        await using var harness = await TiingoHarnessAsync(Enumerable.Range(0, 20).Select(i => $"T{i:D2}").ToArray());

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        // The forward half of 25 is 13 wire requests: six bindings of two, then a metadata request that leaves the
        // seventh with no budget for its prices. It is deferred with nothing written.
        Assert.Equal(13, harness.Handler.Requests.Count);
        Assert.Equal(13, run.RequestsMade);
        Assert.Equal(6, run.InstrumentsSucceeded);
        Assert.Equal(14, run.InstrumentsDeferred);
        Assert.Equal(0, run.InstrumentsFailed);
        var bindings = await harness.Store.InstrumentPriceSourceBindings.AsNoTracking().ToListAsync();
        Assert.Equal(6, bindings.Count(b => b.NewestFetched == Today));
        Assert.All(bindings.Where(b => b.NewestFetched is null), b => Assert.Null(b.LastFailure));
    }

    [Fact]
    public async Task Tiingo_spaces_its_requests_two_seconds_apart()
    {
        await using var harness = await TiingoHarnessAsync("AAPL", "VTI", "BRK-B");
        var sentAt = new List<DateTime>();
        harness.Handler.Respond = request =>
        {
            sentAt.Add(harness.Clock.Now);
            return TiingoFixtures.Healthy(request);
        };

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Equal(6, sentAt.Count);
        Assert.All(sentAt.Zip(sentAt.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task After_a_token_refusal_no_further_request_goes_to_tiingo_in_that_run(HttpStatusCode status)
    {
        await using var harness = await TiingoHarnessAsync("AAPL", "VTI", "BRK-B");
        harness.Handler.Respond = _ => TiingoFixtures.File("error-403-invalid-token.json", status);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Single(harness.Handler.Requests);
        Assert.Equal(JobFailureCategory.Configuration, run.FailureCategory);
    }

    [Fact]
    public async Task A_per_symbol_refusal_leaves_tiingo_usable_for_other_instruments()
    {
        await using var harness = await TiingoHarnessAsync("AAPL", "VTI");
        harness.Handler.Respond = request => TiingoFixtures.TickerOf(request) == "AAPL" && TiingoFixtures.IsPrices(request)
            ? ScriptedPriceHandler.Status(HttpStatusCode.PaymentRequired)
            : TiingoFixtures.Healthy(request);

        var run = await harness.RunAsync((await harness.Sync.RunScheduledIfDueAsync())!.Value);

        Assert.Equal(JobRunStatus.PartiallySucceeded, run.Status);
        Assert.Equal(1, run.InstrumentsFailed);
        Assert.Equal(1, run.InstrumentsSucceeded);
        Assert.Contains(harness.Handler.Requests, r => TiingoFixtures.TickerOf(r) == "VTI" && TiingoFixtures.IsPrices(r));
    }

    [Fact]
    public async Task An_unset_token_sends_nothing_and_an_unreadable_one_records_configuration_on_every_binding()
    {
        await using var harness = await TiingoHarnessAsync("AAPL", "VTI");
        harness.Secrets.Result = SecretResult.NotSet;
        await harness.Sync.RunScheduledIfDueAsync();
        Assert.Empty(harness.Handler.Requests);
        Assert.All(await harness.Store.InstrumentPriceSourceBindings.AsNoTracking().ToListAsync(), b => Assert.Null(b.LastFailure));

        harness.Secrets.Result = SecretResult.Unreadable;
        harness.Clock.Now = Now.AddHours(1);
        var run = await harness.RunAsync((await harness.ExecuteManualAsync())!.Value);

        Assert.Empty(harness.Handler.Requests);
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.All(await harness.Store.InstrumentPriceSourceBindings.AsNoTracking().ToListAsync(),
            b => Assert.Equal(JobFailureCategory.Configuration, b.LastFailure));
    }

    [Fact]
    public async Task A_currency_rejection_evicts_tiingos_cached_venue()
    {
        await using var harness = new SyncHarness(historyDays: 5, tiingo: true);
        harness.Clock.Jumping = true;
        harness.Handler.Respond = TiingoFixtures.Healthy;
        // A USD listing bound to a NOK instrument: rule 5 rejects every batch.
        await harness.BindAsync(await harness.AddInstrumentAsync("AAPL", currency: "NOK"), TiingoPriceSource.SourceKey, 1, symbol: "AAPL");

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Null(harness.Tiingo.Metadata.Find("AAPL"));
        Assert.Empty(await harness.Store.InstrumentPrices.ToListAsync());
    }

    /// <summary>AC 6: the token, the symbol and the venue reach no log line, activity tag, exception or stored field.</summary>
    [Fact]
    public async Task The_token_symbol_and_venue_leave_no_trace()
    {
        const string symbol = "SENTINELTKR";
        var tags = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                tags.Enqueue(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    tags.Enqueue($"{tag.Key}={tag.Value}");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using var harness = await TiingoHarnessAsync(symbol, "FAILS");
        harness.Handler.Respond = request => TiingoFixtures.TickerOf(request) == "FAILS"
            ? ScriptedPriceHandler.Status(HttpStatusCode.BadGateway)
            : TiingoFixtures.Healthy(request);

        await harness.Sync.RunScheduledIfDueAsync();

        Assert.All(harness.Handler.Requests, request =>
        {
            Assert.Equal(["Token " + SyncHarness.Secret], request.Headers.GetValues("Authorization"));
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.All(query.AllKeys, key => Assert.Contains(key!, TiingoPriceSource.AllowedQueryKeys));
        });
        Assert.NotEmpty(harness.Logs.Lines);
        Assert.All(harness.Logs.Lines, line =>
        {
            Assert.DoesNotContain(SyncHarness.Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain(symbol, line, StringComparison.Ordinal);
            Assert.DoesNotContain("NASDAQ", line, StringComparison.Ordinal);
            Assert.DoesNotContain("tiingo.com", line, StringComparison.Ordinal);
        });
        Assert.All(tags, tag => Assert.DoesNotContain(SyncHarness.Secret, tag, StringComparison.Ordinal));

        var stored = JsonSerializer.Serialize(new object[]
        {
            await harness.Store.InstrumentPriceSourceBindings.AsNoTracking().ToListAsync(),
            await harness.Store.InstrumentPriceSourceStates.AsNoTracking().ToListAsync(),
            await harness.Store.Set<InstrumentPriceSyncJobRun>().AsNoTracking().ToListAsync(),
        });
        Assert.DoesNotContain(SyncHarness.Secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("NASDAQ", stored, StringComparison.Ordinal);
    }

    private static int Weekdays(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            count += day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : 1;
        }

        return count;
    }
}
