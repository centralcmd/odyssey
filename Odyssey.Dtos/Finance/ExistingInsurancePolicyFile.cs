namespace Odyssey.Dtos.Finance;

public sealed record ExistingInsurancePolicyFile
{
    public required Guid Id { get; set; }

    public required Guid InsurancePolicyId { get; set; }

    public required ExistingFileMetadata FileMetadata { get; set; }

    public PolicyFileType FileType { get; set; } = PolicyFileType.Other;

    public DateTime? EffectiveDate { get; set; }

    public string? AttachedByUserId { get; set; }

    public required DateTime AttachedAtUtc { get; set; }
}
