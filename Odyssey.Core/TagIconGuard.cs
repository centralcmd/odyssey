using Odyssey.Dtos;

namespace Odyssey.Core;

/// <summary>
/// The service-side re-check of a tag icon, shared by the four tag services (issues #279 §8, #345).
/// </summary>
/// <remarks>
/// Defence in depth for non-HTTP callers: <c>[TagIcon]</c> already refuses an unknown icon on the HTTP
/// path before a service runs. Field-keyed so the <c>400</c> carries the same <c>errors.Icon</c> shape,
/// and the message never echoes the submitted value.
/// </remarks>
public static class TagIconGuard
{
    public static void EnsureKnown(string? icon)
    {
        if (icon is not null && !TagIcons.IsKnown(icon))
        {
            throw new DomainValidationException(TagIcons.InvalidIconMessage, code: null, "Icon");
        }
    }
}
