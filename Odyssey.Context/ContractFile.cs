using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A reference attaching an already-uploaded <see cref="FileMetadata"/> record to a contract (the
/// #174 draft's <c>AppFile</c>). The same file may be attached to a contract at most once (enforced by
/// the unique index). Hard-deleting the contract cascades the link row; detaching or deleting the
/// underlying file is owned by the files API (issue #174 §6).
/// </summary>
[Index(nameof(FileMetadataId))]
[Index(nameof(AttachedAtUtc))]
[Index(nameof(ContractId), nameof(FileMetadataId), IsUnique = true)]
public class ContractFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ContractFileId { get; set; }

    [Required]
    public required Guid ContractId { get; set; }

    [ForeignKey(nameof(ContractId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Contract? Contract { get; set; }

    [Required]
    public required Guid FileMetadataId { get; set; }

    [ForeignKey(nameof(FileMetadataId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileMetadata? FileMetadata { get; set; }

    [Required]
    public ContractFileType FileType { get; set; } = ContractFileType.Other;

    public string? AttachedByUserId { get; set; }

    [Required]
    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;
}
