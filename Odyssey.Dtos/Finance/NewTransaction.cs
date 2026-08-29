using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewTransaction
{
    [StringLength(256)]
    public required string Description { get; set; }
    public required decimal Amount { get; set; }
    public DateTime? TimeStamp { get; set; }
    public required Guid AccountId { get; set; }
    public List<Guid> TransactionTagIds { get; set; } = [];
    public Guid? ContactId { get; set; }

    [StringLength(3)]
    public string CurrencyCode { get; set; } = "USD";

    [StringLength(64)]
    public string? ExternalId { get; set; }

    [StringLength(64)]
    public string? InternalId { get; set; }

    [StringLength(1024)]
    public string? ExtraData { get; set; }

    [EnumDataType(typeof(TransactionStatus))]
    public TransactionStatus Status { get; set; } = TransactionStatus.New;

    [StringLength(256)]
    public string? StatusComment { get; set; }
}
