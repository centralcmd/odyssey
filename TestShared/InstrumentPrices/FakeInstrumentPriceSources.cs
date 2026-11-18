using System.Net;
using System.Text;
using System.Text.Json;
using Odyssey.Core.Finance.Instruments.Sources;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.TestShared.InstrumentPrices;

/// <summary>
/// A deterministic, test-only price source (issue #446 §3, component 13). It goes through the framework's
/// <see cref="ISourceHttp"/> like a real source, so the transport's host pin, credential, quota, retry and size
/// rules are exercised; <see cref="ScriptedPriceHandler"/> answers its requests. Never registered in a production host.
/// </summary>
public abstract class FakeInstrumentPriceSourceBase : IInstrumentPriceSource
{
    public const string SecretKey = "FakePriceSourceApiKey";

    public abstract string Key { get; }

    public string DisplayName => $"Fake {Key}";

    public string Attribution => $"Prices from Fake {Key}";

    public abstract string Host { get; }

    public CredentialPlacement CredentialPlacement => new CredentialPlacement.Query("api_token");

    public IReadOnlySet<InstrumentType> SupportedTypes { get; } =
        new HashSet<InstrumentType> { InstrumentType.Stock, InstrumentType.Etf, InstrumentType.Fund };

    public InstrumentPriceSourceLimits Limits { get; set; } = new(MaxDaysPerRequest: 30, TimeSpan.Zero, MaxRequestsPerRun: 20, MaxRequestsPerDay: 100);

    public IReadOnlyList<string> SecretKeys { get; } = [SecretKey];

    /// <summary>Set to make the source throw instead of returning a result.</summary>
    public Exception? Throw { get; set; }

    public async Task<SourceFetchResult> FetchClosesAsync(SourceFetchRequest request, ISourceHttp http, CancellationToken cancellationToken)
    {
        if (Throw is { } exception)
        {
            throw exception;
        }

        var uri = http.BuildUri(["eod", request.SourceSymbol], new Dictionary<string, string>
        {
            ["from"] = request.From.ToString("yyyy-MM-dd"),
            ["to"] = request.To.ToString("yyyy-MM-dd"),
        });
        var response = await http.GetAsync(uri, cancellationToken);
        var status = (int)response.Status;
        if (status is >= 300 and < 400)
        {
            return SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed);
        }

        switch (response.Status)
        {
            case HttpStatusCode.TooManyRequests:
                return SourceFetchResult.Failed(JobFailureCategory.ProviderRateLimited, response.RetryAfter);
            case HttpStatusCode.Unauthorized:
                return SourceFetchResult.Failed(JobFailureCategory.Configuration);
            case HttpStatusCode.NotFound or HttpStatusCode.BadRequest:
                return SourceFetchResult.Failed(JobFailureCategory.UnsupportedInput);
            case HttpStatusCode.NoContent:
                return SourceFetchResult.Success([]);
        }

        if (status != 200)
        {
            return SourceFetchResult.Failed(JobFailureCategory.ProviderUnavailable);
        }

        if (response.ContentType != "application/json")
        {
            return SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed);
        }

        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return SourceFetchResult.Success(rows.Select(r => new FetchedClose(r.Date, r.Close, r.Currency)).ToList());
        }
        catch (JsonException)
        {
            return SourceFetchResult.Failed(JobFailureCategory.ProviderMalformed);
        }
    }

    public sealed record Row(DateOnly Date, decimal Close, string? Currency);
}

public sealed class FakePrimarySource : FakeInstrumentPriceSourceBase, IInstrumentPriceSourceDeclaration
{
    public static string DeclaredKey => "fake-primary";

    public static string DeclaredHost => "primary.prices.example";

    public override string Key => DeclaredKey;

    public override string Host => DeclaredHost;
}

public sealed class FakeSecondarySource : FakeInstrumentPriceSourceBase, IInstrumentPriceSourceDeclaration
{
    public static string DeclaredKey => "fake-secondary";

    public static string DeclaredHost => "secondary.prices.example";

    public override string Key => DeclaredKey;

    public override string Host => DeclaredHost;
}

/// <summary>Answers source requests from a delegate and records every request it was sent.</summary>
public sealed class ScriptedPriceHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = WeekdayCloses("NOK");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    /// <summary>One close per weekday in the requested window, 100 + the day of month.</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> WeekdayCloses(string currency) => request =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var from = DateOnly.Parse(query["from"]!);
        var to = DateOnly.Parse(query["to"]!);
        var rows = new List<object>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                rows.Add(new { date = day.ToString("yyyy-MM-dd"), close = 100m + day.Day, currency });
            }
        }

        return Json(rows);
    };

    public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    }
}
