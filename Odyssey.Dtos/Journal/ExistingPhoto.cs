using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Journal;

/// <summary>
/// Full photo read model. Cross-context links are returned as ids only (§10.5); the client hydrates
/// names (tag ids → tag names, person ids → contact names, album ids → album names). The image
/// bytes are served separately via the existing authenticated <c>GET /api/files/{FileId}/content</c>.
/// </summary>
public sealed record ExistingPhoto
{
    public required Guid PhotoId { get; set; }

    public required Guid FileId { get; set; }

    /// <summary>The backing file's name (from the Files store), resolved at the API edge.</summary>
    [StringLength(256)]
    public string? FileName { get; set; }

    [StringLength(PhotoLimits.MaxTitleLength)]
    public string? Title { get; set; }

    [StringLength(PhotoLimits.MaxCaptionLength)]
    public string? Caption { get; set; }

    public DateTime? TakenAt { get; set; }

    public double? CapturedLatitude { get; set; }

    public double? CapturedLongitude { get; set; }

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

    [StringLength(255)]
    public string? UpdatedByUserId { get; set; }

    /// <summary>Display name of the last editor, resolved at the API edge; null if unresolved.</summary>
    [StringLength(256)]
    public string? UpdatedByName { get; set; }

    public required DateTime CreatedAt { get; set; }

    public required DateTime UpdatedAt { get; set; }

    public List<Guid> TagIds { get; set; } = [];

    public List<Guid> PersonContactIds { get; set; } = [];

    public List<Guid> AlbumIds { get; set; } = [];
}
