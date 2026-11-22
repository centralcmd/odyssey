using System.Net;
using System.Text.Json;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Component names are keyed on the short type name, so two Odyssey types sharing one name would collide.
/// Under Swashbuckle that threw while generating the document — failing all of it, not just those schemas
/// (found while implementing issue #382 point 4). The generator qualifies only the ambiguous names with their
/// namespace, and these ids are the published names: issue #569 kept them stable across the move to the
/// built-in generator.
/// </summary>
public class OpenApiSchemaIdTests(OpenApiSchemaIdTests.Host host) : SharedApiHostTests<OpenApiSchemaIdTests.Host>(host)
{
    [Fact]
    public async Task TheDocument_GeneratesWithCollidingTypeNamesDisambiguated()
    {
        var response = await Factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var ids = schemas.EnumerateObject().Select(schema => schema.Name).ToList();

        // The one shared ArchivalStatus enum (issue #287 L4 merged the Finance and Journal copies)
        // is not ambiguous, so it keeps its short id.
        Assert.Contains("ArchivalStatus", ids);
        Assert.DoesNotContain("FinanceArchivalStatus", ids);
        Assert.DoesNotContain("JournalArchivalStatus", ids);
        // The ambiguous pair: the identity-side profile Sex against the contact Sex, kept
        // deliberately distinct by issue #316 §6. The root of the merged DTO project prefixes as
        // "Shared", a module folder as its own name.
        Assert.Contains("ApplicationSex", ids);
        Assert.Contains("SharedSex", ids);

        // A non-ambiguous type keeps its short, readable id.
        Assert.Contains("ExistingAccount", ids);
        Assert.Contains("ExistingPropertyFile", ids);
    }

    /// <summary>
    /// A Dtos enum that merely shares its name with the entity copy in <c>Odyssey.Context</c>
    /// is not ambiguous on the OpenAPI surface — only the Dtos copy is a contract. Before issue #392
    /// the export document bound to the entity enums, both copies reached the generator, and these
    /// were pushed to module-qualified ids they never needed.
    /// </summary>
    [Theory]
    [InlineData("AccountType")]
    [InlineData("TermValueUnit")]
    [InlineData("Interval")]
    [InlineData("BudgetCategoryType")]
    [InlineData("AccountFileType")]
    [InlineData("TransactionFileType")]
    [InlineData("PropertyFileType")]
    public async Task AnEnumDuplicatedOnlyByItsEntityCounterpart_KeepsItsShortId(string name)
    {
        var ids = await SchemaIdsAsync();

        Assert.Contains(name, ids);
        Assert.DoesNotContain("Finance" + name, ids);
    }

    private async Task<IReadOnlyList<string>> SchemaIdsAsync()
    {
        var response = await Factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(schema => schema.Name).ToList();
    }

    /// <summary>The class's shared host: the document is anonymous, so only the configuration differs.</summary>
    public sealed class Host() : OdysseyApiFactory(configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });
}
