using System.Net;
using System.Text.Json.Nodes;
using Odyssey.Api.Tests.Infrastructure;
using Xunit;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// The generated OpenAPI document must carry at least the documentation the committed inventory records
/// (issue #569 §8): per operation, its summary, description, tag, parameters, request body content types
/// and response codes. The inventory was first captured from the Swashbuckle document, so this is what
/// proves the move to the built-in generator lost nothing.
/// </summary>
/// <remarks>
/// <para>
/// The check is one-directional. A <b>new</b> operation does not fail it; a <b>changed</b> summary,
/// description or response does, on purpose. The fix is never to hand-edit the fixture: regenerate it with
/// <see cref="RegenerateInventory"/> and review the diff in that pull request.
/// </para>
/// <para>
/// Anything the new document legitimately says differently is listed by name in
/// <see cref="OpenApiParityAllowList"/>, with the reason — never absorbed by loosening a comparison.
/// </para>
/// </remarks>
public class OpenApiParityTests(OpenApiParityTests.Host host) : SharedApiHostTests<OpenApiParityTests.Host>(host)
{
    internal const string RegenerateVariable = "ODYSSEY_REGENERATE_OPENAPI_INVENTORY";

    private static readonly string[] FixturePath = ["Odyssey.Api.Tests", "ApiDocs", "openapi-inventory.json"];

    [Fact]
    public async Task TheDocument_CarriesEveryInventoriedOperationsDocumentation()
    {
        var expected = OpenApiInventory.Deserialize(SourceTree.ReadAllText(FixturePath)).Operations;
        var actual = OpenApiInventory.FromDocument(await FetchDocumentAsync());

        var failures = new List<string>();
        foreach (var (key, want) in expected)
        {
            if (!actual.TryGetValue(key, out var got))
            {
                failures.Add($"{key}: operation missing");
                continue;
            }

            Compare(failures, key, "summary", want.Summary, got.Summary);
            Compare(failures, key, "description", want.Description, got.Description);
            Compare(failures, key, "tags", string.Join(",", want.Tags), string.Join(",", got.Tags));
            Compare(failures, key, "request body description", want.RequestDescription, got.RequestDescription);
            CompareSchema(failures, key, "request schema", want.RequestSchema, got.RequestSchema);
            Compare(failures, key, "request content types",
                string.Join(",", want.RequestContentTypes), string.Join(",", got.RequestContentTypes));

            foreach (var parameter in want.Parameters)
            {
                var match = got.Parameters.SingleOrDefault(candidate =>
                    candidate.Name == parameter.Name && candidate.In == parameter.In);
                var label = $"parameter {parameter.In}:{parameter.Name}";
                if (match is null)
                {
                    failures.Add($"{key}: {label} missing");
                    continue;
                }

                Compare(failures, key, $"{label} description", parameter.Description, match.Description);
                if (parameter.Required != match.Required
                    && !OpenApiParityAllowList.RequiredMismatches.ContainsKey($"{key}|{parameter.In}:{parameter.Name}"))
                {
                    failures.Add($"{key}: {label} required {parameter.Required} → {match.Required}");
                }
            }

            foreach (var (status, contentTypes) in want.Responses)
            {
                if (!got.Responses.TryGetValue(status, out var gotContentTypes))
                {
                    failures.Add($"{key}: response {status} missing");
                    continue;
                }

                Compare(failures, key, $"response {status} content types",
                    string.Join(",", contentTypes), string.Join(",", gotContentTypes));
            }

            foreach (var (status, schema) in want.ResponseSchemas)
            {
                CompareSchema(failures, key, $"response {status} schema", schema, got.ResponseSchemas.GetValueOrDefault(status));
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} documentation difference(s) against {string.Join('/', FixturePath)}:\n"
            + string.Join('\n', failures));
    }

    [Fact]
    public void EveryAllowListEntry_NamesAnInventoriedParameter()
    {
        // An exemption must not outlive the parameter it excuses.
        var expected = OpenApiInventory.Deserialize(SourceTree.ReadAllText(FixturePath)).Operations;
        var known = expected
            .SelectMany(operation => operation.Value.Parameters.Select(parameter =>
                $"{operation.Key}|{parameter.In}:{parameter.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(OpenApiParityAllowList.RequiredMismatches.Keys, key => Assert.Contains(key, known));
    }

    [Fact]
    public void EveryRenamedSchema_NamesAnInventoriedSchema()
    {
        var expected = OpenApiInventory.Deserialize(SourceTree.ReadAllText(FixturePath)).Operations;
        var known = expected.Values
            .SelectMany(operation => operation.ResponseSchemas.Values.Append(operation.RequestSchema ?? string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(OpenApiParityAllowList.RenamedSchemas.Keys, name => Assert.Contains(name, known));
    }

    /// <summary>
    /// The documented way to regenerate the fixture: set <c>ODYSSEY_REGENERATE_OPENAPI_INVENTORY=1</c> and run
    /// <c>dotnet test Odyssey.Api.Tests --filter FullyQualifiedName~OpenApiParityTests.RegenerateInventory</c>.
    /// Reported as Skipped otherwise, so an ordinary run can never rewrite it.
    /// </summary>
    [SkippableFact]
    public async Task RegenerateInventory()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(RegenerateVariable) == "1",
            $"Set {RegenerateVariable}=1 to regenerate the OpenAPI inventory fixture.");

        var file = new OpenApiInventory.InventoryFile
        {
            Header = new JsonObject
            {
                ["source"] = OpenApiDocumentPath,
                ["environment"] = "Testing (OdysseyApiFactory, API docs enabled)",
                ["regenerate"] = $"{RegenerateVariable}=1 dotnet test Odyssey.Api.Tests --filter FullyQualifiedName~OpenApiParityTests.RegenerateInventory",
                ["note"] = "Generated. Never hand-edit; regenerate and review the diff (issue #569).",
            },
            Operations = OpenApiInventory.FromDocument(await FetchDocumentAsync()),
        };

        await File.WriteAllTextAsync(SourceTree.File(FixturePath), OpenApiInventory.Serialize(file));
    }

    /// <summary>The document the inventory is read from.</summary>
    internal const string OpenApiDocumentPath = "/openapi/v1.json";

    private async Task<string> FetchDocumentAsync()
    {
        var response = await Factory.CreateClient().GetAsync(OpenApiDocumentPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// An empty <paramref name="want"/> accepts anything: the document must carry AT LEAST what the inventory
    /// recorded, so a description the old generator dropped and the new one renders is an improvement, not a
    /// difference (Swashbuckle ignored the descriptions on the seventeen <c>[FromBody]</c> parameters).
    /// </summary>
    private static void Compare(List<string> failures, string key, string what, string? want, string? got)
    {
        if (!string.IsNullOrEmpty(want) && !string.Equals(want, got, StringComparison.Ordinal))
        {
            failures.Add($"{key}: {what} \"{want}\" → \"{got}\"");
        }
    }

    private static void CompareSchema(List<string> failures, string key, string what, string? want, string? got)
    {
        if (string.IsNullOrEmpty(want) || string.Equals(want, got, StringComparison.Ordinal))
        {
            return;
        }

        if (OpenApiParityAllowList.RenamedSchemas.TryGetValue(want, out var renamed) && renamed.Name == got)
        {
            return;
        }

        failures.Add($"{key}: {what} \"{want}\" → \"{got}\"");
    }

    public sealed class Host() : OdysseyApiFactory(configuration: new Dictionary<string, string?> { ["ApiDocs:Enabled"] = "true", ["ApiDocs:RequireLogin"] = "false" });
}
