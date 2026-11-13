using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Finance.Instruments.Sources.Tiingo;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared.InstrumentPrices;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The Tiingo source against its fixtures (issue #447 §3.3, §3.5, AC 2, 3, 5, 6, 12, 17): the status table, the venue
/// map, the strict parse, the metadata cache, the credential header and the query allow-list — no live call.
/// </summary>
public class TiingoPriceSourceTests
{
    private const string Token = "tiingo-token-sentinel-value";

    private static readonly DateTimeOffset Now = new(2026, 1, 9, 12, 20, 0, TimeSpan.Zero);

    private static SourceFetchRequest Request(string symbol = "AAPL", string expected = "USD") =>
        new(symbol, new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 9), expected);

    private sealed class Wiring
    {
        public Wiring(Func<HttpRequestMessage, HttpResponseMessage> respond, SecretResult? secret = null)
        {
            Clock = new ManualClock(Now);
            Source = new TiingoPriceSource(Clock);
            Handler = new ScriptedPriceHandler { Respond = respond };
            Secrets = new SettableSecrets { Result = secret ?? SecretResult.Found(Token) };
            Http = NewHttp();
        }

        public ManualClock Clock { get; }

        public TiingoPriceSource Source { get; }

        public ScriptedPriceHandler Handler { get; }

        public SettableSecrets Secrets { get; }

        public SourceHttp Http { get; private set; }

        public SourceHttp NewHttp() => Http = new SourceHttp(
            Source, new HttpClient(Handler) { BaseAddress = new Uri("https://api.tiingo.com/") }, Secrets, new Unlimited(),
            NullLogger.Instance, Clock);

        public Task<SourceFetchResult> FetchAsync(SourceFetchRequest? request = null) =>
            Source.FetchClosesAsync(request ?? Request(), Http, CancellationToken.None);
    }

    /// <summary>A settable "now" whose timers are real, so the transport's short retry delays elapse.</summary>
    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class SettableSecrets : ISecretSettingsReader
    {
        public SecretResult Result { get; set; } = SecretResult.NotSet;

        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class Unlimited : ISourceRequestQuota
    {
        public Task<QuotaReservation> TryReserveAsync(string sourceKey, InstrumentPriceSourceLimits limits, CancellationToken cancellationToken) =>
            Task.FromResult(QuotaReservation.Reserved);
    }

    // ── Declaration ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_declaration_matches_the_spec_constants()
    {
        var source = new TiingoPriceSource(TimeProvider.System);

        Assert.Equal("tiingo", source.Key);
        Assert.Equal("api.tiingo.com", source.Host);
        Assert.Equal(TiingoPriceSource.DeclaredHost, source.Host);
        Assert.Equal(new CredentialPlacement.Header("Authorization", "Token "), source.CredentialPlacement);
        Assert.Equal([SecretSettingKeys.TiingoApiToken], source.SecretKeys);
        Assert.Equal(new HashSet<InstrumentType> { InstrumentType.Stock, InstrumentType.Etf, InstrumentType.Fund }, source.SupportedTypes);
        Assert.Equal(new InstrumentPriceSourceLimits(1461, TimeSpan.FromSeconds(2), 25, 600, 25), source.Limits);
        // 25 × 25 h (the US fall-back day) stays under Tiingo's 1,000/day, so any provider day is bounded (A5).
        Assert.True(source.Limits.MaxRequestsPerHour * 25 < 1000);
        Assert.True(source.Limits.MaxRequestsPerHour * 2 <= 50);
    }

    [Fact]
    public void No_plaintext_setting_names_the_host()
    {
        foreach (var key in Odyssey.Context.SystemSettingsKeys.AllKeys)
        {
            Assert.DoesNotContain("tiingo", key, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Venue map (AC 3) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_venue_map_holds_exactly_the_confirmed_entries()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["NYSE"] = "USD", ["NASDAQ"] = "USD", ["NYSE ARCA"] = "USD", ["NYSE MKT"] = "USD", ["AMEX"] = "USD",
                ["BATS"] = "USD", ["NMFQS"] = "USD",
            }.OrderBy(p => p.Key, StringComparer.Ordinal),
            TiingoVenues.CurrencyByExchangeCode.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("nasdaq")]
    [InlineData("Nasdaq")]
    [InlineData("NASDAQ ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SHE")]
    [InlineData("SHG")]
    [InlineData("PINK")]
    public void The_venue_lookup_is_exact_case_sensitive_and_has_no_default(string? code) =>
        Assert.Null(TiingoVenues.CurrencyOf(code, "AAPL"));

    [Fact]
    public void A_us_venue_ticker_tiingo_prices_in_aud_is_refused() =>
        Assert.Null(TiingoVenues.CurrencyOf("NASDAQ", "mpg"));

    // ── Success paths ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_stock_maps_close_never_adj_close_in_usd()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", "prices-AAPL.json"));

        var result = await wiring.FetchAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                new FetchedClose(new DateOnly(2026, 1, 2), 243.85m, "USD"),
                new FetchedClose(new DateOnly(2026, 1, 5), 245.0m, "USD"),
                new FetchedClose(new DateOnly(2026, 1, 6), 246.12345678m, "USD"),
            ],
            result.Closes);
    }

    [Fact]
    public async Task A_mutual_fund_close_is_its_nav()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-VFIAX.json", "prices-VFIAX.json"));

        var result = await wiring.FetchAsync(Request("VFIAX"));

        Assert.Equal([552.31m, 555.02m], result.Closes!.Select(c => c.Close));
        Assert.All(result.Closes!, c => Assert.Equal("USD", c.CurrencyCode));
    }

    [Theory]
    [InlineData("VTI", "meta-VTI.json")]
    [InlineData("BRK-B", "meta-BRK-B.json")]
    public async Task An_etf_and_a_share_class_resolve_their_venue(string symbol, string metadata)
    {
        var wiring = new Wiring(TiingoFixtures.Route(metadata, request => TiingoFixtures.WeekdayPrices(request)));

        var result = await wiring.FetchAsync(Request(symbol));

        Assert.True(result.Succeeded);
        Assert.Equal("/tiingo/daily/" + symbol + "/prices", wiring.Handler.Requests[^1].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("prices-weekend.json")]
    public async Task An_empty_array_is_an_empty_success(string prices)
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", prices));

        var result = await wiring.FetchAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Closes!);
    }

    [Fact]
    public async Task A_204_on_prices_is_an_empty_success()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", _ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        Assert.Empty((await wiring.FetchAsync()).Closes!);
    }

    [Fact]
    public async Task A_duplicate_date_is_passed_up_for_the_batch_check_to_reject()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", "prices-duplicate-date.json"));
        var request = Request();

        var result = await wiring.FetchAsync(request);

        Assert.Equal(2, result.Closes!.Count);
        Assert.Equal((JobFailureCategory.ProviderMalformed, 2), FetchedBatchCheck.Check(result.Closes!, request, "USD", new DateOnly(2026, 1, 9)));
    }

    // ── A2: the stated currency never follows the instrument ───────────────────────────────────

    [Theory]
    [InlineData("USD")]
    [InlineData("NOK")]
    [InlineData("")]
    public async Task The_stated_currency_does_not_change_with_the_expected_one(string expected)
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", "prices-AAPL.json"));

        var result = await wiring.FetchAsync(Request(expected: expected));

        Assert.All(result.Closes!, c => Assert.Equal("USD", c.CurrencyCode));
    }

    // ── Metadata refusals ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("meta-unmapped.json")]
    [InlineData("meta-lowercase-venue.json")]
    [InlineData("meta-mismatch.json")]
    [InlineData("meta-null-dates.json")]
    public async Task Metadata_that_names_no_mapped_venue_for_this_ticker_is_unsupported_and_sends_no_price_request(string metadata)
    {
        var wiring = new Wiring(TiingoFixtures.Route(metadata, "prices-AAPL.json"));

        var result = await wiring.FetchAsync(Request(metadata == "meta-unmapped.json" ? "VOD" : "AAPL"));

        Assert.Equal(JobFailureCategory.UnsupportedInput, result.Failure);
        Assert.DoesNotContain(wiring.Handler.Requests, TiingoFixtures.IsPrices);
    }

    [Fact]
    public async Task The_ticker_comparison_ignores_case()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", "prices-AAPL.json"));

        Assert.True((await wiring.FetchAsync(Request("aapl"))).Succeeded);
    }

    [Theory]
    [InlineData("meta-array.json", "application/json")]
    [InlineData("meta-AAPL.json", "text/html")]
    public async Task Metadata_that_is_not_a_json_object_is_malformed(string metadata, string contentType)
    {
        var wiring = new Wiring(request => TiingoFixtures.IsPrices(request)
            ? TiingoFixtures.File("prices-AAPL.json")
            : TiingoFixtures.File(metadata, contentType: contentType));

        Assert.Equal(JobFailureCategory.ProviderMalformed, (await wiring.FetchAsync()).Failure);
    }

    // ── Price-body rules (§3.3, §3.5) ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("prices-object.json")]
    [InlineData("prices-bad-date.json")]
    [InlineData("prices-null-close.json")]
    [InlineData("prices-string-close.json")]
    [InlineData("error-403-invalid-token.json")]
    public async Task A_price_body_that_is_not_a_well_formed_array_is_malformed(string prices)
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", prices));

        Assert.Equal(JobFailureCategory.ProviderMalformed, (await wiring.FetchAsync()).Failure);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public async Task A_price_body_with_another_content_type_is_malformed(string contentType)
    {
        var wiring = new Wiring(request => TiingoFixtures.IsPrices(request)
            ? TiingoFixtures.File("prices-AAPL.json", contentType: contentType)
            : TiingoFixtures.File("meta-AAPL.json"));

        Assert.Equal(JobFailureCategory.ProviderMalformed, (await wiring.FetchAsync()).Failure);
    }

    [Fact]
    public async Task A_truncated_body_is_malformed()
    {
        var full = TiingoFixtures.Read("prices-AAPL.json");
        var wiring = new Wiring(request => TiingoFixtures.IsPrices(request)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(full[..(full.Length / 2)], Encoding.UTF8, "application/json") }
            : TiingoFixtures.File("meta-AAPL.json"));

        Assert.Equal(JobFailureCategory.ProviderMalformed, (await wiring.FetchAsync()).Failure);
    }

    [Fact]
    public async Task A_body_over_the_size_cap_passes_the_framework_exception_through()
    {
        var wiring = new Wiring(request => TiingoFixtures.IsPrices(request)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[SourceHttpLimits.MaxResponseBytes + 1])
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            }
            : TiingoFixtures.File("meta-AAPL.json"));

        await Assert.ThrowsAsync<SourceResponseMalformedException>(() => wiring.FetchAsync());
    }

    [Theory]
    [InlineData("2026-01-02T00:00:00.000Z", 2026, 1, 2)]
    [InlineData("2026-01-02T23:59:59.999-05:00", 2026, 1, 2)]
    [InlineData("2026-01-02", 2026, 1, 2)]
    public void A_date_is_its_first_ten_characters_with_no_time_zone_conversion(string value, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), TiingoPriceSource.ParseDate(value));

    [Theory]
    [InlineData("2026-1-2")]
    [InlineData("02/01/2026")]
    [InlineData("2026-01-02X")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_date(string? value) => Assert.Null(TiingoPriceSource.ParseDate(value));

    // ── The status table (§3.3) ────────────────────────────────────────────────────────────────

    public static TheoryData<int, JobFailureCategory> Statuses() => new()
    {
        { 206, JobFailureCategory.ProviderMalformed },
        { 301, JobFailureCategory.ProviderMalformed },
        { 302, JobFailureCategory.ProviderMalformed },
        { 307, JobFailureCategory.ProviderMalformed },
        { 400, JobFailureCategory.UnsupportedInput },
        { 401, JobFailureCategory.Configuration },
        { 402, JobFailureCategory.UnsupportedInput },
        { 403, JobFailureCategory.Configuration },
        { 404, JobFailureCategory.UnsupportedInput },
        { 408, JobFailureCategory.ProviderUnavailable },
        { 413, JobFailureCategory.ProviderMalformed },
        { 418, JobFailureCategory.ProviderUnavailable },
        { 422, JobFailureCategory.UnsupportedInput },
        { 429, JobFailureCategory.ProviderRateLimited },
        { 500, JobFailureCategory.ProviderUnavailable },
        { 501, JobFailureCategory.ProviderUnavailable },
        { 503, JobFailureCategory.ProviderUnavailable },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task Each_status_on_the_metadata_request_maps_by_the_table(int status, JobFailureCategory expected)
    {
        var wiring = new Wiring(_ => ScriptedPriceHandler.Status((HttpStatusCode)status));

        Assert.Equal(expected, (await wiring.FetchAsync()).Failure);
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task Each_status_on_the_price_request_maps_by_the_table(int status, JobFailureCategory expected)
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", _ => ScriptedPriceHandler.Status((HttpStatusCode)status)));

        Assert.Equal(expected, (await wiring.FetchAsync()).Failure);
    }

    [Theory]
    [InlineData("error-403-no-token.json")]
    [InlineData("error-403-invalid-token.json")]
    public async Task The_recorded_token_refusal_is_a_configuration_failure(string fixture)
    {
        var wiring = new Wiring(_ => TiingoFixtures.File(fixture, HttpStatusCode.Forbidden));

        Assert.Equal(JobFailureCategory.Configuration, (await wiring.FetchAsync()).Failure);
    }

    [Fact]
    public async Task A_rate_limit_without_retry_after_waits_for_the_next_clock_hour()
    {
        var wiring = new Wiring(_ => ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests));

        var result = await wiring.FetchAsync();

        Assert.Equal(TimeSpan.FromMinutes(40), result.RetryAfter);
    }

    [Fact]
    public async Task A_rate_limit_with_retry_after_passes_it_through()
    {
        var wiring = new Wiring(_ => ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(17)));

        Assert.Equal(TimeSpan.FromMinutes(17), (await wiring.FetchAsync()).RetryAfter);
    }

    [Fact]
    public async Task A_timeout_is_unavailable()
    {
        var wiring = new Wiring(_ => throw new HttpRequestException("boom"));

        Assert.Equal(JobFailureCategory.ProviderUnavailable, (await wiring.FetchAsync()).Failure);
    }

    // ── Hostile input ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("..")]
    [InlineData("A/../B")]
    [InlineData("A/B")]
    public async Task A_hostile_source_symbol_never_leaves_the_path(string symbol)
    {
        var wiring = new Wiring(TiingoFixtures.Healthy);

        await Assert.ThrowsAsync<SourceDestinationException>(() => wiring.FetchAsync(Request(symbol)));
        Assert.Empty(wiring.Handler.Requests);
    }

    [Theory]
    [InlineData("AAPL?token=x")]
    [InlineData("AAPL#frag")]
    [InlineData("AAPL&format=csv")]
    public async Task A_symbol_carrying_url_syntax_is_one_escaped_segment(string symbol)
    {
        var wiring = new Wiring(TiingoFixtures.Healthy);

        await wiring.FetchAsync(Request(symbol));

        Assert.All(wiring.Handler.Requests, request =>
        {
            Assert.Equal("api.tiingo.com", request.RequestUri!.Host);
            Assert.Equal(symbol, TiingoFixtures.TickerOf(request));
            Assert.DoesNotContain("token", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── Credential and query (AC 5, 6, 12) ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_token_travels_only_as_the_prefixed_header_and_the_query_keys_are_allow_listed()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", "prices-AAPL.json"));

        await wiring.FetchAsync();

        Assert.Equal(2, wiring.Handler.Requests.Count);
        Assert.All(wiring.Handler.Requests, request =>
        {
            Assert.Equal(["Token " + Token], request.Headers.GetValues("Authorization"));
            Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.All(query.AllKeys, key => Assert.Contains(key!, TiingoPriceSource.AllowedQueryKeys));
        });
        var prices = wiring.Handler.Requests[1].RequestUri!;
        Assert.Equal("?startDate=2026-01-02&endDate=2026-01-09&format=json", prices.Query);
    }

    [Fact]
    public async Task A_token_rotation_takes_effect_on_the_next_request()
    {
        var wiring = new Wiring(TiingoFixtures.Route("meta-AAPL.json", "prices-AAPL.json"));
        await wiring.FetchAsync();

        wiring.Secrets.Result = SecretResult.Found("rotated-token");
        await wiring.FetchAsync();

        Assert.Equal(["Token rotated-token"], wiring.Handler.Requests[^1].Headers.GetValues("Authorization"));
    }

    [Theory]
    [InlineData(SecretReadState.NotSet)]
    [InlineData(SecretReadState.Unreadable)]
    public async Task Without_a_readable_token_nothing_is_sent(SecretReadState state)
    {
        var wiring = new Wiring(TiingoFixtures.Healthy, state == SecretReadState.NotSet ? SecretResult.NotSet : SecretResult.Unreadable);

        var thrown = await Assert.ThrowsAsync<SourceCredentialException>(() => wiring.FetchAsync());

        Assert.Equal(state, thrown.State);
        Assert.Empty(wiring.Handler.Requests);
    }

    // ── Metadata cache (AC 17) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_span_within_seven_days_sends_one_request()
    {
        var wiring = new Wiring(TiingoFixtures.Healthy);
        await wiring.FetchAsync();
        Assert.Equal(2, wiring.Handler.Requests.Count);

        wiring.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        await wiring.FetchAsync();
        Assert.Equal(3, wiring.Handler.Requests.Count);

        wiring.Clock.Advance(TimeSpan.FromMinutes(2));
        await wiring.FetchAsync();
        Assert.Equal(5, wiring.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_currency_rejection_evicts_the_cached_venue()
    {
        var wiring = new Wiring(TiingoFixtures.Healthy);
        var request = Request(expected: "NOK");
        var result = await wiring.FetchAsync(request);
        var rejected = FetchedBatchCheck.Check(result.Closes!, request, "NOK", new DateOnly(2026, 1, 9));
        Assert.Equal(5, rejected!.Value.Rule);

        wiring.Source.OnBatchRejected(request, rejected.Value.Rule);
        await wiring.FetchAsync(request);

        Assert.Equal(4, wiring.Handler.Requests.Count);
        Assert.Equal(2, wiring.Handler.Requests.Count(r => !TiingoFixtures.IsPrices(r)));
    }

    [Fact]
    public async Task Another_rejection_keeps_the_cached_venue()
    {
        var wiring = new Wiring(TiingoFixtures.Healthy);
        await wiring.FetchAsync();

        wiring.Source.OnBatchRejected(Request(), 2);
        await wiring.FetchAsync();

        Assert.Equal(1, wiring.Handler.Requests.Count(r => !TiingoFixtures.IsPrices(r)));
    }

    [Theory]
    [InlineData("unmapped")]
    [InlineData("unknown")]
    public async Task A_negative_entry_suppresses_metadata_requests_for_24_hours(string kind)
    {
        var wiring = new Wiring(request => kind == "unknown"
            ? ScriptedPriceHandler.Status(HttpStatusCode.NotFound)
            : TiingoFixtures.File("meta-unmapped.json"));
        var request = Request("VOD");
        await wiring.FetchAsync(request);

        wiring.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(JobFailureCategory.UnsupportedInput, (await wiring.FetchAsync(request)).Failure);
        Assert.Single(wiring.Handler.Requests);

        wiring.Clock.Advance(TimeSpan.FromHours(2));
        await wiring.FetchAsync(request);
        Assert.Equal(2, wiring.Handler.Requests.Count);
    }

    [Fact]
    public async Task A_transient_metadata_failure_is_not_cached()
    {
        var calls = 0;
        // One metadata request and both its retries fail; the next span asks again.
        var wiring = new Wiring(request => ++calls <= 3 ? ScriptedPriceHandler.Status(HttpStatusCode.BadGateway) : TiingoFixtures.Healthy(request));

        Assert.Equal(JobFailureCategory.ProviderUnavailable, (await wiring.FetchAsync()).Failure);
        Assert.True((await wiring.FetchAsync()).Succeeded);
    }

    [Fact]
    public void The_store_never_exceeds_its_capacity_and_evicts_the_oldest()
    {
        var clock = new FakeTimeProvider(Now);
        var cache = new TiingoMetadataCache(clock);
        for (var i = 0; i < TiingoMetadataCache.Capacity + 50; i++)
        {
            cache.SetPositive($"S{i}", "USD");
            Assert.True(cache.Count <= TiingoMetadataCache.Capacity);
        }

        Assert.Equal(TiingoMetadataCache.Capacity, cache.Count);
        Assert.Null(cache.Find("S0"));
        Assert.Null(cache.Find("S49"));
        Assert.Equal("USD", cache.Find("S50")!.Value.CurrencyCode);
    }

    [Fact]
    public void The_store_runs_on_the_time_provider()
    {
        var clock = new FakeTimeProvider(Now);
        var cache = new TiingoMetadataCache(clock);
        cache.SetNegative("X");

        clock.Advance(TiingoMetadataCache.NegativeTtl);

        Assert.Null(cache.Find("X"));
    }

    // ── Fixture hygiene (§7.8) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void No_fixture_carries_a_token_shaped_string()
    {
        var files = Directory.GetFiles(TiingoFixtures.Directory, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file).Replace(TiingoFixtures.Sentinel, string.Empty, StringComparison.Ordinal);
            // Tiingo's token shape as the spec states it from memory (40 hex characters) — not confirmed against an issued token.
            Assert.DoesNotMatch(new Regex("[0-9a-fA-F]{40}"), text);
            // Anything token-like: 16+ characters of one alphanumeric run with at least one digit and one letter.
            foreach (Match match in Regex.Matches(text, "[A-Za-z0-9_\\-]{16,}"))
            {
                Assert.False(match.Value.Any(char.IsDigit) && match.Value.Any(char.IsLetter),
                    $"{Path.GetFileName(file)} carries a token-shaped string.");
            }
        }
    }
}
