using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Accepts <c>null</c> (the default icon) or a <see cref="TransactionTagIcons"/> catalogue key (issue #279).
/// </summary>
/// <remarks>
/// The bound is a compile-time allow-list, so it is an attribute rather than a service validator. The
/// default key, an empty or whitespace string, a differently-cased key and anything else outside the
/// catalogue are all rejected — one representation per state, and nothing the client could render as
/// markup. The message is fixed and never interpolates the submitted value.
///
/// <para>
/// A value longer than <see cref="TransactionTagIcons.MaxKeyLength"/> is left to the
/// <see cref="StringLengthAttribute"/> beside this one, which carries the same message, so a too-long
/// value yields one <c>errors.Icon</c> entry rather than two identical ones.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class TransactionTagIconAttribute : ValidationAttribute
{
    public TransactionTagIconAttribute() : base(TransactionTagIcons.InvalidIconMessage)
    {
    }

    public override bool IsValid(object? value) => value switch
    {
        null => true,
        string { Length: > TransactionTagIcons.MaxKeyLength } => true,
        string key => TransactionTagIcons.IsKnown(key),
        _ => false,
    };
}
