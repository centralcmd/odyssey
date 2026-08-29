using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Application;

/// <summary>
/// The write-side three-state value for one "count" import/export setting (issue #343 §6):
/// <see langword="null"/> on the containing <see cref="SystemSettingsUpdate"/> property means "leave
/// unchanged" (matching every other field on that DTO); a non-null <see cref="CapacityLimit"/> means
/// "set this field", and is itself one of two shapes — <c>{ unlimited: true }</c> (no limit) or
/// <c>{ value: 20000 }</c> (a finite cap). <see cref="Unlimited"/> and <see cref="Value"/> are
/// mutually exclusive; <see cref="Odyssey.Api.SystemSettings.SystemSettingsService"/> rejects any
/// other combination (both set, or neither) with a 400 naming the field.
/// </summary>
public sealed record CapacityLimit
{
    /// <summary>True = no limit. Mutually exclusive with <see cref="Value"/>.</summary>
    public bool Unlimited { get; set; }

    [Range(1, 1_000_000)]
    public int? Value { get; set; }
}
