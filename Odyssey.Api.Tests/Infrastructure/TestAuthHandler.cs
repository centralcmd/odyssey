using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Odyssey.Api.Tests.Infrastructure;

/// <summary>
/// Test authentication that bypasses the real login flow: it materializes a principal
/// from the <see cref="TestClaimsProvider"/> (a fixed actor id plus permission claims),
/// so authorization can be exercised without issuing cookies. A <see langword="null"/>
/// permission set yields no principal, producing the unauthenticated path.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TestClaimsProvider claimsProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OdysseyTestAuth";
    public const string DefaultActorUserId = "test-actor-id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (claimsProvider.Permissions is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, claimsProvider.ActorUserId),
            new(ClaimTypes.Name, "actor@example.com"),
        };
        claims.AddRange(claimsProvider.Permissions.Select(permission => new Claim(PermissionClaims.Type, permission)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
