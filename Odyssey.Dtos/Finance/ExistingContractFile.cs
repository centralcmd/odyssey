namespace Odyssey.Dtos.Finance;

public sealed record ExistingContractFile : IAttributedFile
{
    public required Guid ContractFileId { get; set; }

    public required Guid ContractId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public ContractFileType FileType { get; set; } = ContractFileType.Other;

    public string? AttachedByUserId { get; set; }

    /// <summary>
    /// The display label for <see cref="AttachedByUserId"/>, resolved at the API edge under the
    /// CALLER's own claims (issue #106). It accompanies the id rather than replacing it — a
    /// read-modify-write round trip still needs the identifier — and it is <c>null</c> on any
    /// surface that does not resolve it, never a fallback rendered from the id.
    /// </summary>
    public string? AttachedByName { get; set; }

    public required DateTime AttachedAtUtc { get; set; }

    /// <summary>When the document takes effect (e.g. agreement start date). Optional.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>When the document expires (e.g. agreement end, warranty expiry). Optional.</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>Date the document was issued/signed. Optional.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>
    /// Issuing contact id (e.g. bank, insurer). Optional. The id alone — the name is deliberately not
    /// resolved server-side, unlike <see cref="AttachedByName"/>: resolving it would move a contact
    /// attribute across the <c>contacts.read</c> boundary onto a <c>contracts.read</c> response
    /// (issue #146 §7.3), matching <c>ExistingAccountFile.IssuedBy</c>.
    /// </summary>
    public Guid? IssuedBy { get; set; }
}
