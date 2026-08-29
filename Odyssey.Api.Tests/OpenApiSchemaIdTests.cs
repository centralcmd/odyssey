using System.Net;
using System.Text.Json;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// Swashbuckle keys schemas on the short type name, so two Odyssey types sharing one name threw while
/// generating <c>/swagger/v1/swagger.json</c> — failing the entire document, not just those schemas
/// (found while implementing issue #382 point 4; Swagger UI was unusable until this was fixed). The
/// generator now qualifies only the ambiguous names with their namespace.
/// </summary>
public class OpenApiSchemaIdTests
{
    [Fact]
    public async Task TheDocument_GeneratesWithCollidingTypeNamesDisambiguated()
    {
        using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var ids = schemas.EnumerateObject().Select(schema => schema.Name).ToList();

        // Both ArchivalStatus enums survive as distinct schemas rather than one clobbering the other.
        // The ids are the module segment of the namespace — Odyssey.Dtos.Finance → "Finance" —
        // since the four DTO projects were merged into one and the whole namespace tail would now read
        // SharedDtosFinanceArchivalStatus.
        Assert.Contains("FinanceArchivalStatus", ids);
        Assert.Contains("JournalArchivalStatus", ids);
        // The other ambiguous pair: the identity-side profile Sex against the contact Sex, kept
        // deliberately distinct by issue #316 §6. The root of the merged DTO project prefixes as
        // "Shared", a module folder as its own name.
        Assert.Contains("ApplicationSex", ids);
        Assert.Contains("SharedSex", ids);

        // A non-ambiguous type keeps its short, readable id.
        Assert.Contains("ExistingAccount", ids);
    }

    /// <summary>
    /// A Dtos enum that merely shares its name with the entity copy in <c>Odyssey.Context</c>
    /// is not ambiguous on the OpenAPI surface — only the Dtos copy is a contract. Before issue #392
    /// the export document bound to the entity enums, both copies reached the generator, and these
    /// seven were pushed to module-qualified ids they never needed.
    /// </summary>
    [Theory]
    [InlineData("AccountType")]
    [InlineData("TermKind")]
    [InlineData("TermValueUnit")]
    [InlineData("BillingPeriod")]
    [InlineData("BudgetCategoryType")]
    [InlineData("AccountFileType")]
    [InlineData("TransactionFileType")]
    public async Task AnEnumDuplicatedOnlyByItsEntityCounterpart_KeepsItsShortId(string name)
    {
        var ids = await SchemaIdsAsync();

        Assert.Contains(name, ids);
        Assert.DoesNotContain("Finance" + name, ids);
    }

    private static async Task<IReadOnlyList<string>> SchemaIdsAsync()
    {
        using var factory = new OdysseyApiFactory(
            configuration: new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" });

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(schema => schema.Name).ToList();
    }
}
