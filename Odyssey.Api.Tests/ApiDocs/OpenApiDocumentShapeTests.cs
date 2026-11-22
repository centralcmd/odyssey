using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Api.ApiDocs;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// What the served document says about itself (issue #569 AC 2, §7.4.4) and that the persistence guard is wired into
/// the generator, not just written (AC 14).
/// </summary>
public class OpenApiDocumentShapeTests(OpenApiDocumentShapeTests.Host host) : SharedApiHostTests<OpenApiDocumentShapeTests.Host>(host)
{
    [Fact]
    public async Task TheDocument_IsOpenApi31_NamedOdyssey_WithNoServers()
    {
        using var document = JsonDocument.Parse(await Factory.CreateClient().GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;

        Assert.StartsWith("3.1", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal("Odyssey API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
        Assert.True(!root.TryGetProperty("servers", out var servers) || servers.GetArrayLength() == 0,
            "The document names a server; \"try it\" must follow the page's own origin.");
    }

    [Fact]
    public async Task ASpoofedHostHeader_IsNotReflectedIntoTheDocument()
    {
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Host = "attacker.invalid";

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("attacker.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGenerator_RefusesAnEndpointThatExposesAnEntityType()
    {
        // The real registration, the real generator, one endpoint returning a persistence type. An entity ENUM, because
        // an entity class's cyclic navigations overflow the schema exporter before any id is asked for, which would
        // fail this test for the wrong reason; the enum generates cleanly unless the guard refuses it.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddOpenApiDocument();
        await using var app = builder.Build();
        app.MapGet("/leak", () => Results.Ok()).Produces<Odyssey.Context.AccountType>();
        await app.StartAsync();

        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(OpenApiDocumentRegistration.DocumentName);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOpenApiDocumentAsync());

        Assert.StartsWith(typeof(Odyssey.Context.AccountType).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Contains("persistence entity type", failure.Message, StringComparison.Ordinal);
        Assert.Contains("#392", failure.Message, StringComparison.Ordinal);
    }

    public sealed class Host() : OdysseyApiFactory(configuration: new Dictionary<string, string?>
    {
        [ApiDocsConfiguration.EnabledKey] = "true",
        [ApiDocsConfiguration.RequireLoginKey] = "false",
    });
}
