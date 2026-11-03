using Odyssey.Dtos;

namespace Odyssey.Core;

/// <summary>
/// The service-side re-check of a tag colour, shared by the four tag services (issue #392).
/// </summary>
/// <remarks>
/// Defence in depth for non-HTTP callers: <c>[HexColor]</c> already refuses an invalid colour on the HTTP
/// path before a service runs. Field-keyed so the <c>400</c> carries the same <c>errors.Color</c> shape,
/// and the message never echoes the submitted value. It calls <see cref="HexColor.IsValid"/>, the one
/// implementation — no length cap stands in front of it here, which is exactly why that check is a loop
/// and not a regex.
/// </remarks>
public static class TagColorGuard
{
    public static void EnsureValid(string? color)
    {
        if (color is not null && !HexColor.IsValid(color))
        {
            throw new DomainValidationException(HexColor.InvalidMessage, code: null, "Color");
        }
    }
}
