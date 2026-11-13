using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Core.Finance.Instruments.Sources.Tiingo;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.TestShared.InstrumentPrices;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The contract every <see cref="IInstrumentPriceSource"/> must keep (issue #446 §3.1). Phase 1 runs it against the
/// test-only fakes and every production source (Tiingo, issue #447), each answered in its own provider's shape, so the
/// framework's assumptions are checked for every real client the same way.
/// </summary>
public class InstrumentPriceSourceContractTests
{
    private static readonly string[] Keys = [FakePrimarySource.DeclaredKey, FakeSecondarySource.DeclaredKey, TiingoPriceSource.SourceKey];

    public static TheoryData<string> Sources() => new(Keys);

    private static IInstrumentPriceSource Create(string key) => key switch
    {
        "fake-primary" => new FakePrimarySource(),
        "fake-secondary" => new FakeSecondarySource(),
        "tiingo" => new TiingoPriceSource(TimeProvider.System),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>Each source's healthy provider, answering in the shape that source reads.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Healthy(string key) => key switch
    {
        "tiingo" => TiingoFixtures.Healthy,
        _ => ScriptedPriceHandler.WeekdayCloses("NOK"),
    };

    private static readonly SourceFetchRequest Request = new("EQNR.OL", new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), "NOK");

    [Theory]
    [MemberData(nameof(Sources))]
    public void The_declaration_is_well_formed(string key)
    {
        var source = Create(key);

        Assert.Matches(InstrumentIdentifierRules.SourceKeyPattern, source.Key);
        Assert.Equal(source.Key, key);
        Assert.True(Uri.CheckHostName(source.Host) == UriHostNameType.Dns, "Host must be a bare DNS name.");
        Assert.False(string.IsNullOrWhiteSpace(source.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(source.Attribution));
        Assert.NotEmpty(source.SupportedTypes);
        Assert.True(source.Limits.MaxDaysPerRequest >= 1);
        Assert.True(source.Limits.MaxRequestsPerRun >= 1);
        Assert.True(source.Limits.MaxRequestsPerDay >= source.Limits.MaxRequestsPerRun);
        Assert.Equal(source.CredentialPlacement is CredentialPlacement.None, source.SecretKeys.Count == 0);
        Assert.True(source.Limits.MaxRequestsPerHour is null
            || (source.Limits.MaxRequestsPerHour >= source.Limits.MaxRequestsPerRun && source.Limits.MaxRequestsPerHour <= source.Limits.MaxRequestsPerDay));
        Assert.Single(new InstrumentPriceSourceCatalogue([source]).All);
    }

    [Fact]
    public void The_declared_statics_equal_the_instance()
    {
        Assert.Equal(TiingoPriceSource.DeclaredKey, new TiingoPriceSource(TimeProvider.System).Key);
        Assert.Equal(TiingoPriceSource.DeclaredHost, new TiingoPriceSource(TimeProvider.System).Host);
    }

    /// <summary>
    /// A2: the currency a source states comes from the provider, never from the instrument — the same response gives
    /// the same code whatever <see cref="SourceFetchRequest.ExpectedCurrencyCode"/> says.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task The_stated_currency_does_not_depend_on_the_expected_one(string key)
    {
        var stated = new List<string?>();
        foreach (var expected in new[] { "NOK", "USD", "SEK" })
        {
            var (source, http, _) = Wire(key, Healthy(key));
            var result = await source.FetchClosesAsync(Request with { ExpectedCurrencyCode = expected }, http, CancellationToken.None);
            stated.AddRange(result.Closes!.Select(c => c.CurrencyCode));
        }

        Assert.NotEmpty(stated);
        Assert.Single(stated.Distinct());
    }

    /// <summary>A per-symbol refusal (A3) is UnsupportedInput: it never disables nor backs off the whole source.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task An_unknown_symbol_is_unsupported_input(string key)
    {
        var (source, http, _) = Wire(key, _ => ScriptedPriceHandler.Status(HttpStatusCode.NotFound));

        Assert.Equal(JobFailureCategory.UnsupportedInput, (await source.FetchClosesAsync(Request, http, CancellationToken.None)).Failure);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task A_success_returns_closes_in_the_window(string key)
    {
        var (source, http, _) = Wire(key, Healthy(key));

        var result = await source.FetchClosesAsync(Request, http, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.Closes!);
        Assert.All(result.Closes!, close => Assert.InRange(close.TradingDate, Request.From, Request.To));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Every_request_goes_to_the_declared_host_through_the_framework(string key)
    {
        var (source, http, handler) = Wire(key, Healthy(key));

        await source.FetchClosesAsync(Request, http, CancellationToken.None);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
            Assert.Equal(source.Host, request.RequestUri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
        });
    }

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task A_provider_failure_is_a_category_never_an_exception(string key, int status, JobFailureCategory expected)
    {
        var (source, http, _) = Wire(key, _ => ScriptedPriceHandler.Status((HttpStatusCode)status));

        var result = await source.FetchClosesAsync(Request, http, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure);
    }

    public static TheoryData<string, int, JobFailureCategory> ProviderFailures()
    {
        var data = new TheoryData<string, int, JobFailureCategory>();
        foreach (var key in Keys)
        {
            data.Add(key, 401, JobFailureCategory.Configuration);
            data.Add(key, 404, JobFailureCategory.UnsupportedInput);
            data.Add(key, 429, JobFailureCategory.ProviderRateLimited);
            data.Add(key, 301, JobFailureCategory.ProviderMalformed);
            data.Add(key, 501, JobFailureCategory.ProviderUnavailable);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task A_rate_limit_passes_its_retry_after_through(string key)
    {
        var (source, http, _) = Wire(key, _ => ScriptedPriceHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(7)));

        var result = await source.FetchClosesAsync(Request, http, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(7), result.RetryAfter);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task An_unparseable_body_is_malformed_never_an_exception(string key)
    {
        var (source, http, _) = Wire(key, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        });

        var result = await source.FetchClosesAsync(Request, http, CancellationToken.None);

        Assert.Equal(JobFailureCategory.ProviderMalformed, result.Failure);
    }

    private static (IInstrumentPriceSource Source, SourceHttp Http, ScriptedPriceHandler Handler) Wire(
        string key, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var source = Create(key);
        var handler = new ScriptedPriceHandler { Respond = respond };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://{source.Host}/") };
        return (source, new SourceHttp(source, client, new Secrets(), new Quota(), NullLogger.Instance, TimeProvider.System), handler);
    }

    private sealed class Secrets : ISecretSettingsReader
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(SecretResult.Found("contract-secret"));
    }

    private sealed class Quota : ISourceRequestQuota
    {
        public Task<QuotaReservation> TryReserveAsync(string sourceKey, InstrumentPriceSourceLimits limits, CancellationToken cancellationToken) => Task.FromResult(QuotaReservation.Reserved);
    }
}
