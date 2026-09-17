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
}
