using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos;

/// <summary>
/// An enum-array list filter (issue #409 §5.2): at most <see cref="MaxCount"/> values, no value twice, and every
/// value a defined member other than the <c>Unknown</c> sentinel the tolerant converter reads into. A violation is
/// a <c>400</c> from model validation, before any query runs.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EnumFilterAttribute(Type enumType, int maxCount) : ValidationAttribute
{
    public Type EnumType { get; } = enumType;

    public int MaxCount { get; } = maxCount;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not System.Collections.IEnumerable items || value is string)
        {
            return ValidationResult.Success;
        }

        var seen = new HashSet<object>();
        foreach (var item in items)
        {
            if (item is null || item.GetType() != EnumType || !Enum.IsDefined(EnumType, item)
                || string.Equals(item.ToString(), "Unknown", StringComparison.Ordinal))
            {
                return Fail(validationContext, $"Every value must be a known {EnumType.Name}.");
            }

            if (!seen.Add(item))
            {
                return Fail(validationContext, "A value may appear only once.");
            }
        }

        return seen.Count > MaxCount
            ? Fail(validationContext, $"At most {MaxCount} values may be given.")
            : ValidationResult.Success;
    }

    private static ValidationResult Fail(ValidationContext context, string message) =>
        new(message, context.MemberName is { } member ? [member] : null);
}
