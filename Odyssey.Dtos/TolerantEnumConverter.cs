using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos;

/// <summary>
/// Writes an enum as its member name and reads a name it does not know as the enum's <c>Unknown</c> member
/// instead of throwing (issue #409 §5, F8/FS1). A server that adds a member therefore never breaks a client
/// built before it: the client sees <c>Unknown</c> and renders its neutral fallback.
/// </summary>
/// <remarks>
/// It lives here, not in <c>Odyssey.ApiClient</c>, because it is applied by a <c>[JsonConverter]</c> attribute on
/// the enum itself, and this project — a leaf with zero project references — is the only one both halves of the
/// stack can name. Every consumer of the DTOs, the shared API client included, deserialises through it.
/// The <c>Unknown</c> member is a client-side sentinel: it is never stored, never sent by the server, and the
/// query-string filters reject it (<see cref="EnumFilterAttribute"/>).
/// </remarks>
public sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly TEnum UnknownValue = Enum.TryParse<TEnum>("Unknown", out var unknown)
        ? unknown
        : throw new InvalidOperationException($"{typeof(TEnum).Name} declares no Unknown member for the tolerant converter.");

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var name = reader.GetString();
                // Names only: a digit string would otherwise parse as any ordinal, defined or not.
                return name is { Length: > 0 } && !char.IsDigit(name[0]) && name[0] != '-'
                    && Enum.TryParse<TEnum>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : UnknownValue;
            case JsonTokenType.Number when reader.TryGetInt64(out var number):
                var value = (TEnum)Enum.ToObject(typeof(TEnum), number);
                return Enum.IsDefined(value) ? value : UnknownValue;
            default:
                reader.Skip();
                return UnknownValue;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
