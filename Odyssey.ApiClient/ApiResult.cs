using System.Net;

namespace Odyssey.ApiClient;

/// <summary>
/// The outcome of an API call that returns no body. Success and failure are distinguished by
/// <see cref="Problem"/> being <c>null</c>, so a call that legitimately produced nothing is never
/// confused with one that failed.
/// </summary>
/// <remarks>
/// This library never presents errors itself — it has no notion of a toast, a dialog or a log. It
/// hands back the problem and lets the consumer decide. The Blazor client maps these onto MudBlazor
/// snackbars at the page call site via <c>ApiInteropExtensions</c>; a console consumer might print
/// or throw.
/// </remarks>
public readonly record struct ApiResult
{
    /// <summary>The response status. <see cref="default"/> when the request never completed (network error).</summary>
    public HttpStatusCode Status { get; init; }

    /// <summary>The parsed problem body on failure; <c>null</c> on success.</summary>
    public ApiProblem? Problem { get; init; }

    /// <summary>
    /// The <c>Location</c> header of a <c>201 Created</c> response. The API's create endpoints return
    /// <c>201</c> with an empty body, so this is the only place the new row's id appears — see
    /// <see cref="CreatedId"/>.
    /// </summary>
    public Uri? Location { get; init; }

    /// <summary>The id parsed from the trailing segment of <see cref="Location"/>, when it is a GUID.</summary>
    public Guid? CreatedId => ApiLocation.ExtractId(Location);

    public bool IsSuccess => Problem is null;

    /// <summary>The human-readable failure message, or <c>null</c> on success.</summary>
    public string? Error => Problem?.Message;

    public static ApiResult Success(HttpStatusCode status, Uri? location = null) =>
        new() { Status = status, Location = location };

    public static ApiResult Failure(HttpStatusCode status, ApiProblem problem) =>
        new() { Status = status, Problem = problem };

    /// <summary>A failure that never reached the server (network error, cancellation, bad URL).</summary>
    public static ApiResult Failure(Exception ex) =>
        new() { Problem = new ApiProblem { Detail = ex.Message } };
}

/// <summary>
/// The outcome of an API call that returns a <typeparamref name="T"/> body. Replaces the three
/// ad-hoc shapes the Blazor client used to carry: <c>(T Value, string? Error)</c>,
/// <c>(bool Ok, HttpStatusCode, ApiProblem)</c>, and the paged success/failure envelope.
/// </summary>
/// <remarks>
/// A successful call may still carry a <c>null</c> <see cref="Value"/> (a <c>204</c>, or a body that
/// deserialized to null). Use <see cref="ValueOr"/> when you need a guaranteed non-null value —
/// that is the "render an empty list rather than crash" path the pages rely on.
/// </remarks>
public readonly record struct ApiResult<T>
{
    public T? Value { get; init; }

    /// <inheritdoc cref="ApiResult.Status"/>
    public HttpStatusCode Status { get; init; }

    /// <inheritdoc cref="ApiResult.Problem"/>
    public ApiProblem? Problem { get; init; }

    /// <summary>
    /// The <c>Location</c> header of a <c>201 Created</c> response. The API's create endpoints return
    /// <c>201</c> with an empty body, so this is the only place the new row's id appears — see
    /// <see cref="CreatedId"/>.
    /// </summary>
    public Uri? Location { get; init; }

    /// <summary>The id parsed from the trailing segment of <see cref="Location"/>, when it is a GUID.</summary>
    public Guid? CreatedId => ApiLocation.ExtractId(Location);

    public bool IsSuccess => Problem is null;

    /// <inheritdoc cref="ApiResult.Error"/>
    public string? Error => Problem?.Message;

    /// <summary>The value on success, or <paramref name="fallback"/> on failure or a null body.</summary>
    public T ValueOr(T fallback) => IsSuccess && Value is not null ? Value : fallback;

    public static ApiResult<T> Success(T? value, HttpStatusCode status, Uri? location = null) =>
        new() { Value = value, Status = status, Location = location };

    public static ApiResult<T> Failure(HttpStatusCode status, ApiProblem problem) =>
        new() { Status = status, Problem = problem };

    /// <inheritdoc cref="ApiResult.Failure(Exception)"/>
    public static ApiResult<T> Failure(Exception ex) =>
        new() { Problem = new ApiProblem { Detail = ex.Message } };

    /// <summary>Carry a failure across a change of payload type, preserving status and problem.</summary>
    public ApiResult<TOther> CastFailure<TOther>() =>
        new() { Status = Status, Problem = Problem };
}

/// <summary>
/// Reads the created row's id out of a <c>201 Created</c> <c>Location</c> header. The API returns
/// <c>201</c> with an empty body throughout, so callers that need the new id have to parse it from
/// the header — logic that was previously hand-rolled at each call site.
/// </summary>
public static class ApiLocation
{
    public static Guid? ExtractId(Uri? location)
    {
        if (location is null)
            return null;

        var segment = location.OriginalString.TrimEnd('/').Split('/').LastOrDefault();
        return Guid.TryParse(segment, out var id) ? id : null;
    }
}
