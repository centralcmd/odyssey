using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create/replace payload for a contact phone number (issue #325 §7).</summary>
public sealed record NewPhoneNumber
{
    [Required]
    [EnumDataType(typeof(PhoneLabel))]
    public PhoneLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    [Required]
    [StringLength(32)]
    [Phone]
    public required string Value { get; set; }
}

/// <summary>Read projection of a contact phone number (issue #325 §7).</summary>
public sealed record ExistingPhoneNumber
{
    public required Guid Id { get; set; }
    public required Guid ContactId { get; set; }
    public PhoneLabel Label { get; set; }
    public bool IsPrimary { get; set; }

    [StringLength(32)]
    public required string Value { get; set; }
}
