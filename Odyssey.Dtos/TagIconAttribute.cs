using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos;

/// <summary>
/// Accepts <c>null</c> (the default icon) or a <see cref="TagIcons"/> catalogue key (issues #279, #345).
/// </summary>
/// <remarks>
/// The bound is a compile-time allow-list, so it is an attribute rather than a service validator. The
/// default key, an empty or whitespace string, a differently-cased key and anything else outside the
/// catalogue are all rejected — one representation per state, and nothing the client could render as
/// markup. The message is fixed and never interpolates the submitted value.
///
/// <para>
/// A value longer than <see cref="TagIcons.MaxKeyLength"/> is left to the
/// <see cref="StringLengthAttribute"/> beside this one, which carries the same message, so a too-long
/// value yields one <c>errors.Icon</c> entry rather than two identical ones.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class TagIconAttribute : ValidationAttribute
{
    public TagIconAttribute() : base(TagIcons.InvalidIconMessage)
    {
    }

    public override bool IsValid(object? value) => value switch
    {
        null => true,
        string { Length: > TagIcons.MaxKeyLength } => true,
        string key => TagIcons.IsKnown(key),
        _ => false,
    };
}
