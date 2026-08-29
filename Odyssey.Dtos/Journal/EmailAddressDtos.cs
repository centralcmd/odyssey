using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>Create/replace payload for a contact email address (issue #325 §7).</summary>
public sealed record NewEmailAddress
{
    [Required]
    [EnumDataType(typeof(EmailLabel))]
    public EmailLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    [Required]
    [StringLength(256)]
    [EmailAddress]
    public required string Value { get; set; }
}

/// <summary>Read projection of a contact email address (issue #325 §7).</summary>
public sealed record ExistingEmailAddress
{
    public required Guid Id { get; set; }
    public required Guid ContactId { get; set; }
    public EmailLabel Label { get; set; }
    public bool IsPrimary { get; set; }

    [StringLength(256)]
    public required string Value { get; set; }
}
