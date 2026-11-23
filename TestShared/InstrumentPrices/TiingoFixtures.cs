using System.Net;
using System.Text;

namespace Odyssey.TestShared.InstrumentPrices;

/// <summary>
/// Answers Tiingo's two endpoints from the fixture folder (issue #447): <c>/tiingo/daily/{ticker}</c> (metadata) and
/// <c>/tiingo/daily/{ticker}/prices</c>. Plug a responder into <see cref="ScriptedPriceHandler.Respond"/>.
/// </summary>
public static class TiingoFixtures
{
    public const string Sentinel = "TIINGO-FIXTURE-SENTINEL";

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tiingo");

    public static string Read(string file) => System.IO.File.ReadAllText(Path.Combine(Directory, file));

    public static bool IsPrices(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/prices", StringComparison.Ordinal);

    /// <summary>A metadata fixture and a price fixture, each as a 200 JSON response.</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> Route(string metadata, string prices) =>
        request => File(IsPrices(request) ? prices : metadata);

    /// <summary>Metadata from <paramref name="metadata"/>, prices from <paramref name="prices"/>.</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> Route(
        string metadata, Func<HttpRequestMessage, HttpResponseMessage> prices) =>
        request => IsPrices(request) ? prices(request) : File(metadata);

    public static HttpResponseMessage File(string file, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json") =>
        new(status) { Content = new StringContent(Read(file), Encoding.UTF8, contentType) };

    /// <summary>One close per weekday of the requested window, Tiingo-shaped, 100 + the day of month.</summary>
    public static HttpResponseMessage WeekdayPrices(HttpRequestMessage request, Func<DateOnly, bool>? include = null)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var from = DateOnly.Parse(query["startDate"]!, System.Globalization.CultureInfo.InvariantCulture);
        var to = DateOnly.Parse(query["endDate"]!, System.Globalization.CultureInfo.InvariantCulture);
        var rows = new List<string>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (include?.Invoke(day) ?? true))
            {
                var close = 100 + day.Day;
                rows.Add($"{{\"date\":\"{day:yyyy-MM-dd}T00:00:00.000Z\",\"close\":{close},\"adjClose\":{close - 1}.5,\"divCash\":0.0,\"splitFactor\":1.0}}");
            }
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[" + string.Join(',', rows) + "]", Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A metadata document for <paramref name="ticker"/> on <paramref name="exchangeCode"/>.</summary>
    public static HttpResponseMessage Metadata(string ticker, string exchangeCode = "NASDAQ") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"ticker\":\"{ticker}\",\"name\":\"n\",\"exchangeCode\":\"{exchangeCode}\",\"startDate\":\"2000-01-03\",\"endDate\":\"2026-11-10\",\"description\":\"d\"}}",
            Encoding.UTF8, "application/json"),
    };

    /// <summary>Metadata echoing the requested ticker, and weekday prices: a healthy Tiingo for any symbol.</summary>
    public static HttpResponseMessage Healthy(HttpRequestMessage request) =>
        IsPrices(request) ? WeekdayPrices(request) : Metadata(TickerOf(request));

    public static string TickerOf(HttpRequestMessage request) =>
        Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2]);
}
