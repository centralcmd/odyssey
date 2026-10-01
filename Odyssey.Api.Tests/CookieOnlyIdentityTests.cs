using Xunit;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.Identity;
using Odyssey.Api.Tests.Infrastructure;

namespace Odyssey.Api.Tests;

/// <summary>
/// The API is cookie-only (issue #245): no bearer handler is registered, <c>/login</c> refuses to issue a
/// token and <c>/refresh</c> answers <c>404</c>. A bearer token would outlive a password reset, a disable
/// or a delete, because only the cookie re-checks the security stamp.
/// </summary>
public sealed class CookieOnlyIdentityTests : IDisposable
{
    private readonly PasswordGateFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task NoBearerScheme_IsRegistered_AndTheCookieIsTheDefault()
    {
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync(IdentityConstants.BearerScheme));
        // The composite AddIdentityApiEndpoints installs is internal, so assert the whole set instead:
        // only the four cookies AddIdentityCookies registers (and the test host adds none of its own).
        Assert.All(await schemes.GetAllSchemesAsync(), scheme =>
            Assert.Equal(typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler), scheme.HandlerType));
        Assert.Equal(IdentityConstants.ApplicationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
        Assert.Equal(IdentityConstants.ApplicationScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/login?useCookies=false")]
    [InlineData("/login?useCookies=false&useSessionCookies=false")]
    [InlineData("/login?useCookies=nonsense")]
    public async Task Login_WithoutACookieFlag_IsRefused_AndIssuesNoToken(string route)
    {
        var email = $"bearer-{Guid.NewGuid():N}@odyssey.test";
        await factory.CreateUserAsync(email);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(route, new { email, password = PasswordGateFactory.Password });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/login?useCookies=true")]
    [InlineData("/login?useSessionCookies=true")]
    [InlineData("/login?useCookies=false&useSessionCookies=true")]
    public async Task Login_AskingForACookie_StillSignsIn(string route)
    {
        var email = $"cookie-{Guid.NewGuid():N}@odyssey.test";
        await factory.CreateUserAsync(email);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(route, new { email, password = PasswordGateFactory.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/manage/info")).StatusCode);
    }

    [Fact]
    public async Task Refresh_IsNotFound()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/refresh", new { refreshToken = "anything" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_ApiCall_Is401_NotARedirect()
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/manage/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Forbidden_ApiCall_Is403_NotARedirect()
    {
        var email = $"guestless-{Guid.NewGuid():N}@odyssey.test";
        await factory.CreateUserAsync(email);
        var client = await factory.LoginAsync(email);
        using var _ = client;

        // No role, so no permission claims: any claim-gated endpoint forbids.
        var response = await client.GetAsync("/api/accounts");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public void BothRoutes_CarryTheRefusal()
    {
        var guarded = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<CookieOnlyIdentityMetadata>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(CookieOnlyIdentityEndpoints.LoginRoute, guarded);
        Assert.Contains(CookieOnlyIdentityEndpoints.RefreshRoute, guarded);
    }
}
