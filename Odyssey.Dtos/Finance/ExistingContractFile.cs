namespace Odyssey.Dtos.Finance;

public sealed record ExistingContractFile
{
    public required Guid ContractFileId { get; set; }

    public required Guid ContractId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public ContractFileType FileType { get; set; } = ContractFileType.Other;

    public string? AttachedByUserId { get; set; }

    public required DateTime AttachedAtUtc { get; set; }
}
