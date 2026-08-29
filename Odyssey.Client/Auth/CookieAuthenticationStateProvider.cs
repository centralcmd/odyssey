using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

using Odyssey.ApiClient.Auth;

namespace Odyssey.Client.Auth;

public sealed class CookieAuthenticationStateProvider(AuthApiClient authApiClient) : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var isAuthenticated = await authApiClient.IsAuthenticatedAsync();
        if (!isAuthenticated)
        {
            return new AuthenticationState(Anonymous);
        }

        var claims = await authApiClient.GetClaimsAsync();
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")));
    }

    public Task RefreshAsync()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        return Task.CompletedTask;
    }
}
