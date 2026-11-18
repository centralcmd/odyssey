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

    /// <summary>
    /// The legacy database copy of the bytes (issue #448). Null once the file lives on the object store, when exactly one
    /// of the three stored-object columns below is set instead (<c>CK_FileMetadata_ExactlyOneSource</c>). Read it only
    /// through the file content source; a source-lint keeps it so.
    /// </summary>
    public Guid? FileBlobId { get; set; }

    /// <summary>The bytes on the store, feature <c>FinanceFiles</c> (RESTRICT, issue #448 §4.1).</summary>
    public Guid? FinanceStoredObjectId { get; set; }

    /// <summary>The bytes on the store, feature <c>JournalFiles</c> (RESTRICT, issue #448 §4.1).</summary>
    public Guid? JournalStoredObjectId { get; set; }

    /// <summary>The bytes on the store, feature <c>ContactAvatars</c> (RESTRICT, issue #448 §4.1).</summary>
    public Guid? ContactAvatarStoredObjectId { get; set; }

    [MaxLength(256)]
    public string? Description { get; set; }

    [Required]
    public required DateTime UploadedAtUtc { get; set; }

    [ForeignKey(nameof(FileBlobId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileBlob? FileBlob { get; set; }
}