using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(UploadedByUserId))]
[Index(nameof(UploadedAtUtc))]
[Index(nameof(Sha256Hash))]
[Index(nameof(UploadedByUserId), nameof(Sha256Hash), nameof(SizeBytes))]
public class FileMetadata
{
    [Key]
    public required Guid Id { get; set; }

    public string? UploadedByUserId { get; set; }

    [Required]
    [MaxLength(256)]
    public required string FileName { get; set; }

    [Required]
    [MaxLength(256)]
    public required string ContentType { get; set; }

    [Required]
    [Range(0, long.MaxValue)]
    public required long SizeBytes { get; set; }

    [Required]
    [MaxLength(64)]
    public required string Sha256Hash { get; set; }

    [Required]
    public required Guid FileBlobId { get; set; }

    [MaxLength(256)]
    public string? Description { get; set; }

    [Required]
    public required DateTime UploadedAtUtc { get; set; }

    [ForeignKey(nameof(FileBlobId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileBlob? FileBlob { get; set; }
}