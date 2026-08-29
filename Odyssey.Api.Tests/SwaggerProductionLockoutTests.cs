using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Swagger must be unreachable in Production no matter what configuration says (issue #451 §1.3).
/// </summary>
/// <remarks>
/// <para>
/// The config half of <c>enableSwagger</c> used to be the whole story, and the container stack
/// defaults <c>Swagger__Enabled</c> to <c>true</c>, so any deployment that did not explicitly pass the
/// variable served the API's full surface at <c>/api/swagger</c> through the client's nginx proxy. The
/// overlay fix (setting the key the app actually binds) closes it for the compose path only; this
/// environment check is the half a hand-written env file, a <c>docker run</c> or a Kubernetes manifest
/// cannot bypass, and it is what these tests lock.
/// </para>
/// <para>
/// Every case sets <c>Swagger:Enabled=true</c> deliberately — the point is that the environment wins
/// over the flag, so a test that left the flag off would pass against the old code too.
/// </para>
/// </remarks>
public class SwaggerProductionLockoutTests
{
    [Theory]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task Production_DoesNotServeSwagger_EvenWhenTheFlagIsOn(string path)
    {
        using var factory = new SwaggerFactory("Production");
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        // Not a 404 assertion: UseHttpsRedirection is registered after the Swagger middleware, so in
        // Production a request that falls through the disabled middleware is answered with a redirect
        // rather than a not-found. What matters is that no document is served.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OutsideProduction_TheFlagStillServesSwagger()
    {
        // The control: the refusal above is the environment, not a factory that could never serve
        // Swagger in the first place.
        using var factory = new SwaggerFactory("Staging");
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("openapi", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient(SwaggerFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private sealed class SwaggerFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"SwaggerProductionLockoutTests-{Guid.NewGuid()}";
        private readonly string environmentName;

        public SwaggerFactory(string environmentName)
        {
            this.environmentName = environmentName;

            // AddDatabases runs before this factory's configuration is applied, so the in-memory
            // selection has to arrive as an environment variable — same reason
            // EmailOptionsProductionValidationTests does it this way.
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["UseInMemoryDatabase"] = "true",
                    ["Swagger:Enabled"] = "true",
                    // Production refuses to start without a relay (issue #405); supplying it keeps that
                    // rule from masking what this suite measures.
                    ["Email:SmtpHost"] = "smtp.example.test",
                    ["Email:FromAddress"] = "no-reply@odyssey.test"
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options =>
                    options.UseInMemoryDatabase(databaseName));
            });
        }
    }
}
