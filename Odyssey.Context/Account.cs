using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

[Index(nameof(Archived))]
[Index(nameof(CustodianId))]
public class Account
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid AccountId { get; set; }
    
    [StringLength(256)]
    [Required]
    public required string Name { get; set; }
    
    [StringLength(256)]
    [Required]
    public required string Description { get; set; }
    
    [Required]
    public required DateTime Opened { get; set; } = DateTime.UtcNow;

    [StringLength(64)]
    public string? AccountNumber { get; set; }

    [Required]
    public AccountType AccountType { get; set; }
    
    public DateTime? Closed { get; set; }

    public DateTime? Archived { get; set; }
    
    [StringLength(3)]
    [Required]
    public string CurrencyCode { get; set; } = "USD";

    /// <summary>The contact that holds/custodies this account (the bank for a bank account, the
    /// broker for a brokerage, the provider for a pension). Optional — <c>null</c> when unknown or not
    /// applicable.</summary>
    /// <summary>A real FK to <c>Contact</c> with <c>ON DELETE SET NULL</c>, declared in
    /// <see cref="OdysseyContext"/>. Left as a bare id rather than a navigation so Finance code keeps
    /// resolving custodians through <c>IContactLookup</c>'s batched projection.</summary>
    public Guid? CustodianId { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    public ICollection<AccountFile> AccountFiles { get; set; } = new List<AccountFile>();
    public ICollection<AccountTerm> AccountTerms { get; set; } = new List<AccountTerm>();
    public ICollection<AccountEstimate> AccountEstimates { get; set; } = new List<AccountEstimate>();
    public ICollection<AccountSmartTag> SmartTags { get; set; } = new List<AccountSmartTag>();
}
