using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// Join row linking a <see cref="Photo"/> to a Contact of type Person. <see cref="ContactId"/>
/// is a real FK to the contact (cascading on delete); an unresolved id is dropped at read
/// time (§10.4).
/// </summary>
public class PhotoPerson
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PhotoPersonId { get; set; }

    public Guid PhotoId { get; set; }

    public Guid ContactId { get; set; }

    public Photo? Photo { get; set; }
}
