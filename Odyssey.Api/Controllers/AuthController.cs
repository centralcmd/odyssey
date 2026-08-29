using System.Security.Claims;
using Odyssey.Api.Identity;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Odyssey.Api.Controllers;

[ApiController]
[Authorize]
[Route("auth")]
public sealed class AuthController : ControllerBase
{
    // Both actions are exempt from the must-change-password block (issue #406): the client cannot render
    // an authenticated shell — including the change-password gate page itself — without them. The
    // attribute goes on each action rather than on the class, so a third action added here would be
    // blocked by default rather than silently inheriting the exemption.
    [HttpGet("claims")]
    [PasswordChangeExempt]
    public ActionResult<IReadOnlyList<ClaimDto>> GetClaims()
    {
        var claims = User.Claims
            .Select(claim => new ClaimDto(claim.Type, claim.Value))
            .ToList();
        return Ok(claims);
    }

    [HttpGet("permissions")]
    [PasswordChangeExempt]
    public ActionResult<IReadOnlyList<string>> GetPermissions()
    {
        var permissions = User.Claims
            .Where(claim => claim.Type == PermissionClaims.Type)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return Ok(permissions);
    }

    public sealed record ClaimDto(string Type, string Value);
}
