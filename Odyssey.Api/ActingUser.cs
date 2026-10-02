using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Odyssey.Api;

/// <summary>
/// The one way a controller learns who is acting (issue #287 M4). Before this the id was read five
/// ways: defaulted to <c>"unknown"</c> (writing an audit line that names nobody), thrown as an
/// <see cref="InvalidOperationException"/> (a 500), a bare <c>Unauthorized()</c>, an
/// <c>UnauthorizedProblem</c>, or passed to the service as <c>null</c>.
/// </summary>
/// <remarks>
/// Every action that needs the id is behind authentication, so a missing one means a principal the
/// pipeline should never have admitted. The one rule is a <c>401</c> ProblemDetails, nothing written:
/// <code>if (User.ActingUserId() is not { } userId) return this.MissingUserProblem();</code>
/// </remarks>
public static class ActingUser
{
    /// <summary>The caller's user id, or <see langword="null"/> when the principal carries none.</summary>
    public static string? ActingUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id && !string.IsNullOrWhiteSpace(id)
            ? id
            : null;

    /// <summary>The <c>401</c> every action returns when <see cref="ActingUserId"/> is null.</summary>
    public static ObjectResult MissingUserProblem(this ControllerBase controller) =>
        controller.UnauthorizedProblem("User identity is missing from the request.");
}
