using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Odyssey.Core.Ai;

/// <summary>
/// How a Messages API call failed, without a word of what the provider said (#602 §3.1, security R2-N1).
/// </summary>
public enum ClaudeTransportFailure
{
    /// <summary>A <c>3xx</c>: not followed, because the key and the document would go to an unconfigured host.</summary>
    Redirect,

    /// <summary>A non-success status other than the ones below.</summary>
    HttpError,

    /// <summary>The provider refused the request as invalid (<c>400</c> <c>invalid_request_error</c>) — for a document, unreadable or too long.</summary>
    InvalidRequest,

    /// <summary>A <c>2xx</c> whose body was empty or not a Messages response.</summary>
    EmptyResponse,
}

/// <summary>
/// A Messages API call that did not produce a response. Carries the HTTP status, the provider's error
/// <c>type</c> and its request id — never the response body, which is attacker-influenceable and may
/// echo the document.
/// </summary>
public sealed class ClaudeTransportException(ClaudeTransportFailure failure, int? statusCode, string? errorType, string? requestId)
    : Exception(Describe(failure, statusCode, errorType))
{
    public ClaudeTransportFailure Failure { get; } = failure;

    public int? StatusCode { get; } = statusCode;

    public string? ErrorType { get; } = errorType;

    public string? RequestId { get; } = requestId;

    private static string Describe(ClaudeTransportFailure failure, int? status, string? type) => failure switch
    {
        ClaudeTransportFailure.Redirect =>
            "The analysis provider redirected the request. Redirects are not followed because the API key "
            + "and the document would travel to a host that was never configured.",
        ClaudeTransportFailure.EmptyResponse => "Claude API returned an empty response.",
        _ => $"Claude API error {status}" + (string.IsNullOrEmpty(type) ? string.Empty : $" ({type})"),
    };
}

/// <summary>
/// The one Messages API send every Claude flow uses (statement analysis, AI recipe import). It refuses
/// redirects, and on failure it logs and throws the status, the error type and the request id only.
/// The error type is read from the JSON envelope and checked against a fixed set; nothing else of the
/// body is read, logged or kept.
/// </summary>
internal static class ClaudeMessages
{
    /// <summary>Root-absolute so an administrator's base URL contributes scheme and authority only.</summary>
    public const string MessagesPath = "/v1/messages";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new ContentConverter() },
    };

    /// <summary>Known Anthropic error types. Anything else is reported as "other", so no provider text is echoed.</summary>
    private static readonly HashSet<string> KnownErrorTypes = new(StringComparer.Ordinal)
    {
        "invalid_request_error", "authentication_error", "permission_error", "not_found_error",
        "request_too_large", "rate_limit_error", "api_error", "overloaded_error",
    };

    public static Uri EndpointFor(string baseUrl) => new(new Uri(baseUrl, UriKind.Absolute), MessagesPath);

    public static async Task<ClaudeResponse> SendAsync(
        HttpClient httpClient, ILogger logger, string baseUrl, ClaudeRequest request, string flow, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointFor(baseUrl))
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var status = (int)response.StatusCode;
        var requestId = response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;

        if (status is >= 300 and < 400)
        {
            // The Location header is chosen by the responding host: it reaches the log as a host name at
            // most, never the path or query.
            var to = response.Headers.Location is { IsAbsoluteUri: true } loc ? loc.Host : "(relative or none)";
            logger.LogError("The {Flow} provider answered {StatusCode} towards {Host}; redirects are not followed.", flow, status, to);
            throw new ClaudeTransportException(ClaudeTransportFailure.Redirect, status, null, requestId);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorType = await ReadErrorTypeAsync(response, cancellationToken);
            logger.LogError("The {Flow} provider answered {StatusCode} ({ErrorType}), request id {RequestId}.",
                flow, status, errorType ?? "unknown", requestId ?? "none");
            var failure = status == 400 && errorType == "invalid_request_error"
                ? ClaudeTransportFailure.InvalidRequest
                : ClaudeTransportFailure.HttpError;
            throw new ClaudeTransportException(failure, status, errorType, requestId);
        }

        ClaudeResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null || parsed.Content is null)
        {
            logger.LogError("The {Flow} provider answered {StatusCode} with no readable response, request id {RequestId}.",
                flow, status, requestId ?? "none");
            throw new ClaudeTransportException(ClaudeTransportFailure.EmptyResponse, status, null, requestId);
        }

        return parsed;
    }

    private static async Task<string?> ReadErrorTypeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ClaudeErrorEnvelope>(JsonOptions, cancellationToken);
            var type = envelope?.Error?.Type;
            return type is null ? null : KnownErrorTypes.Contains(type) ? type : "other";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class ContentConverter : JsonConverter<ClaudeContent>
    {
        public override ClaudeContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ClaudeContent value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
