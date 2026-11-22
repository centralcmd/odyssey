using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Api.ApiDocs;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// The API documentation must be unreachable in Production no matter what configuration says (issue #451 §1.3,
/// carried over to the OpenAPI document and Scalar by issue #569 §7.1).
/// </summary>
/// <remarks>
/// <para>
/// The config half of the gate used to be the whole story, and the container stack defaulted the flag to
/// <c>true</c>, so any deployment that did not explicitly pass the variable served the API's full surface through
/// the client's nginx proxy. The environment check is the half a hand-written env file, a <c>docker run</c> or a
/// Kubernetes manifest cannot bypass, and it is what these tests lock.
/// </para>
/// <para>
/// Every Production case sets <c>ApiDocs:Enabled=true</c> deliberately — the point is that the environment wins
/// over the flag, so a test that left the flag off would pass against a broken gate too. The controls run in
/// Staging with <c>ApiDocs:RequireLogin=false</c>, because this host has no test authentication.
/// </para>
/// </remarks>
public class ApiDocsProductionLockoutTests
{
    public static TheoryData<string> DocsPaths => new()
    {
        "/openapi/v1.json",
        "/scalar/v1",
        "/swagger/v1/swagger.json",
        "/swagger/index.html",
    };

    [Theory]
    [MemberData(nameof(DocsPaths))]
    public async Task Production_DoesNotServeTheDocs_EvenWhenTheFlagIsOn(string path)
    {
        using var factory = new LockoutFactory("Production", new() { [ApiDocsConfiguration.EnabledKey] = "true", [ApiDocsConfiguration.RequireLoginKey] = "false" });
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        // Not a 404 assertion: in Production an unmapped route falls through to UseHttpsRedirection, which answers
        // with a redirect. What matters is that no document is served.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    public async Task OutsideProduction_TheFlagServesTheDocs(string path)
    {
        // The control: the refusal above is the environment, not a factory that could never serve docs at all.
        using var factory = new LockoutFactory("Staging", new() { [ApiDocsConfiguration.EnabledKey] = "true", [ApiDocsConfiguration.RequireLoginKey] = "false" });
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task OutsideProduction_TheOldSwaggerRoutesServeNothing(string path)
    {
        using var factory = new LockoutFactory("Staging", new() { [ApiDocsConfiguration.EnabledKey] = "true", [ApiDocsConfiguration.RequireLoginKey] = "false" });
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OutsideProduction_TheRetiredKeyAloneServesNothing()
    {
        // Fail-closed rename: a deployment still setting only the old key gets docs OFF (issue #569 §7.1.2).
        using var factory = new LockoutFactory("Staging", new() { [ApiDocsConfiguration.RetiredEnabledKey] = "true", [ApiDocsConfiguration.RequireLoginKey] = "false" });
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Production_WithTheDocsOff_StillRefusesAMalformedRequireLogin()
    {
        // ApiDocs:RequireLogin is validated in every environment, so a typo is reported where it was written rather
        // than the day the docs are switched on (issue #569 §7.1.3).
        using var factory = new LockoutFactory("Production", new() { [ApiDocsConfiguration.RequireLoginKey] = "banana" });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ApiDocsConfiguration.RequireLoginKey, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("banana", failure.ToString(), StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(LockoutFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private sealed class LockoutFactory : WebApplicationFactory<Program>
    {
        private readonly string databaseName = $"ApiDocsProductionLockoutTests-{Guid.NewGuid()}";
        private readonly string environmentName;
        private readonly Dictionary<string, string?> settings;

        public LockoutFactory(string environmentName, Dictionary<string, string?> settings)
        {
            this.environmentName = environmentName;
            this.settings = settings;

            // AddDatabases runs before this factory's configuration is applied, so the in-memory selection has
            // to arrive as an environment variable.
            Environment.SetEnvironmentVariable("UseInMemoryDatabase", "true");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>(settings) { ["UseInMemoryDatabase"] = "true" }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<OdysseyContext>>();
                services.AddDbContext<OdysseyContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        }
    }
}
