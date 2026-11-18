using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Finance.Instruments.Sources;

/// <summary>A bounded response: the status, the declared content type and at most <see cref="SourceHttpLimits.MaxResponseBytes"/> decompressed bytes.</summary>
public sealed record SourceHttpResponse(HttpStatusCode Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter);

/// <summary>The transport a source receives (issue #446 §3.2). Every rule a source could get wrong lives behind it.</summary>
public interface ISourceHttp
{
    /// <summary>
    /// A relative URI under the source's host: each path segment is escaped as one segment, each query value as one
    /// value. A segment that is or contains a dot segment is refused, because <see cref="Uri"/> collapses them.
    /// </summary>
    Uri BuildUri(IEnumerable<string> pathSegments, IReadOnlyDictionary<string, string>? query = null);

    /// <summary>One GET, with the framework's retry policy. 3xx is returned as-is, never followed.</summary>
    Task<SourceHttpResponse> GetAsync(Uri relativeUri, CancellationToken cancellationToken);
}

public static class SourceHttpLimits
{
    public const long MaxResponseBytes = 2L * 1024 * 1024;

    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(120);

    public const int MaxRetries = 2;

    public static string ClientName(string sourceKey) => $"instrument-price-source:{sourceKey}";
}

/// <summary>The source's credential is not usable: <see cref="SecretReadState.NotSet"/> or <see cref="SecretReadState.Unreadable"/>. Nothing was sent.</summary>
public sealed class SourceCredentialException(SecretReadState state)
    : Exception($"The price source credential is {state}; the request was not sent.")
{
    public SecretReadState State { get; } = state;
}

/// <summary>A request would have left the source's declared https host. Nothing was sent.</summary>
public sealed class SourceDestinationException() : Exception("A price source request targeted a host other than its declared https host.");

/// <summary>The source's daily request cap is reached. Nothing was sent.</summary>
public sealed class SourceQuotaExhaustedException() : Exception("The price source's daily request limit is reached.");

/// <summary>The response exceeded the size cap after decompression, or could not be read.</summary>
public sealed class SourceResponseMalformedException(string reason) : Exception(reason);

/// <summary>The secret wrapper a source credential travels in; <see cref="ToString"/> redacts.</summary>
public readonly record struct SourceSecret(string Value)
{
    public override string ToString() => "[redacted]";
}

/// <summary>
/// The framework-built transport for one source (issue #446 §3.2): host pin, credential (stripped then attached,
/// never sent without), daily quota per wire attempt, a retry policy that retries only 5xx (except 501) and
/// timeouts and never 429, per-attempt and total timeouts, and a decompressed-size cap. It logs one line per
/// attempt — source key, status, duration — never the URI, query, symbol or a header.
/// </summary>
public sealed class SourceHttp(
    IInstrumentPriceSource source,
    HttpClient client,
    ISecretSettingsReader secrets,
    ISourceRequestQuota quota,
    ILogger logger,
    TimeProvider timeProvider) : ISourceHttp
{
    private readonly Uri baseAddress = new($"https://{source.Host}/");

    /// <summary>Wire requests this transport sent (every attempt), for the run counters.</summary>
    public int RequestsMade { get; private set; }

    public Uri BuildUri(IEnumerable<string> pathSegments, IReadOnlyDictionary<string, string>? query = null)
    {
        var segments = new List<string>();
        foreach (var segment in pathSegments)
        {
            if (string.IsNullOrEmpty(segment) || InstrumentIdentifierRules.HasDotSegment(segment) || segment.Contains('/'))
            {
                throw new SourceDestinationException();
            }

            segments.Add(Uri.EscapeDataString(segment));
        }

        var path = string.Join('/', segments);
        if (query is { Count: > 0 })
        {
            path += "?" + string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        return new Uri(path, UriKind.Relative);
    }

    public async Task<SourceHttpResponse> GetAsync(Uri relativeUri, CancellationToken cancellationToken)
    {
        var absolute = relativeUri.IsAbsoluteUri ? relativeUri : new Uri(baseAddress, relativeUri);
        if (absolute.Scheme != Uri.UriSchemeHttps || absolute.Port != 443
            || !string.Equals(absolute.Host, source.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceDestinationException();
        }

        var secret = await ReadCredentialAsync(cancellationToken);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(SourceHttpLimits.TotalTimeout);

        for (var attempt = 0; ; attempt++)
        {
            if (!await quota.TryReserveAsync(source.Key, source.Limits.MaxRequestsPerDay, total.Token))
            {
                throw new SourceQuotaExhaustedException();
            }

            RequestsMade++;
            var started = Stopwatch.GetTimestamp();
            SourceHttpResponse? response = null;
            var timedOut = false;
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                attemptTimeout.CancelAfter(SourceHttpLimits.AttemptTimeout);
                using var request = BuildRequest(absolute, secret);
                using var message = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);
                response = new SourceHttpResponse(
                    message.StatusCode,
                    message.Content.Headers.ContentType?.MediaType,
                    await ReadBoundedAsync(message.Content, attemptTimeout.Token),
                    RetryAfterOf(message.Headers.RetryAfter));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (HttpRequestException)
            {
                timedOut = true;
            }
            finally
            {
                logger.LogInformation("Price source {SourceKey} request {Attempt}: {Status} in {ElapsedMs} ms.",
                    source.Key, attempt + 1, response is null ? "no response" : ((int)response.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            var retryable = timedOut || (response is { } r && (int)r.Status >= 500 && r.Status != HttpStatusCode.NotImplemented);
            if (!retryable || attempt >= SourceHttpLimits.MaxRetries || total.IsCancellationRequested)
            {
                return response ?? new SourceHttpResponse(HttpStatusCode.GatewayTimeout, null, [], null);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 250)), timeProvider, total.Token);
        }
    }

    private HttpRequestMessage BuildRequest(Uri absolute, SourceSecret? secret)
    {
        var target = absolute;
        if (source.CredentialPlacement is CredentialPlacement.Query query)
        {
            // Strip any same-named parameter the source put on the URI, then attach the credential.
            var kept = (absolute.Query.Length > 1 ? absolute.Query[1..] : string.Empty)
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(pair => !string.Equals(Uri.UnescapeDataString(pair.Split('=')[0]), query.Name, StringComparison.Ordinal));
            var parts = kept.ToList();
            if (secret is { } value)
            {
                parts.Add($"{Uri.EscapeDataString(query.Name)}={Uri.EscapeDataString(value.Value)}");
            }

            target = new UriBuilder(absolute) { Query = string.Join('&', parts) }.Uri;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, target);
        if (source.CredentialPlacement is CredentialPlacement.Header header)
        {
            request.Headers.Remove(header.Name);
            if (secret is { } value)
            {
                request.Headers.TryAddWithoutValidation(header.Name, value.Value);
            }
        }

        return request;
    }

    private async Task<SourceSecret?> ReadCredentialAsync(CancellationToken cancellationToken)
    {
        if (source.CredentialPlacement is CredentialPlacement.None || source.SecretKeys.Count == 0)
        {
            return null;
        }

        var result = await secrets.GetAsync(source.SecretKeys[0], cancellationToken);
        if (result.State == SecretReadState.Found && result.TryGetValue(out var plaintext))
        {
            return new SourceSecret(plaintext);
        }

        throw new SourceCredentialException(result.State);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > SourceHttpLimits.MaxResponseBytes)
        {
            throw new SourceResponseMalformedException("The response is larger than the size cap.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > SourceHttpLimits.MaxResponseBytes)
            {
                throw new SourceResponseMalformedException("The response is larger than the size cap after decompression.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header) => header switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - timeProvider.GetUtcNow() is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null,
    };
}
