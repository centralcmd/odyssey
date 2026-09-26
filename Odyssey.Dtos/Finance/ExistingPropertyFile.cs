namespace Odyssey.Dtos.Finance;

/// <summary>A document attached to a property (issue #210 §5.2).</summary>
public sealed record ExistingPropertyFile : IAttributedFile
{
    public required Guid PropertyFileId { get; set; }

    public required Guid PropertyId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public PropertyFileType FileType { get; set; } = PropertyFileType.Other;

    public string? AttachedByUserId { get; set; }

    /// <summary>
    /// The display label for <see cref="AttachedByUserId"/>, resolved at the API edge under the
    /// CALLER's own claims (issue #106); <c>null</c> on any surface that does not resolve it.
    /// </summary>
    public string? AttachedByName { get; set; }

    public required DateTime AttachedAtUtc { get; set; }

    /// <summary>When the document takes effect. Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires. Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>
    /// Issuing contact id. The id alone — the name is deliberately not resolved, so no
    /// <c>contacts.read</c> data crosses onto a <c>properties.read</c> response (issue #210 §7.3),
    /// matching <see cref="ExistingContractFile.IssuedBy"/>.
    /// </summary>
    public Guid? IssuedBy { get; set; }
}
