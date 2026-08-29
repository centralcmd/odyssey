namespace Odyssey.Dtos.Finance;

public sealed record ExistingAccountFile
{
    public required Guid Id { get; set; }

    public required Guid AccountId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    public required DateTime AttachedAtUtc { get; set; } = DateTime.UtcNow;

    public required AccountFileType FileType { get; set; }

    /// <summary>When the document takes effect (e.g. policy start date). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. policy end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Issuing contact id (e.g. bank, insurer). Optional.</summary>
    public Guid? IssuedBy { get; set; }
}
