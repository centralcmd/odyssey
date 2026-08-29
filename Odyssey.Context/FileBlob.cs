using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

public class FileBlob
{
    [Key]
    public required Guid Id { get; set; }

    [Required]
    public required byte[] Content { get; set; }

    public FileMetadata? FileMetadata { get; set; }
}