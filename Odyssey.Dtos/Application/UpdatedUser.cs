using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Application;

public sealed record UpdatedUser
{
    public bool? EmailConfirmed { get; set; }

    public bool? Enabled { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
