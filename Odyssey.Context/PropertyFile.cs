using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A reference attaching an already-uploaded <see cref="FileMetadata"/> record to a
/// <see cref="Property"/> as one of its documents (issue #210) — the deed, a valuation report, the
/// vehicle registration. Mirrors <see cref="ContractFile"/> field for field. The same file may be
/// attached to a property at most once (the unique index). Deleting the property or the file cascades
/// the link row; the file itself is owned by the files API and survives a property delete.
///
/// <para>
/// A sibling table rather than a widened <see cref="AccountFile"/>/<see cref="ContractFile"/>, for the
/// reason <see cref="PropertySmartTag"/> records: a polymorphic table loses its unique index, makes both
/// owner keys optional, and turns two cascade behaviours into application-code vigilance.
/// </para>
/// </summary>
[Index(nameof(FileMetadataId))]
[Index(nameof(AttachedAtUtc))]
[Index(nameof(IssuedBy))]
[Index(nameof(PropertyId), nameof(FileMetadataId), IsUnique = true)]
public class PropertyFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid PropertyFileId { get; set; }

    [Required]
    public required Guid PropertyId { get; set; }

    [ForeignKey(nameof(PropertyId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Property? Property { get; set; }

    [Required]
    public required Guid FileMetadataId { get; set; }

    [ForeignKey(nameof(FileMetadataId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileMetadata? FileMetadata { get; set; }

    [Required]
    public PropertyFileType FileType { get; set; } = PropertyFileType.Other;

    public string? AttachedByUserId { get; set; }

    [Required]
    public required DateTime AttachedAtUtc { get; set; }

    /// <summary>When the document takes effect (e.g. a warranty's start). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. insurance certificate, warranty). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing institution — a Contact (e.g. land registry, insurer). Optional. A real FK with
    /// <c>ON DELETE SET NULL</c>, declared in <see cref="OdysseyContext"/>.</summary>
    public Guid? IssuedBy { get; set; }
}
