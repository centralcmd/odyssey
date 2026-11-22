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
///
/// <para>
/// Two optional request headers override the factory's fixed values for one request, so one host can serve
/// several callers: <see cref="ActorOverrideHeader"/> (the user id) and <see cref="PermissionsOverrideHeader"/>
/// (the claim set). Both exist in this test assembly only — <c>Odyssey.Api</c> has no such header, and
/// production authentication never reads them. Tests set them through <see cref="SharedApiHostClients"/>
/// rather than by hand; the principal is otherwise identical to the one a factory built with the same
/// permissions issues (same name-identifier, name and one <see cref="PermissionClaims.Type"/> claim per
/// permission, no role claims).
/// </para>
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

    /// <summary>
    /// Optional request header overriding the actor id for one request, so a single host can serve two
    /// principals — what a per-user rate-limit partition can only be exercised with.
    /// </summary>
    public const string ActorOverrideHeader = "X-Test-Actor-Id";

    /// <summary>
    /// Optional request header overriding the factory's permission set for one request, so a single shared host
    /// can serve callers with different claims. Its value is a comma-separated claim list,
    /// <see cref="NoPermissionsValue"/> for an authenticated caller holding no claim, or
    /// <see cref="AnonymousValue"/> for no principal at all. Build it with <see cref="EncodePermissions"/>.
    /// </summary>
    public const string PermissionsOverrideHeader = "X-Test-Permissions";

    /// <summary><see cref="PermissionsOverrideHeader"/> value for an authenticated caller with an empty claim set.</summary>
    public const string NoPermissionsValue = "(none)";

    /// <summary><see cref="PermissionsOverrideHeader"/> value for an unauthenticated request (a 401, not a 403).</summary>
    public const string AnonymousValue = "(anonymous)";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var permissions = ResolvePermissions();
        if (permissions is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier,
                Request.Headers.TryGetValue(ActorOverrideHeader, out var actor) && !string.IsNullOrEmpty(actor)
                    ? actor.ToString()
                    : claimsProvider.ActorUserId),
            new(ClaimTypes.Name, "actor@example.com"),
        };
        claims.AddRange(permissions.Select(permission => new Claim(PermissionClaims.Type, permission)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>The request's permission override when it carries one, else the factory's fixed set.</summary>
    private IReadOnlyCollection<string>? ResolvePermissions() =>
        Request.Headers.TryGetValue(PermissionsOverrideHeader, out var header) && header.Count > 0
            ? DecodePermissions(header.ToString())
            : claimsProvider.Permissions;

    /// <summary>Encodes a claim set as a <see cref="PermissionsOverrideHeader"/> value; <see langword="null"/> is anonymous.</summary>
    public static string EncodePermissions(IReadOnlyCollection<string>? permissions)
    {
        if (permissions is null)
        {
            return AnonymousValue;
        }

        if (permissions.Count == 0)
        {
            return NoPermissionsValue;
        }

        if (permissions.Any(p => string.IsNullOrWhiteSpace(p) || p.Contains(',') || p != p.Trim()
            || p is NoPermissionsValue or AnonymousValue))
        {
            throw new ArgumentException(
                "A permission claim sent in the test header must be non-blank, carry no comma or edge whitespace, and not be a sentinel.",
                nameof(permissions));
        }

        return string.Join(',', permissions);
    }

    /// <summary>The inverse of <see cref="EncodePermissions"/>.</summary>
    public static IReadOnlyCollection<string>? DecodePermissions(string value) => value switch
    {
        AnonymousValue => null,
        NoPermissionsValue => [],
        _ => value.Split(','),
    };
}
