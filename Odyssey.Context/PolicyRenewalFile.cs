using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(FileMetadataId))]
[Index(nameof(AttachedAtUtc))]
[Index(nameof(PolicyRenewalId), nameof(FileMetadataId), IsUnique = true)]
public class PolicyRenewalFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid PolicyRenewalId { get; set; }

    [ForeignKey(nameof(PolicyRenewalId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public PolicyRenewal? PolicyRenewal { get; set; }

    [Required]
    public required Guid FileMetadataId { get; set; }

    [ForeignKey(nameof(FileMetadataId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileMetadata? FileMetadata { get; set; }

    [Required]
    public PolicyFileType FileType { get; set; } = PolicyFileType.Other;

    public DateTime? EffectiveDate { get; set; }

    public string? AttachedByUserId { get; set; }

    [Required]
    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;
}
