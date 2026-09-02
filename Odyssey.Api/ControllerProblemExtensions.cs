using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Odyssey.Api;

/// <summary>
/// Helpers that turn a handled error into an RFC 7807 <see cref="ProblemDetails"/> response so every
/// controller emits the same <c>application/problem+json</c> body instead of the previous mix of bare
/// strings and ad-hoc DTOs. The <see cref="ProblemDetails"/> is built directly (rather than via
/// <c>ControllerBase.Problem()</c>) so the helpers also work in unit tests that new up a controller
/// without an <c>HttpContext</c>. Unhandled exceptions take the same shape via
/// <see cref="GlobalExceptionHandler"/>.
/// </summary>
public static class ControllerProblemExtensions
{
    public static ObjectResult NotFoundProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status404NotFound, detail);

    public static ObjectResult BadRequestProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status400BadRequest, detail);

    public static ObjectResult UnauthorizedProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status401Unauthorized, detail);

    public static ObjectResult ConflictProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status409Conflict, detail);

    /// <summary>
    /// A conflict that carries structured detail the client needs in order to recover — the blocked
    /// contact delete names which insurance link kinds block it, and (claims permitting) which policies
    /// (issue #27 §7 #5). The extensions ride alongside the standard problem members.
    /// </summary>
    public static ObjectResult ConflictProblem(
        this ControllerBase controller, string detail, IDictionary<string, object?> extensions) =>
        Problem(StatusCodes.Status409Conflict, detail, extensions);

    /// <summary>A well-formed request that cannot be fulfilled — distinct from a genuine 409 conflict.</summary>
    public static ObjectResult UnprocessableEntityProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status422UnprocessableEntity, detail);

    public static ObjectResult LockedProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status423Locked, detail);

    public static ObjectResult TooManyRequestsProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status429TooManyRequests, detail);

    public static ObjectResult ForbiddenProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status403Forbidden, detail);

    public static ObjectResult ServiceUnavailableProblem(this ControllerBase controller, string detail) =>
        Problem(StatusCodes.Status503ServiceUnavailable, detail);

    private static ObjectResult Problem(
        int statusCode, string detail, IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = ReasonPhrases.GetReasonPhrase(statusCode),
            Detail = detail,
        };

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" },
        };
    }
}
