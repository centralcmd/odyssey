using Xunit;
using System.Security.Claims;
using Odyssey.Api.Controllers;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Odyssey.Api.Tests;

public sealed class AuthControllerTests
{
    [Fact]
    public void GetPermissions_ReturnsOnlyDistinctPermissionClaims()
    {
        var controller = new AuthController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "user-123"),
                    new Claim(PermissionClaims.Type, PermissionClaims.ContactsRead),
                    new Claim(PermissionClaims.Type, PermissionClaims.ContactsRead),
                    new Claim(PermissionClaims.Type, PermissionClaims.AccountsRead)
                ],
                "Cookies"))
            }
        };

        var result = controller.GetPermissions().Result as OkObjectResult;

        Assert.NotNull(result);
        var permissions = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Value);
        Assert.Equal(2, permissions.Count);
        Assert.Contains(PermissionClaims.ContactsRead, permissions);
        Assert.Contains(PermissionClaims.AccountsRead, permissions);
    }

    [Fact]
    public void GetClaims_ProjectsEveryClaim_AsTypeValuePairs_WithoutFiltering()
    {
        // GetClaims is the unfiltered counterpart to GetPermissions: it returns ALL claims
        // (identity + permission), preserving duplicates — the client reads the full set.
        var controller = new AuthController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "user-123"),
                    new Claim(PermissionClaims.Type, PermissionClaims.AccountsRead),
                    new Claim(PermissionClaims.Type, PermissionClaims.AccountsRead),
                ],
                "Cookies"))
            }
        };

        var result = controller.GetClaims().Result as OkObjectResult;

        Assert.NotNull(result);
        var claims = Assert.IsAssignableFrom<IReadOnlyList<AuthController.ClaimDto>>(result.Value);

        // Nothing is filtered or de-duplicated: the non-permission NameIdentifier is present and
        // the repeated permission claim appears twice.
        Assert.Equal(3, claims.Count);
        Assert.Contains(claims, c => c.Type == ClaimTypes.NameIdentifier && c.Value == "user-123");
        Assert.Equal(2, claims.Count(c => c.Type == PermissionClaims.Type && c.Value == PermissionClaims.AccountsRead));
    }
}
