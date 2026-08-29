using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos.Journal;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingTransaction
{
    public required Guid TransactionId { get; set; }
    public required string Description { get; set; }
    public required decimal Amount { get; set; }
    public required DateTime TimeStamp { get; set; } = DateTime.UtcNow;
    public required Guid AccountId { get; set; }
    public ExistingAccount? Account { get; set; }
    public Guid? ContactId { get; set; }
    public List<ExistingTransactionTag> TransactionTags { get; set; } = [];
    public ExistingContact? Contact { get; set; }

    [StringLength(3)]
    public string CurrencyCode { get; set; } = "USD";

    [StringLength(64)]
    public string? ExternalId { get; set; }

    [StringLength(64)]
    public string? InternalId { get; set; }

    [StringLength(1024)]
    public string? ExtraData { get; set; }

    public TransactionStatus Status { get; set; }

    [StringLength(256)]
    public string? StatusComment { get; set; }

    public DateTime StatusChangedAt { get; set; }

    public List<ExistingTransactionFile> TransactionFiles { get; set; } = [];
}
