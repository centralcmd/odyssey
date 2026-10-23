using System.Net;
using System.Text.Json;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The outbound rules both MET clients share (issue #348 §7.6–7.8): a bounded read that aborts past the
/// response cap counted in <b>decompressed</b> bytes, a time budget per call, no redirect (the handler does
/// not follow them, so a 3xx lands here as a failure), and a mapping of every outcome onto
/// <see cref="WeatherFetchFailureKind"/>. Nothing from a failure — URL, body, exception text — is kept.
/// </summary>
internal static class WeatherHttp
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };

    internal sealed record Response(
        HttpStatusCode Status,
        JsonDocument? Body,
        DateTime? Expires,
        string? LastModified,
        TimeSpan? RetryAfter,
        WeatherFetchFailureKind? FailureKind)
        : IDisposable
    {
        public void Dispose() => Body?.Dispose();
    }

    /// <summary>
    /// Sends <paramref name="request"/> and reads the body through the cap. Cancellation of
    /// <paramref name="cancellationToken"/> (host shutdown) propagates; the budget expiring is a
    /// <see cref="WeatherFetchFailureKind.Timeout"/>.
    /// </summary>
    public static async Task<Response> SendAsync(
        HttpClient http, HttpRequestMessage request, int maxBytes, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var retryAfter = RetryAfter(response);
            var expires = response.Content.Headers.Expires?.UtcDateTime;
            var lastModified = response.Content.Headers.LastModified?.ToString("R");

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new Response(response.StatusCode, null, expires, lastModified, retryAfter, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => WeatherFetchFailureKind.ProviderRejected,
                    HttpStatusCode.TooManyRequests => WeatherFetchFailureKind.RateLimited,
                    _ => WeatherFetchFailureKind.ProviderError,
                };
                return new Response(response.StatusCode, null, expires, lastModified, retryAfter, kind);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return new Response(response.StatusCode, null, expires, lastModified, retryAfter,
                        WeatherFetchFailureKind.ResponseTooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            try
            {
                var document = JsonDocument.Parse(buffer, DocumentOptions);
                return new Response(response.StatusCode, document, expires, lastModified, retryAfter, null);
            }
            catch (JsonException)
            {
                return new Response(response.StatusCode, null, expires, lastModified, retryAfter,
                    WeatherFetchFailureKind.MalformedPayload);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Response(0, null, null, null, null, WeatherFetchFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            return new Response(0, null, null, null, null, WeatherFetchFailureKind.ProviderError);
        }
        catch (IOException)
        {
            return new Response(0, null, null, null, null, WeatherFetchFailureKind.ProviderError);
        }
    }

    /// <summary>The <c>User-Agent</c> MET's terms require: the application and a way to reach its operator.</summary>
    public static void SetUserAgent(HttpRequestMessage request, string contact)
    {
        // The contact has passed WeatherRules.ValidateContact (printable ASCII, no spaces), so it cannot
        // split the header; TryAddWithoutValidation keeps an e-mail address legal as a comment-free token.
        request.Headers.TryAddWithoutValidation("User-Agent", $"Odyssey/1.0 {contact}");
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    // Accessors that tolerate the wrong JSON kind: an unexpected shape degrades to "absent", never a throw.

    public static JsonElement? Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    public static string? String(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? Number(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? number
            : null;

    /// <summary>A value rounded to one decimal, or null when absent or outside <c>[min, max]</c> (§8).</summary>
    public static decimal? InRange(double? value, double min, double max) =>
        value is { } v && v >= min && v <= max
            ? Math.Round((decimal)v, 1, MidpointRounding.AwayFromZero)
            : null;
}
