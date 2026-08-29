using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// List-row projection for a photo: the scalar fields the grid needs (incl. <see cref="FileId"/> to build
/// the image URL), tag ids, and link counts. People/album names are not included — the read stays
/// link-ids-only (§10.5).
/// </summary>
public sealed record PhotoSummary
{
    public required Guid PhotoId { get; set; }

    public required Guid FileId { get; set; }

    [StringLength(PhotoLimits.MaxTitleLength)]
    public string? Title { get; set; }

    public DateTime? TakenAt { get; set; }

    [StringLength(PhotoLimits.MaxLocationNameLength)]
    public string? LocationName { get; set; }

    public int? PixelWidth { get; set; }

    public int? PixelHeight { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>Favourited timestamp; null = not a favourite.</summary>
    public DateTime? Favourited { get; set; }

    [StringLength(255)]
    public string? CreatedByUserId { get; set; }

    /// <summary>Display name of the creator, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? CreatedByName { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public List<Guid> PersonContactIds { get; set; } = [];

    public required int PersonCount { get; set; }

    public required int AlbumCount { get; set; }
}
