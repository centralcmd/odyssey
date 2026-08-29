using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(FileMetadataId))]
[Index(nameof(AttachedAtUtc))]
[Index(nameof(IssuedBy))]
[Index(nameof(AccountId), nameof(FileMetadataId), IsUnique = true)]
public class AccountFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid AccountId { get; set; }

    [ForeignKey(nameof(AccountId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Account? Account { get; set; }

    [Required]
    public required Guid FileMetadataId { get; set; }

    [ForeignKey(nameof(FileMetadataId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileMetadata? FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    [Required]
    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public required AccountFileType FileType { get; set; }

    /// <summary>When the document takes effect (e.g. policy start date). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. policy end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing institution — a Contact (e.g. bank, insurer). Optional. A real FK with
    /// <c>ON DELETE SET NULL</c>, declared in <see cref="OdysseyContext"/>; resolved for display via
    /// <c>IContactLookup</c>.</summary>
    public Guid? IssuedBy { get; set; }
}
