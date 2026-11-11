using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sources.Tiingo;

/// <summary>
/// Tiingo's end-of-day prices (issue #447): US-listed stocks, ETFs and mutual funds, on the free Starter plan. Every
/// request goes through the framework's <see cref="ISourceHttp"/>; the token travels only as
/// <c>Authorization: Token …</c> (never the <c>?token=</c> query parameter). A span costs one price request, plus one
/// metadata request when the symbol's venue is not cached — the venue is what the currency is derived from (A2).
///
/// <para>
/// Status codes alone decide the outcome — Tiingo documents no error bodies, so no body text is ever read — and the
/// mapping is default-deny: anything not listed is <see cref="JobFailureCategory.ProviderUnavailable"/>. The source
/// never logs, stores or returns provider text, and never catches the framework's own exceptions.
/// </para>
/// </summary>
public sealed class TiingoPriceSource : IInstrumentPriceSource, IInstrumentPriceSourceDeclaration
{
    public const string SourceKey = "tiingo";

    public const string ApiHost = "api.tiingo.com";

    /// <summary>The compiled authentication scheme; the stored secret is the bare token (A4).</summary>
    public const string TokenPrefix = "Token ";

    /// <summary>
    /// Tiingo's Starter (free) plan limits, as published on its pricing page (read 2026-11-11): 50 requests an hour,
    /// 1,000 a day (reset at midnight US Eastern), 500 unique symbols a month, no per-minute limit.
    /// <list type="bullet">
    /// <item>25 an hour — half the limit, so two adjacent clock-hour buckets stay within 50 even if Tiingo's window is rolling.</item>
    /// <item>25 a run — equal to the hourly cap, which the catalogue requires it not to exceed.</item>
    /// <item>600 an Oslo day — and 25 × 25 h (the US fall-back day) = 625 &lt; 1,000, so the hourly cap bounds any provider day (A5).</item>
    /// <item>2 s between requests — Tiingo publishes no per-minute limit; this is courtesy spacing (A7).</item>
    /// <item>1,461 days a request — Tiingo's own example asks for four years; ≈ 1,000 rows ≈ 250 KB, under the 2 MB cap.</item>
    /// </list>
    /// </summary>
    public static readonly InstrumentPriceSourceLimits StarterPlanLimits = new(
        MaxDaysPerRequest: 1461,
        MinRequestInterval: TimeSpan.FromSeconds(2),
        MaxRequestsPerRun: 25,
        MaxRequestsPerDay: 600,
        MaxRequestsPerHour: 25);

    /// <summary>The only query keys a request may carry.</summary>
    public static readonly IReadOnlySet<string> AllowedQueryKeys = new HashSet<string>(StringComparer.Ordinal) { "startDate", "endDate", "format" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private readonly TimeProvider timeProvider;

    public TiingoPriceSource(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        Metadata = new TiingoMetadataCache(timeProvider);
    }

    public static string DeclaredKey => SourceKey;

    public static string DeclaredHost => ApiHost;

    public string Key => SourceKey;

    public string DisplayName => "Tiingo";

    public string Attribution => "End-of-day prices from Tiingo (tiingo.com).";

    public string Host => ApiHost;

    public CredentialPlacement CredentialPlacement { get; } = new CredentialPlacement.Header("Authorization", TokenPrefix);

    public IReadOnlySet<InstrumentType> SupportedTypes { get; } =
        new HashSet<InstrumentType> { InstrumentType.Stock, InstrumentType.Etf, InstrumentType.Fund };

    public InstrumentPriceSourceLimits Limits => StarterPlanLimits;

    public IReadOnlyList<string> SecretKeys { get; } = [SecretSettingKeys.TiingoApiToken];

    /// <summary>The per-process metadata store; exposed for tests.</summary>
    public TiingoMetadataCache Metadata { get; }

    public async Task<SourceFetchResult> FetchClosesAsync(SourceFetchRequest request, ISourceHttp http, CancellationToken cancellationToken)
    {
        // The stated currency comes from Tiingo's venue alone; request.ExpectedCurrencyCode is deliberately never read.
        var currency = await CurrencyOfAsync(request.SourceSymbol, http, cancellationToken);
        if (currency.Failure is { } failed)
        {
            return failed;
        }

        var uri = http.BuildUri(["tiingo", "daily", request.SourceSymbol, "prices"], new Dictionary<string, string>
        {
            ["startDate"] = request.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = request.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["format"] = "json",
        });
        var response = await http.GetAsync(uri, cancellationToken);
        if (response.Status == HttpStatusCode.NoContent)
        {
            return SourceFetchResult.Success([]);
        }

        if (response.Status != HttpStatusCode.OK)
        {
            return Classify(response);
        }

        if (!IsJson(response) || ParsePrices(response.Body, currency.Code!) is not { } closes)
        {
            return SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed);
        }

        return SourceFetchResult.Success(closes);
    }

    public void OnBatchRejected(SourceFetchRequest request, int rule)
    {
        // Rule 5: the derived currency differs from the instrument's. Ask again rather than trust the cached venue.
        if (rule == 5)
        {
            Metadata.Evict(request.SourceSymbol);
        }
    }

    private readonly record struct CurrencyLookup(string? Code, SourceFetchResult? Failure);

    private async Task<CurrencyLookup> CurrencyOfAsync(string symbol, ISourceHttp http, CancellationToken cancellationToken)
    {
        if (Metadata.Find(symbol) is { } hit)
        {
            return hit.CurrencyCode is { } cached
                ? new CurrencyLookup(cached, null)
                : new CurrencyLookup(null, SourceFetchResult.Failed(JobFailureCategory.UnsupportedInput));
        }

        var response = await http.GetAsync(http.BuildUri(["tiingo", "daily", symbol]), cancellationToken);
        if (response.Status != HttpStatusCode.OK)
        {
            if (response.Status == HttpStatusCode.NotFound)
            {
                Metadata.SetNegative(symbol);
            }

            // A 204 has no venue to derive a currency from.
            return new CurrencyLookup(null, response.Status == HttpStatusCode.NoContent
                ? SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed)
                : Classify(response));
        }

        MetadataRow? row;
        try
        {
            row = IsJson(response) ? JsonSerializer.Deserialize<MetadataRow>(response.Body, Json) : null;
        }
        catch (JsonException)
        {
            row = null;
        }

        if (row is null)
        {
            return new CurrencyLookup(null, SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed));
        }

        var currency = string.Equals(row.Ticker, symbol, StringComparison.OrdinalIgnoreCase)
            && row.StartDate is not null && row.EndDate is not null
                ? TiingoVenues.CurrencyOf(row.ExchangeCode, symbol)
                : null;
        if (currency is null)
        {
            Metadata.SetNegative(symbol);
            return new CurrencyLookup(null, SourceFetchResult.Failed(JobFailureCategory.UnsupportedInput));
        }

        Metadata.SetPositive(symbol, currency);
        return new CurrencyLookup(currency, null);
    }

    /// <summary>The §3.3 status table, for every response that is not a 200 or 204.</summary>
    internal SourceFetchResult Classify(SourceHttpResponse response)
    {
        var status = (int)response.Status;
        return status switch
        {
            >= 300 and < 400 => SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed),
            206 or 413 => SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed),
            // A 402 is a per-symbol or per-plan refusal and never disables the source (A3).
            400 or 402 or 404 or 422 => SourceFetchResult.Failed(JobFailureCategory.UnsupportedInput),
            // Observed live (2026-11-11, recorded in the fixtures): Tiingo answers a missing or invalid token with 403,
            // and checks the token before the symbol. So 403, like 401, means the credential: the source is unusable
            // for the rest of the run rather than every binding failing one by one.
            401 or 403 => SourceFetchResult.Failed(JobFailureCategory.Configuration),
            429 => SourceFetchResult.Failed(JobFailureCategory.ProviderRateLimited, response.RetryAfter ?? UntilNextHour()),
            _ => SourceFetchResult.Failed(JobFailureCategory.ProviderUnavailable),
        };
    }

    /// <summary>
    /// Tiingo sends no <c>Retry-After</c> it documents, and a status code cannot say whether the hourly or the daily
    /// limit was hit, so the shorter reset is assumed: the next clock hour. A second refusal there backs off again.
    /// </summary>
    private TimeSpan UntilNextHour()
    {
        var now = timeProvider.GetUtcNow();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        return next - now;
    }

    private static bool IsJson(SourceHttpResponse response) =>
        string.Equals(response.ContentType, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The price rows, or <c>null</c> when anything is malformed: not a JSON array, a row without a parseable
    /// <c>date</c> or <c>close</c>. Only <c>close</c> (unadjusted; a fund's NAV) is read — every <c>adj*</c> field,
    /// <c>divCash</c> and <c>splitFactor</c> are ignored.
    /// </summary>
    internal static List<FetchedClose>? ParsePrices(byte[] body, string currency)
    {
        List<PriceRow>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<PriceRow>>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (rows is null)
        {
            return null;
        }

        var closes = new List<FetchedClose>(rows.Count);
        foreach (var row in rows)
        {
            if (row?.Close is not { } close || ParseDate(row.Date) is not { } date)
            {
                return null;
            }

            closes.Add(new FetchedClose(date, close, currency));
        }

        return closes;
    }

    /// <summary>
    /// Tiingo's <c>date</c> is an ISO timestamp (<c>2026-01-02T00:00:00.000Z</c>); its first ten characters are the
    /// trading date, parsed strictly with no time-zone conversion.
    /// </summary>
    internal static DateOnly? ParseDate(string? value) =>
        value is { Length: >= 10 }
        && (value.Length == 10 || value[10] == 'T')
        && DateOnly.TryParseExact(value.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private sealed record PriceRow(
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("close")] decimal? Close);

    /// <summary>Only these four fields are read; <c>name</c> and <c>description</c> are never deserialised.</summary>
    private sealed record MetadataRow(
        [property: JsonPropertyName("ticker")] string? Ticker,
        [property: JsonPropertyName("exchangeCode")] string? ExchangeCode,
        [property: JsonPropertyName("startDate")] string? StartDate,
        [property: JsonPropertyName("endDate")] string? EndDate);
}
