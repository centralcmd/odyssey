using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

// The API fails closed: AddAuthorization sets a FallbackPolicy of RequireAuthenticatedUser, so an
// endpoint that declares no authorization metadata of its own is unreachable anonymously rather
// than public by accident. That default is only safe while the genuinely public endpoints are
// explicitly exempt, and only useful while the authenticated ones are NOT — so both halves are
// pinned here.
//
// The sharp edge is MapIdentityApi: it maps /login and friends with no metadata (they need the
// exemption) and /manage/* behind its own RequireAuthorization. AllowAnonymous beats
// RequireAuthorization wherever both are present, so exempting the group wholesale would silently
// publish /manage/info and /manage/2fa. The exemption is therefore scoped by route pattern, and
// these tests fail if that scoping is ever widened.
public class FallbackAuthorizationPolicyTests
{
    private const string ActorUserId = "fallback-policy-actor-id";

    private static EndpointDataSource Endpoints(OdysseyApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>();

    private static RouteEndpoint? Find(OdysseyApiFactory factory, string route) =>
        Endpoints(factory).Endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(endpoint =>
                string.Equals(endpoint.RoutePattern.RawText, route, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void FallbackPolicy_RequiresAnAuthenticatedUser()
    {
        using var factory = new ApiFactory(permissions: null);

        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>();

        Assert.NotNull(options.Value.FallbackPolicy);
        Assert.Contains(options.Value.FallbackPolicy!.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/antiforgery/token")]
    [InlineData("/login")]
    [InlineData("/register")]
    [InlineData("/forgotPassword")]
    [InlineData("/resetPassword")]
    [InlineData("/refresh")]
    [InlineData("/confirmEmail")]
    [InlineData("/resendConfirmationEmail")]
    public void PublicEndpoints_AreExemptFromTheFallbackPolicy(string route)
    {
        using var factory = new ApiFactory(permissions: null);

        var endpoint = Find(factory, route);

        Assert.True(endpoint is not null, $"No endpoint is mapped at '{route}'.");
        Assert.True(endpoint!.Metadata.GetMetadata<IAllowAnonymous>() is not null,
            $"'{route}' must carry AllowAnonymous — the fallback policy would otherwise make it "
            + "require a login, which for /login is unrecoverable.");
    }

    // The whole point of scoping the exemption by route: these sit inside the same MapIdentityApi
    // group as /login but must stay behind authentication.
    [Theory]
    [InlineData("/manage/info")]
    [InlineData("/manage/2fa")]
    public void IdentityManageEndpoints_AreNotExempt(string route)
    {
        using var factory = new ApiFactory(permissions: null);

        var endpoint = Find(factory, route);

        Assert.True(endpoint is not null, $"No endpoint is mapped at '{route}'.");
        Assert.True(endpoint!.Metadata.GetMetadata<IAllowAnonymous>() is null,
            $"'{route}' manages a signed-in user's own account and must never be anonymous. "
            + "AllowAnonymous beats RequireAuthorization, so this usually means the exemption was "
            + "applied to the whole MapIdentityApi group instead of only its public routes.");
    }

    [Fact]
    public async Task ManageInfo_Unauthenticated_IsRefused()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/manage/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_Unauthenticated_IsReachable()
    {
        await using var factory = new ApiFactory(permissions: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class ApiFactory(IReadOnlyCollection<string>? permissions)
        : OdysseyApiFactory(permissions, ActorUserId);
}
