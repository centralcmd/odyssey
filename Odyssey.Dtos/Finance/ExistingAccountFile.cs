namespace Odyssey.Dtos.Finance;

public sealed record ExistingAccountFile
{
    public required Guid Id { get; set; }

    public required Guid AccountId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public string? AttachedByUserId { get; set; }

    /// <summary>
    /// The display label for <see cref="AttachedByUserId"/>, resolved at the API edge under the
    /// CALLER's own claims (issue #106). It accompanies the id rather than replacing it — a
    /// read-modify-write round trip still needs the identifier — and it is <c>null</c> on any
    /// surface that does not resolve it, never a fallback rendered from the id.
    /// </summary>
    public string? AttachedByName { get; set; }

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
