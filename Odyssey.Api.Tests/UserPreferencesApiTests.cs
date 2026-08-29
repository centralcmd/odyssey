using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Odyssey.Api.Tests;

public class UserPreferencesApiTests
{
    private const string TestUserId = "test-user-id";

    [Fact]
    public async Task UpsertThenGet_ReturnsPreference()
    {
        await using var factory = new ApiFactory(TestAuthHandler.SchemeName, TestAuthHandler.ConfigureScheme);
        using var client = factory.CreateClient();

        await CreateTestUserAsync(factory);

        var upsertResponse = await client.PutAsJsonAsync("/api/user-preferences/transactions-page", new UserPreferenceRequest(
            "{\"version\":1,\"columns\":[]}"));

        upsertResponse.EnsureSuccessStatusCode();

        var updatedPreference = await upsertResponse.Content.ReadFromJsonAsync<UserPreferenceResponse>();
        Assert.NotNull(updatedPreference);
        Assert.Equal("transactions-page", updatedPreference!.PageKey);

        var getResponse = await client.GetAsync("/api/user-preferences/transactions-page");

        getResponse.EnsureSuccessStatusCode();

        var fetchedPreference = await getResponse.Content.ReadFromJsonAsync<UserPreferenceResponse>();
        Assert.NotNull(fetchedPreference);
        Assert.Equal(updatedPreference.PreferencesJson, fetchedPreference!.PreferencesJson);
    }

    [Fact]
    public async Task UpsertPreference_PageKeyOverMaxLength_ReturnsBadRequestProblem()
    {
        await using var factory = new ApiFactory(TestAuthHandler.SchemeName, TestAuthHandler.ConfigureScheme);
        using var client = factory.CreateClient();

        await CreateTestUserAsync(factory);

        // UserPreference.Key is [Length(1, 256)]; a longer key must 400 rather than fail at the
        // database (architect finding F-14).
        var overLongPageKey = new string('a', 257);
        var response = await client.PutAsJsonAsync(
            $"/api/user-preferences/{overLongPageKey}", new UserPreferenceRequest("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UpsertPreference_EmptyPreferencesJson_ReturnsBadRequestProblem()
    {
        await using var factory = new ApiFactory(TestAuthHandler.SchemeName, TestAuthHandler.ConfigureScheme);
        using var client = factory.CreateClient();

        await CreateTestUserAsync(factory);

        var response = await client.PutAsJsonAsync("/api/user-preferences/transactions-page", new UserPreferenceRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetPreference_WithoutUserPreferenceReadClaim_ReturnsForbidden()
    {
        await using var factory = new ApiFactory(NoPermissionAuthHandler.SchemeName, NoPermissionAuthHandler.ConfigureScheme);
        using var client = factory.CreateClient();

        await CreateTestUserAsync(factory);

        var response = await client.GetAsync("/api/user-preferences/transactions-page");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task CreateTestUserAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = TestUserId,
            UserName = "preferences@example.com",
            Email = "preferences@example.com"
        };

        var existingUser = await userManager.FindByIdAsync(TestUserId);
        if (existingUser is not null)
        {
            return;
        }

        var result = await userManager.CreateAsync(user, "Password123!Safe");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    private sealed class ApiFactory(string schemeName, Action<AuthenticationBuilder> configureScheme) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = schemeName;
                        options.DefaultChallengeScheme = schemeName;
                    });

                configureScheme(services.AddAuthentication());
            });
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestAuth";

        public static void ConfigureScheme(AuthenticationBuilder authenticationBuilder)
        {
            authenticationBuilder.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(SchemeName, _ => { });
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, TestUserId),
                new Claim(ClaimTypes.Name, "preferences@example.com"),
                new Claim(PermissionClaims.Type, PermissionClaims.UserPreferencesRead),
                new Claim(PermissionClaims.Type, PermissionClaims.UserPreferencesUpdate)
            };

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class NoPermissionAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "NoPermissionAuth";

        public static void ConfigureScheme(AuthenticationBuilder authenticationBuilder)
        {
            authenticationBuilder.AddScheme<AuthenticationSchemeOptions, NoPermissionAuthHandler>(SchemeName, _ => { });
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, TestUserId),
                new Claim(ClaimTypes.Name, "preferences@example.com")
            };

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
