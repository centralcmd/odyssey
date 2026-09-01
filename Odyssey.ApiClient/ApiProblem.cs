using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odyssey.ApiClient;

/// <summary>
/// The client-side shape of an RFC 7807 <c>application/problem+json</c> error body, as emitted by
/// the API for every error response. <see cref="Code"/> captures the machine-readable extension some
/// responses carry (e.g. <c>feature_disabled</c> on a feature-off <c>503</c>). Use
/// <see cref="ApiProblemExtensions.ReadProblemAsync"/> to parse a response into this,
/// and <see cref="Message"/> to get a human-readable string for a toast or inline error — never the
/// raw JSON.
/// </summary>
public sealed record ApiProblem
{
    public string? Title { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }

    /// <summary>
    /// The machine-readable error code from the problem's <c>code</c> extension, when present
    /// (e.g. <c>feature_disabled</c>). Reserved for callers that need to discriminate a specific
    /// failure beyond the HTTP status — today the feature-disabled branches key on the <c>503</c>
    /// status itself, so nothing reads this yet; it is parsed so that wiring is already in place.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Per-field validation messages, keyed by the field name the server rejected — the standard
    /// <c>ValidationProblemDetails</c> <c>errors</c> shape (issue #421 Wave 0b).
    ///
    /// <para>
    /// Two server paths populate it. <c>[ApiController]</c> model validation emits it for every
    /// data-annotation failure at once, so a whole-resource <c>PUT</c> that violates three ranges
    /// reports all three; and <c>DomainValidationException</c> can now carry a field name so a
    /// semantic rejection lands here too, rather than only in <see cref="Code"/>.
    /// </para>
    ///
    /// <para>
    /// That "all at once" property is why this exists rather than a <see cref="Code"/>-to-field map:
    /// <see cref="Code"/> carries a single discriminator, so it can only ever name one offending
    /// field. Empty (never null) when the response carried no field errors.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The first message recorded against <paramref name="field"/>, or <see langword="null"/> when the
    /// server reported nothing for it. Case-insensitive, because ASP.NET keys model-validation errors
    /// by the JSON property name whose casing need not match the CLR one.
    /// </summary>
    public string? ErrorFor(string field) =>
        Errors.TryGetValue(field, out var messages) && messages.Length > 0 ? messages[0] : null;

    /// <summary>
    /// Any further problem-details extension members, unparsed. RFC 7807 lets a response carry
    /// arbitrary extensions, and a handful do — the blocked contact delete names which insurance link
    /// kinds refuse it, and which policies when the caller may see them (issue #27 §7 #5). Read one
    /// with <see cref="Extension{T}"/> rather than adding a typed property per feature, which would
    /// grow this shared shape for every consumer's private payload.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>
    /// Deserializes the named extension member, or <see langword="null"/> when the response carried no
    /// such member or it does not deserialize into <typeparamref name="T"/> — a malformed extension is
    /// an absent one, never a throw on an error path.
    /// </summary>
    public T? Extension<T>(string name)
        where T : class
    {
        if (Extensions is null || !Extensions.TryGetValue(name, out var element))
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(ApiProblemExtensions.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The HTTP reason phrase, captured at parse time as a fallback when <see cref="Detail"/> is absent.</summary>
    [JsonIgnore]
    public string? ReasonFallback { get; init; }

    /// <summary>
    /// A human-readable message: the <c>detail</c> when present, otherwise the HTTP reason phrase,
    /// otherwise a bare status code. Safe to show directly in a snackbar or inline error.
    /// </summary>
    [JsonIgnore]
    public string Message =>
        !string.IsNullOrWhiteSpace(Detail) ? Detail!
        : !string.IsNullOrWhiteSpace(ReasonFallback) ? ReasonFallback!
        : Status is int s ? $"HTTP {s}"
        : "Request failed.";
}

/// <summary>Helpers for turning an error <see cref="HttpResponseMessage"/> into an <see cref="ApiProblem"/>.</summary>
public static class ApiProblemExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The same web-defaults options, for reading a problem extension member.</summary>
    internal static JsonSerializerOptions SerializerOptions => Options;

    /// <summary>
    /// Reads an RFC 7807 <c>problem+json</c> error body into an <see cref="ApiProblem"/>. Guards
    /// against a non-JSON or empty body, in which case it returns a status-only problem whose
    /// <see cref="ApiProblem.Message"/> falls back to the response reason phrase.
    /// </summary>
    public static async Task<ApiProblem> ReadProblemAsync(this HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Options);
            if (problem is not null)
                return problem with
                {
                    Status = problem.Status ?? (int)response.StatusCode,
                    ReasonFallback = response.ReasonPhrase,
                };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Non-JSON (NotSupportedException) or malformed/empty body (JsonException) —
            // fall through to a status-only problem.
        }

        return new ApiProblem { Status = (int)response.StatusCode, ReasonFallback = response.ReasonPhrase };
    }
}
