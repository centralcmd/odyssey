using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos;

/// <summary>
/// Accepts <c>null</c> (no colour) or a <see cref="HexColor.IsValid"/> value (issue #392). An empty string
/// is rejected — <c>null</c> is the only "no colour", so each state has one representation. The message
/// is fixed and never interpolates the submitted value.
/// </summary>
/// <remarks>
/// A value longer than <see cref="HexColor.Length"/> is left to the <see cref="StringLengthAttribute"/>
/// beside this one, which carries the same message, so a too-long value yields one <c>errors.Color</c>
/// entry rather than two identical ones.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class HexColorAttribute : ValidationAttribute
{
    public HexColorAttribute() : base(HexColor.InvalidMessage)
    {
    }

    public override bool IsValid(object? value) => value switch
    {
        null => true,
        string { Length: > HexColor.Length } => true,
        string color => HexColor.IsValid(color),
        _ => false,
    };
}
