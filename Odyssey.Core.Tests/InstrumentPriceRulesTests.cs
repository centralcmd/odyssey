using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared.InstrumentPrices;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The instrument-price rules declared once (issue #446 §8): the newest-price ordering and its source-lint, the
/// identifier rules, the structural batch check, the source catalogue's validation and the transport's guards.
/// </summary>
public class InstrumentPriceRulesTests
{
    private static readonly Guid InstrumentId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 10, 22);

    private static InstrumentPrice Row(DateOnly date, string? source, decimal close = 1m, DateTime? updated = null) => new()
    {
        InstrumentPriceId = Guid.NewGuid(),
        InstrumentId = InstrumentId,
        TradingDate = date,
        Close = close,
        SourceKey = source,
        CreatedAtUtc = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = updated,
    };

    private static readonly Func<Guid, string, int> Priorities = InstrumentPriceOrdering.PriorityLookup(
    [
        new InstrumentPriceSourceBinding { InstrumentId = InstrumentId, SourceKey = "a", SourceSymbol = "X", Priority = 1 },
        new InstrumentPriceSourceBinding { InstrumentId = InstrumentId, SourceKey = "b", SourceSymbol = "X", Priority = 2 },
    ]);

    // ── Ordering ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_latest_date_wins_whatever_its_source()
    {
        var winner = InstrumentPriceOrdering.Winner([Row(Day.AddDays(-1), null), Row(Day, "zzz")], Priorities);
        Assert.Equal(Day, winner!.TradingDate);
    }

    [Fact]
    public void On_one_date_a_manual_row_beats_every_fetched_row()
    {
        var winner = InstrumentPriceOrdering.Winner([Row(Day, "a"), Row(Day, null, 7m), Row(Day, "b")], Priorities);
        Assert.Equal(7m, winner!.Close);
    }

    [Fact]
    public void Among_fetched_rows_the_higher_binding_priority_wins_and_an_unbound_source_ranks_last()
    {
        Assert.Equal("a", InstrumentPriceOrdering.Winner([Row(Day, "b"), Row(Day, "a")], Priorities)!.SourceKey);
        Assert.Equal("b", InstrumentPriceOrdering.Winner([Row(Day, "gone"), Row(Day, "b")], Priorities)!.SourceKey);
    }

    [Fact]
    public void Between_two_manual_rows_the_newest_update_wins()
    {
        var winner = InstrumentPriceOrdering.Winner(
            [Row(Day, null, 1m), Row(Day, null, 2m, updated: new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc))], Priorities);
        Assert.Equal(2m, winner!.Close);
    }

    [Fact]
    public void Winners_per_date_keep_one_row_per_trading_date()
    {
        var winners = InstrumentPriceOrdering.WinnersPerDate(
            [Row(Day, "a"), Row(Day, "b"), Row(Day.AddDays(-1), "b"), Row(Day.AddDays(-1), null, 3m)], Priorities);
        Assert.Equal(2, winners.Count);
        Assert.Equal(3m, winners[0].Close);
        Assert.Equal("a", winners[1].SourceKey);
    }

    /// <summary>The newest-price rule is written once (issue #446 §8.3); a second copy would let two reads disagree.</summary>
    [Fact]
    public void No_file_in_Odyssey_Core_restates_the_newest_price_ordering()
    {
        var files = SourceFiles("Odyssey.Core");
        Assert.Contains(files, path => path.EndsWith("InstrumentPriceOrdering.cs", StringComparison.Ordinal));

        var pattern = new Regex(@"TradingDate\s*\)\s*\.ThenBy(Descending)?\(\s*\w+\s*=>\s*\w+\.SourceKey", RegexOptions.Singleline);
        var offenders = files
            .Where(path => !path.EndsWith("InstrumentPriceOrdering.cs", StringComparison.Ordinal))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Use InstrumentPriceOrdering instead of restating it: " + string.Join(", ", offenders));
    }

    /// <summary>The identifier and decimal rules are shared with the client (issue #446 §8.1); no copy in Core.</summary>
    [Fact]
    public void No_file_in_Odyssey_Core_restates_an_identifier_pattern()
    {
        var literals = new[]
        {
            InstrumentIdentifierRules.SymbolPattern, InstrumentIdentifierRules.MicPattern, InstrumentIdentifierRules.IsinPattern,
            InstrumentIdentifierRules.SourceSymbolPattern, InstrumentIdentifierRules.SourceKeyPattern,
        };
        var offenders = SourceFiles("Odyssey.Core").Concat(SourceFiles("Odyssey.Context"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(path => literals.Any(literal => File.ReadAllText(path).Contains(literal, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "Name InstrumentIdentifierRules instead of restating a pattern: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_price_column_scale_is_the_shared_decimal_rule()
    {
        Assert.Equal(8, InstrumentPriceRules.MaxDecimals);
        Assert.Equal(InstrumentPriceRules.MaxDecimals, MoneyBounds.InstrumentPriceMin.Split('.')[1].Length);
        Assert.Equal(InstrumentPriceRules.MaxDecimals, MoneyBounds.InstrumentPriceMax.Split('.')[1].Length);
        Assert.Equal(MoneyBounds.InstrumentPricePrecision,
            MoneyBounds.InstrumentPriceMax.Replace(".", string.Empty, StringComparison.Ordinal).Length);
    }

    // ── Identifiers ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NO0010096985", true)]
    [InlineData("US0378331005", true)]
    [InlineData("NO0010096986", false)]
    [InlineData("US0378331006", false)]
    public void The_isin_check_digit_is_verified(string isin, bool valid) =>
        Assert.Equal(valid, InstrumentIdentifierRules.IsValidIsinCheckDigit(isin));

    [Fact]
    public void A_listing_key_names_the_symbol_and_mic_else_the_isin_and_currency()
    {
        Assert.Equal("EQNR@XOSL", InstrumentIdentifierRules.ListingKey("eqnr", "xosl", null, "NOK"));
        Assert.Equal("ISIN:NO0010096985:NOK", InstrumentIdentifierRules.ListingKey("FUND", null, "no0010096985", "nok"));
    }

    [Theory]
    [InlineData(1.12345678, true)]
    [InlineData(1.123456789, false)]
    public void A_price_carries_at_most_eight_decimals(double value, bool ok) =>
        Assert.Equal(ok, InstrumentPriceRules.HasAtMostDecimals((decimal)value));

    // ── The structural batch check ─────────────────────────────────────────────────────────────

    private static readonly SourceFetchRequest Window = new("X", new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), "NOK");

    private static (JobFailureCategory, int)? Check(params FetchedClose[] batch) =>
        FetchedBatchCheck.Check(batch, Window, "NOK", new DateOnly(2026, 10, 23));

    [Fact]
    public void An_empty_batch_passes() => Assert.Null(Check());

    [Fact]
    public void A_well_formed_batch_passes() =>
        Assert.Null(Check(new FetchedClose(new DateOnly(2026, 10, 19), 100m, "NOK"), new FetchedClose(new DateOnly(2026, 10, 20), 0.00000001m, "nok")));

    [Fact]
    public void More_rows_than_the_window_allows_is_rule_one()
    {
        var rows = Enumerable.Range(0, 11).Select(i => new FetchedClose(new DateOnly(2026, 10, 19), 1m, "NOK")).ToArray();
        Assert.Equal((JobFailureCategory.ProviderMalformed, 1), Check(rows));
    }

    [Fact]
    public void A_duplicate_date_is_rule_two() =>
        Assert.Equal((JobFailureCategory.ProviderMalformed, 2),
            Check(new FetchedClose(new DateOnly(2026, 10, 19), 1m, "NOK"), new FetchedClose(new DateOnly(2026, 10, 19), 2m, "NOK")));

    [Theory]
    [InlineData(2026, 10, 18)]
    [InlineData(2026, 10, 24)]
    public void A_date_outside_the_window_is_rule_three(int y, int m, int d) =>
        Assert.Equal((JobFailureCategory.ProviderMalformed, 3), Check(new FetchedClose(new DateOnly(y, m, d), 1m, "NOK")));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.000000001")]
    [InlineData("10000000000")]
    public void A_close_outside_the_bounds_or_too_precise_is_rule_four(string close) =>
        Assert.Equal((JobFailureCategory.ProviderMalformed, 4),
            Check(new FetchedClose(new DateOnly(2026, 10, 20), decimal.Parse(close, System.Globalization.CultureInfo.InvariantCulture), "NOK")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("USD")]
    [InlineData("NOk")]
    public void A_missing_or_other_currency_is_rule_five(string? currency) =>
        Assert.Equal((JobFailureCategory.UnsupportedInput, 5), Check(new FetchedClose(new DateOnly(2026, 10, 20), 1m, currency)));

    // ── The catalogue ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_catalogue_refuses_a_duplicate_key()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new InstrumentPriceSourceCatalogue([new FakePrimarySource(), new FakePrimarySource()]));
        Assert.Contains("twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalogue_refuses_a_daily_cap_below_the_per_run_budget()
    {
        var source = new FakePrimarySource { Limits = new InstrumentPriceSourceLimits(30, TimeSpan.Zero, MaxRequestsPerRun: 10, MaxRequestsPerDay: 5) };
        Assert.Throws<InvalidOperationException>(() => new InstrumentPriceSourceCatalogue([source]));
    }

    [Fact]
    public void An_empty_catalogue_is_empty()
    {
        var catalogue = new InstrumentPriceSourceCatalogue([]);
        Assert.True(catalogue.IsEmpty);
        Assert.Null(catalogue.Find("fake-primary"));
        Assert.Equal("fake-primary", catalogue.DisplayNameOf("fake-primary"));
    }

    // ── The transport ──────────────────────────────────────────────────────────────────────────

    private sealed class Secrets(SecretResult result) : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class UnlimitedQuota : ISourceRequestQuota
    {
        public Task<bool> TryReserveAsync(string sourceKey, int maxPerDay, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static (SourceHttp Http, ScriptedPriceHandler Handler) Transport(IInstrumentPriceSource source, SecretResult? secret = null)
    {
        var handler = new ScriptedPriceHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://{source.Host}/") };
        return (new SourceHttp(source, client, new Secrets(secret ?? SecretResult.Found("s3cret")), new UnlimitedQuota(),
            NullLogger.Instance, TimeProvider.System), handler);
    }

    [Theory]
    [InlineData("https://evil.example/eod")]
    [InlineData("http://primary.prices.example/eod")]
    public async Task A_request_off_the_pinned_https_host_is_refused_before_it_is_sent(string target)
    {
        var (http, handler) = Transport(new FakePrimarySource());
        await Assert.ThrowsAsync<SourceDestinationException>(() => http.GetAsync(new Uri(target), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_dot_segment_or_a_slash_is_refused(string segment)
    {
        var (http, _) = Transport(new FakePrimarySource());
        Assert.Throws<SourceDestinationException>(() => http.BuildUri(["eod", segment], null));
    }

    [Fact]
    public void A_path_segment_is_escaped()
    {
        var (http, _) = Transport(new FakePrimarySource());
        Assert.Equal("eod/A%20B%3Fx%23", http.BuildUri(["eod", "A B?x#"], null).OriginalString);
    }

    [Fact]
    public async Task A_same_named_query_parameter_is_replaced_by_the_credential()
    {
        var (http, handler) = Transport(new FakePrimarySource());
        handler.Respond = _ => ScriptedPriceHandler.Json(Array.Empty<object>());

        await http.GetAsync(new Uri("eod/X?api_token=attacker&a=1", UriKind.Relative), CancellationToken.None);

        var query = System.Web.HttpUtility.ParseQueryString(Assert.Single(handler.Requests).RequestUri!.Query);
        Assert.Equal(["s3cret"], query.GetValues("api_token") ?? []);
        Assert.Equal("1", query["a"]);
    }

    [Theory]
    [InlineData(SecretReadState.NotSet)]
    [InlineData(SecretReadState.Unreadable)]
    public async Task Without_a_readable_credential_nothing_is_sent(SecretReadState state)
    {
        var (http, handler) = Transport(new FakePrimarySource(), state == SecretReadState.NotSet ? SecretResult.NotSet : SecretResult.Unreadable);
        var exception = await Assert.ThrowsAsync<SourceCredentialException>(() => http.GetAsync(new Uri("eod/X", UriKind.Relative), CancellationToken.None));
        Assert.Equal(state, exception.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_response_over_the_size_cap_is_malformed()
    {
        var (http, handler) = Transport(new FakePrimarySource());
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[SourceHttpLimits.MaxResponseBytes + 1])),
        };

        await Assert.ThrowsAsync<SourceResponseMalformedException>(() => http.GetAsync(new Uri("eod/X", UriKind.Relative), CancellationToken.None));
    }

    [Fact]
    public async Task A_rate_limit_is_never_retried_in_request()
    {
        var (http, handler) = Transport(new FakePrimarySource());
        handler.Respond = _ => ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5));

        var response = await http.GetAsync(new Uri("eod/X", UriKind.Relative), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), response.RetryAfter);
        Assert.Single(handler.Requests);
        Assert.Equal(1, http.RequestsMade);
    }

    [Fact]
    public void A_secret_never_prints_its_value() =>
        Assert.DoesNotContain("s3cret", new SourceSecret("s3cret").ToString(), StringComparison.Ordinal);

    private static List<string> SourceFiles(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Odyssey.sln")))
        {
            directory = directory.Parent;
        }

        var root = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("No solution root."), project);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
    }
}
