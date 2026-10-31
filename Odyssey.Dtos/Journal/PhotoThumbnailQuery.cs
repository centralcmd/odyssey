using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// The thumbnail endpoint's query (issue #362 §5): <c>size</c> is required and must be a member, so a missing or
/// unknown value is a <c>400</c> from model validation. Bound case-insensitively.
/// </summary>
public sealed class PhotoThumbnailQuery
{
    [Required]
    [EnumDataType(typeof(ThumbnailSize))]
    public ThumbnailSize? Size { get; set; }
}
