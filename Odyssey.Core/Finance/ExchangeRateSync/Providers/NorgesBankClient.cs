using System.Globalization;
using System.Net;
using System.Text.Json;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.ExchangeRateSync.Providers;

/// <summary>
/// The Norges Bank SDMX-JSON adapter (issue #349 §3). It fetches business-day spot rates quoted against
/// NOK for a set of codes over a span, and the series list with each series' first and newest dates.
/// </summary>
/// <remarks>
/// <para>
/// The base address is a compile-time constant set at registration, redirects are not followed, and the
/// body is read through a bounded buffer that aborts past <see cref="ExchangeRateSyncConstants.MaxResponseBytes"/>
/// of DECOMPRESSED content. A plain <see cref="HttpClient"/>, deliberately not the standard resilience
/// handler, whose own timeouts and retries would compound with the 15 s timeout and the single retry here.
/// </para>
/// <para>
/// The series key is built only from the codes the caller passes, which the sync service takes from the
/// intersection of the enabled set with the provider's own list — never from request input.
/// </para>
/// </remarks>
public sealed class NorgesBankClient(HttpClient http, ExchangeRateSyncPacing pacing)
{
    public const string BaseAddress = "https://data.norges-bank.no/api/";

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };

    /// <summary>
    /// Every published NOK series with its first and newest observation date. Two requests:
    /// <c>lastNObservations=1</c> and <c>firstNObservations=1</c>.
    /// </summary>
    public async Task<IReadOnlyList<ProviderSeries>> GetSeriesAsync(CancellationToken cancellationToken)
    {
        var newest = await GetDatesAsync("lastNObservations=1", cancellationToken);
        await pacing.Delay(ExchangeRateSyncConstants.RequestSpacing, cancellationToken);
        var first = await GetDatesAsync("firstNObservations=1", cancellationToken);

        return newest
            .Select(pair => new ProviderSeries(
                pair.Key, first.TryGetValue(pair.Key, out var since) ? since : null, pair.Value))
            .OrderBy(series => series.CurrencyCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The observations for <paramref name="codes"/> from <paramref name="from"/> to <paramref name="to"/>, inclusive.</summary>
    public async Task<IReadOnlyList<ProviderObservation>> GetObservationsAsync(
        IReadOnlyCollection<string> codes, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var key = string.Join('+', codes.Select(Uri.EscapeDataString));
        var uri = $"data/EXR/B.{key}.NOK.SP?format=sdmx-json&locale=en"
            + $"&startPeriod={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $"&endPeriod={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

        using var document = await GetJsonWithRetryAsync(uri, cancellationToken);
        if (document is null)
        {
            // 404 is SDMX for "no observations in this span" (a span of holidays only).
            return [];
        }

        return ParseObservations(document.RootElement);
    }

    private async Task<Dictionary<string, DateOnly>> GetDatesAsync(string selector, CancellationToken cancellationToken)
    {
        using var document = await GetJsonWithRetryAsync(
            $"data/EXR/B..NOK.SP?format=sdmx-json&locale=en&{selector}", cancellationToken);
        var result = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        if (document is null)
        {
            return result;
        }

        foreach (var observation in ParseObservations(document.RootElement))
        {
            if (!result.TryGetValue(observation.CurrencyCode, out var known) || observation.Date > known)
            {
                result[observation.CurrencyCode] = observation.Date;
            }
        }

        return result;
    }

    /// <summary>
    /// One request with the issue #349 §9 retry policy: a transient failure (timeout, 5xx, network) is
    /// retried once after 5 s; a 429 is retried once after its <c>Retry-After</c> when that is at most 60 s.
    /// </summary>
    private async Task<JsonDocument?> GetJsonWithRetryAsync(string relativeUri, CancellationToken cancellationToken)
    {
        try
        {
            return await GetJsonAsync(relativeUri, cancellationToken);
        }
        catch (ExchangeRateProviderException failure) when (failure.RetryAfter is { } wait)
        {
            await pacing.Delay(wait, cancellationToken);
            return await GetJsonAsync(relativeUri, cancellationToken);
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExchangeRateSyncConstants.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ExchangeRateProviderException(
                    ExchangeRateSyncErrorKind.ProviderRateLimited, RetryAfter(response));
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new ExchangeRateProviderException(
                    ExchangeRateSyncErrorKind.ProviderUnavailable, ExchangeRateSyncConstants.RetryDelay);
            }

            // AllowAutoRedirect is off on the handler, so a 3xx lands here and is malformed like a 4xx.
            if (!response.IsSuccessStatusCode)
            {
                throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
            {
                // The handler decompresses before this stream, so the cap counts inflated bytes.
                if (buffer.Length + read > ExchangeRateSyncConstants.MaxResponseBytes)
                {
                    throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return JsonDocument.Parse(buffer, DocumentOptions);
        }
        catch (JsonException)
        {
            throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExchangeRateProviderException(
                ExchangeRateSyncErrorKind.ProviderUnavailable, ExchangeRateSyncConstants.RetryDelay);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ExchangeRateProviderException(
                ExchangeRateSyncErrorKind.ProviderUnavailable, ExchangeRateSyncConstants.RetryDelay);
        }
    }

    /// <summary>
    /// <c>Retry-After</c> parsed defensively — delta-seconds or an HTTP-date. Unparseable, negative or
    /// longer than 60 s yields null, which defers to the next tick instead of retrying in-run.
    /// </summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta
            ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } value && value >= TimeSpan.Zero && value <= ExchangeRateSyncConstants.MaxRetryAfter
            ? value
            : null;
    }

    /// <summary>
    /// Flattens an SDMX-JSON 1.0 document into observations. Series keys index into the series
    /// dimensions, observation keys into the time dimension, and each series' attribute indices into the
    /// series attributes. Any shape that does not match is <c>ProviderMalformed</c> for the whole request;
    /// a single unusable observation is passed through for the caller to count as rejected.
    /// </summary>
    internal static List<ProviderObservation> ParseObservations(JsonElement root)
    {
        try
        {
            var structure = root.GetProperty("data").GetProperty("structure");
            var dimensions = structure.GetProperty("dimensions");
            var seriesDimensions = dimensions.GetProperty("series").EnumerateArray().ToList();
            var baseIndex = seriesDimensions.FindIndex(d => d.GetProperty("id").GetString() == "BASE_CUR");
            if (baseIndex < 0)
            {
                throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
            }

            var baseValues = seriesDimensions[baseIndex].GetProperty("values").EnumerateArray()
                .Select(v => v.GetProperty("id").GetString() ?? string.Empty)
                .ToList();
            var periods = dimensions.GetProperty("observation")[0].GetProperty("values").EnumerateArray()
                .Select(v => v.GetProperty("id").GetString() ?? string.Empty)
                .ToList();

            var attributes = structure.GetProperty("attributes").GetProperty("series").EnumerateArray().ToList();
            var unitIndex = attributes.FindIndex(a => a.GetProperty("id").GetString() == "UNIT_MULT");
            var unitValues = unitIndex < 0
                ? []
                : attributes[unitIndex].GetProperty("values").EnumerateArray()
                    .Select(v => v.GetProperty("id").GetString())
                    .ToList();

            var result = new List<ProviderObservation>();
            var dataSets = root.GetProperty("data").GetProperty("dataSets");
            if (dataSets.GetArrayLength() == 0)
            {
                return result;
            }

            foreach (var series in dataSets[0].GetProperty("series").EnumerateObject())
            {
                var keyParts = series.Name.Split(':');
                if (keyParts.Length <= baseIndex
                    || !int.TryParse(keyParts[baseIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var codeIndex)
                    || codeIndex < 0 || codeIndex >= baseValues.Count)
                {
                    throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
                }

                var code = baseValues[codeIndex];
                string? unitMult = null;
                if (unitIndex >= 0
                    && series.Value.TryGetProperty("attributes", out var seriesAttributes)
                    && seriesAttributes.ValueKind == JsonValueKind.Array
                    && seriesAttributes.GetArrayLength() > unitIndex
                    && seriesAttributes[unitIndex].ValueKind == JsonValueKind.Number
                    && seriesAttributes[unitIndex].TryGetInt32(out var unitValueIndex)
                    && unitValueIndex >= 0 && unitValueIndex < unitValues.Count)
                {
                    unitMult = unitValues[unitValueIndex];
                }

                if (!series.Value.TryGetProperty("observations", out var observations)
                    || observations.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var observation in observations.EnumerateObject())
                {
                    if (!int.TryParse(observation.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var periodIndex)
                        || periodIndex < 0 || periodIndex >= periods.Count
                        || !DateOnly.TryParseExact(periods[periodIndex], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date))
                    {
                        throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
                    }

                    var value = observation.Value.ValueKind == JsonValueKind.Array
                        && observation.Value.GetArrayLength() > 0
                        && observation.Value[0].ValueKind == JsonValueKind.String
                            ? observation.Value[0].GetString()
                            : null;

                    result.Add(new ProviderObservation(code, date, value, unitMult));
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new ExchangeRateProviderException(ExchangeRateSyncErrorKind.ProviderMalformed);
        }
    }
}

/// <summary>One published series: its code and its first and newest observation dates.</summary>
public sealed record ProviderSeries(string CurrencyCode, DateOnly? FirstObservation, DateOnly NewestObservation);

/// <summary>
/// One raw observation, as published. <see cref="Value"/> and <see cref="UnitMult"/> are the provider's
/// strings, unchecked — the sync service applies the structural checks and counts what it rejects.
/// </summary>
public sealed record ProviderObservation(string CurrencyCode, DateOnly Date, string? Value, string? UnitMult);

/// <summary>
/// A provider request failed. Carries only the classification and, for a transient failure, how long to
/// wait before the one retry — never the provider's body or the request URL.
/// </summary>
public sealed class ExchangeRateProviderException(ExchangeRateSyncErrorKind kind, TimeSpan? retryAfter = null) : Exception
{
    public ExchangeRateSyncErrorKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
