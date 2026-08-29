using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(FileMetadataId))]
[Index(nameof(AttachedAtUtc))]
[Index(nameof(TransactionId), nameof(FileMetadataId), IsUnique = true)]
public class TransactionFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid TransactionId { get; set; }

    [ForeignKey(nameof(TransactionId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Transaction? Transaction { get; set; }

    [Required]
    public required Guid FileMetadataId { get; set; }

    [ForeignKey(nameof(FileMetadataId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileMetadata? FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    [Required]
    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;

    public TransactionFileType Type { get; set; } = TransactionFileType.Other;
}
