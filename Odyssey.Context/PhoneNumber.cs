using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A phone number belonging to a <see cref="Contact"/> (issue #325). n:1 to the parent, with a
/// <see cref="Label"/> and an application-enforced single <see cref="IsPrimary"/> per contact.
/// </summary>
[Index(nameof(ContactId))]
public class PhoneNumber : IContactMethod
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ContactId { get; set; }

    public Contact Contact { get; set; } = null!;

    public PhoneLabel Label { get; set; }

    public bool IsPrimary { get; set; }

    [Required]
    [StringLength(ContactMethodLimits.PhoneMaxLength)]
    public required string Value { get; set; }
}
