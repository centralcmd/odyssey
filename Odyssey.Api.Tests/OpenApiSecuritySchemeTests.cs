using System.Net;
using System.Text.Json;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// The generated OpenAPI document has to describe how the deployed client actually authenticates:
/// a cookie plus the <c>X-XSRF-TOKEN</c> antiforgery header on every write. Advertising only the bearer
/// scheme made "Try it out" fail with a bare 400 on any POST/PUT/DELETE (issue #382).
/// </summary>
public class OpenApiSecuritySchemeTests
{
    private static async Task<JsonElement> GetSwaggerDocumentAsync()
    {
        using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task TheDocument_DescribesTheAntiforgeryHeaderAlongsideBearer()
    {
        var root = await GetSwaggerDocumentAsync();

        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("bearer", out _));

        var antiforgery = schemes.GetProperty("antiforgery");
        Assert.Equal("apiKey", antiforgery.GetProperty("type").GetString());
        Assert.Equal("header", antiforgery.GetProperty("in").GetString());
        Assert.Equal("X-XSRF-TOKEN", antiforgery.GetProperty("name").GetString());
        // The description has to name the endpoint that mints the token, or "Try it out" is a dead end.
        Assert.Contains("/api/antiforgery/token", antiforgery.GetProperty("description").GetString());
    }

    [Fact]
    public async Task BothSchemes_AreListedAsDocumentLevelRequirements()
    {
        var root = await GetSwaggerDocumentAsync();

        var required = root.GetProperty("security")
            .EnumerateArray()
            .SelectMany(requirement => requirement.EnumerateObject().Select(scheme => scheme.Name))
            .ToList();

        Assert.Contains("bearer", required);
        Assert.Contains("antiforgery", required);
    }
}
