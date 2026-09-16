using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

[Index(nameof(ContactId))]
[Index(nameof(CurrencyCode))]
// The list's dominant shape: filter by account, order by date (TransactionService.ListAsync).
// Leading with AccountId also satisfies EF's foreign-key index convention, so this replaces the
// standalone IX_Transactions_AccountId rather than adding alongside it.
//
// Amount is included so the net-worth history's bucketed aggregate (issue #90 §5.3) is served from
// the index rather than from the heap: it groups by (AccountId, date parts) and sums Amount over the
// whole table on every request. For the same reason this REPLACES (AccountId, TimeStamp) rather than
// joining it — the two-column index is a strict prefix of this one, so keeping both would cost an
// extra secondary-index write on every insert to the app's highest-volume table for no read benefit.
[Index(nameof(AccountId), nameof(TimeStamp), nameof(Amount))]
// Status is a list filter and sort key, and GetSummary groups the whole table by it for the page
// header on every load.
[Index(nameof(Status))]
public class Transaction
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid TransactionId { get; set; }
    
    [StringLength(256)]
    [Required]
    public required string Description { get; set; }
    
    [Required]
    [Precision(18, 6)]
    public required decimal Amount { get; set; }
    
    [Required]
    public required DateTime TimeStamp { get; set; } = DateTime.UtcNow;
    
    [Required]
    public required Guid AccountId { get; set; }
    
    [ForeignKey(nameof(AccountId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Account? Account { get; set; }

    // A real FK to Contact with ON DELETE SET NULL, declared in OdysseyContext; validated on write and
    // resolved for display via IContactLookup.
    public Guid? ContactId { get; set; }

    // Many-to-many with TransactionTag through the TransactionTagLink join entity. The skip
    // navigation drives reads/mapping; the link collection exposes the raw join rows.
    public ICollection<TransactionTag> TransactionTags { get; set; } = new List<TransactionTag>();

    public ICollection<TransactionTagLink> TransactionTagLinks { get; set; } = new List<TransactionTagLink>();

    [StringLength(64)]
    public string? ExternalId { get; set; }

    [StringLength(64)]
    public string? InternalId { get; set; }

    [StringLength(1024)]
    public string? ExtraData { get; set; }

    [Required]
    public TransactionStatus Status { get; set; } = TransactionStatus.New;

    [StringLength(256)]
    public string? StatusComment { get; set; }

    [Required]
    public DateTime StatusChangedAt { get; set; } = DateTime.UtcNow;

    [StringLength(3)]
    [Required]
    public string CurrencyCode { get; set; } = "USD";

    public ICollection<TransactionFile> TransactionFiles { get; set; } = new List<TransactionFile>();
}
