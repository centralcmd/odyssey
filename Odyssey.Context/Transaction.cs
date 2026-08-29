using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

[Index(nameof(ContactId))]
[Index(nameof(CurrencyCode))]
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
