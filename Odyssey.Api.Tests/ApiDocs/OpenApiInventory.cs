using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odyssey.Api.Tests.ApiDocs;

/// <summary>
/// A compact, generator-neutral extract of what an OpenAPI document tells a reader about each operation:
/// its summary, description, tag, parameters, request body content types and responses (issue #569 §8).
/// It is what the parity fixture stores and what <see cref="OpenApiParityTests"/> compares, so a document
/// can be checked for lost documentation without committing the multi-megabyte document itself.
/// </summary>
internal static class OpenApiInventory
{
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>Builds the inventory from a serialized OpenAPI 3.x document, keyed "METHOD /path".</summary>
    public static SortedDictionary<string, OperationEntry> FromDocument(string documentJson)
    {
        using var document = JsonDocument.Parse(documentJson);
        var operations = new SortedDictionary<string, OperationEntry>(StringComparer.Ordinal);

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in HttpMethods)
            {
                if (!path.Value.TryGetProperty(method, out var operation))
                {
                    continue;
                }

                operations[$"{method.ToUpperInvariant()} {path.Name}"] = new OperationEntry
                {
                    Summary = OptionalString(operation, "summary"),
                    Description = OptionalString(operation, "description"),
                    Tags = operation.TryGetProperty("tags", out var tags)
                        ? tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToList()
                        : [],
                    Parameters = operation.TryGetProperty("parameters", out var parameters)
                        ? parameters.EnumerateArray()
                            .Select(parameter => new ParameterEntry
                            {
                                Name = parameter.GetProperty("name").GetString() ?? string.Empty,
                                In = parameter.GetProperty("in").GetString() ?? string.Empty,
                                Required = parameter.TryGetProperty("required", out var required) && required.GetBoolean(),
                                Description = OptionalString(parameter, "description"),
                            })
                            .OrderBy(parameter => parameter.In, StringComparer.Ordinal)
                            .ThenBy(parameter => parameter.Name, StringComparer.Ordinal)
                            .ToList()
                        : [],
                    RequestDescription = operation.TryGetProperty("requestBody", out var requestBody)
                        ? OptionalString(requestBody, "description")
                        : null,
                    RequestSchema = operation.TryGetProperty("requestBody", out var schemaBody)
                        ? JsonSchemaName(schemaBody)
                        : null,
                    RequestContentTypes = operation.TryGetProperty("requestBody", out var body)
                        && body.TryGetProperty("content", out var bodyContent)
                            ? bodyContent.EnumerateObject().Select(content => content.Name).Order(StringComparer.Ordinal).ToList()
                            : [],
                    ResponseSchemas = operation.TryGetProperty("responses", out var schemaResponses)
                        ? schemaResponses.EnumerateObject()
                            .Select(response => (response.Name, Schema: JsonSchemaName(response.Value)))
                            .Where(response => response.Schema is not null)
                            .ToDictionary(response => response.Name, response => response.Schema!, StringComparer.Ordinal)
                        : [],
                    Responses = operation.TryGetProperty("responses", out var responses)
                        ? responses.EnumerateObject().ToDictionary(
                            response => response.Name,
                            response => response.Value.TryGetProperty("content", out var content)
                                ? content.EnumerateObject().Select(entry => entry.Name).Order(StringComparer.Ordinal).ToList()
                                : new List<string>(),
                            StringComparer.Ordinal)
                        : [],
                };
            }
        }

        return operations;
    }

    public static string Serialize(InventoryFile file) =>
        JsonSerializer.Serialize(file, SerializerOptions) + "\n";

    public static InventoryFile Deserialize(string json) =>
        JsonSerializer.Deserialize<InventoryFile>(json, SerializerOptions)
        ?? throw new InvalidOperationException("The OpenAPI inventory fixture is empty.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The component name of an <c>application/json</c> body's schema (<c>Name</c>, or <c>Name[]</c> for an array of
    /// one), or <see langword="null"/> when the body is not JSON or its schema is inline.
    /// </summary>
    private static string? JsonSchemaName(JsonElement holder)
    {
        if (!holder.TryGetProperty("content", out var content)
            || !content.TryGetProperty("application/json", out var json)
            || !json.TryGetProperty("schema", out var schema))
        {
            return null;
        }

        if (RefName(schema) is { } name)
        {
            return name;
        }

        return schema.TryGetProperty("items", out var items) && RefName(items) is { } itemName
            ? itemName + "[]"
            : null;
    }

    private static string? RefName(JsonElement schema) =>
        schema.TryGetProperty("$ref", out var reference) && reference.GetString() is { } value
            ? value[(value.LastIndexOf('/') + 1)..]
            : null;

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal sealed record InventoryFile
    {
        /// <summary>Where and how the inventory was captured, and how to regenerate it.</summary>
        public required JsonObject Header { get; init; }

        public required SortedDictionary<string, OperationEntry> Operations { get; init; }
    }

    internal sealed record OperationEntry
    {
        public string? Summary { get; init; }

        public string? Description { get; init; }

        public List<string> Tags { get; init; } = [];

        public List<ParameterEntry> Parameters { get; init; } = [];

        /// <summary>A <c>[FromBody]</c> parameter's description, which generators attach to the request body.</summary>
        public string? RequestDescription { get; init; }

        public List<string> RequestContentTypes { get; init; } = [];

        /// <summary>The JSON request body's schema component name (see <see cref="JsonSchemaName"/>).</summary>
        public string? RequestSchema { get; init; }

        /// <summary>Each JSON response's schema component name, by status code.</summary>
        public Dictionary<string, string> ResponseSchemas { get; init; } = [];

        public Dictionary<string, List<string>> Responses { get; init; } = [];
    }

    internal sealed record ParameterEntry
    {
        public required string Name { get; init; }

        public required string In { get; init; }

        public bool Required { get; init; }

        public string? Description { get; init; }
    }
}
