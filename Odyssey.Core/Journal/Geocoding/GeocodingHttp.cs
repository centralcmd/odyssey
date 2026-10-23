using System.Text.Json;

namespace Odyssey.Core.Journal.Geocoding;

/// <summary>
/// The shared outbound-call rules for the geocoding clients (issue #350 §7.6, §7.9, §9): the query is one
/// URL-encoded parameter, a provider gets 3 s, the response is read through a bounded buffer that aborts
/// past 256 KB of DECOMPRESSED content, and anything other than a 2xx JSON body is a failure.
/// </summary>
internal static class GeocodingHttp
{
    public const int MaxResponseBytes = 256 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };

    /// <summary>
    /// <see cref="Uri.EscapeDataString(string)"/>, never concatenation (S7): <c>&amp;</c>, <c>#</c>, <c>%</c>,
    /// <c>/</c> and Kartverket's own <c>*</c>/<c>~</c> search syntax all arrive as literal text in one value.
    /// </summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string relativeUri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // AllowAutoRedirect is off on the handler, so a 3xx lands here and is a failure like a 4xx/5xx.
            if (!response.IsSuccessStatusCode)
            {
                throw new GeocodingProviderException();
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                // The handler decompresses before this stream, so the cap counts inflated bytes and a small
                // gzip bomb is cut off exactly like a large plain body.
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new GeocodingProviderException();
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return JsonDocument.Parse(buffer, DocumentOptions);
        }
        catch (GeocodingProviderException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            throw new GeocodingProviderException();
        }
    }

    // Every accessor tolerates a provider sending the wrong JSON kind — TryGetProperty THROWS on anything
    // but an object, and an unexpected shape must degrade to "field absent", never to a 500.

    public static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? number
            : null;

    /// <summary>
    /// A coordinate as a decimal, or null when absent or outside ±<paramref name="limit"/> — checked BEFORE
    /// the conversion, which would otherwise throw on a value beyond decimal's range.
    /// </summary>
    public static decimal? Coordinate(JsonElement element, string property, double limit) =>
        Number(element, property) is { } value && Math.Abs(value) <= limit ? (decimal)value : null;

    /// <summary>A whole number of metres, or null when absent or implausibly large for an altitude.</summary>
    public static int? Metres(JsonElement element, string property) =>
        Number(element, property) is { } value && Math.Abs(value) <= 100_000
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>A number or a numeric string, as text — reference ids arrive as either.</summary>
    public static string? Scalar(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
