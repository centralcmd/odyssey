using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Application;

public sealed record UpdatedUser
{
    public bool? EmailConfirmed { get; init; }

    public bool? Enabled { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
