using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(FileMetadataId))]
[Index(nameof(AttachedAtUtc))]
[Index(nameof(TaxStatementId), nameof(FileMetadataId), IsUnique = true)]
public class TaxStatementFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid TaxStatementId { get; set; }

    [ForeignKey(nameof(TaxStatementId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public TaxStatement? TaxStatement { get; set; }

    [Required]
    public required Guid FileMetadataId { get; set; }

    [ForeignKey(nameof(FileMetadataId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileMetadata? FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    [Required]
    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public TaxStatementFileType FileType { get; set; } = TaxStatementFileType.Other;
}
