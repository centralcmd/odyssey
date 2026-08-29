using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewAccount
{
    [StringLength(256)]
    public required string Description { get; set; }
    [StringLength(256)]
    public required string Name { get; set; }
    [StringLength(64)]
    public string? AccountNumber { get; set; }
    public AccountType AccountType { get; set; }
    public DateTime? Opened { get; set; }
    public DateTime? Closed { get; set; }
    [StringLength(3)]
    public string CurrencyCode { get; set; } = "USD";
    public bool Archived { get; set; }

    /// <summary>Optional link to the contact that holds this account (its custodian). Scalar
    /// only — the request never carries the nested custodian object; the service binds the FK from
    /// this id alone and never reads contact fields from the request body (over-posting guard).
    /// <c>null</c>/omitted clears any existing link.</summary>
    public Guid? CustodianId { get; set; }
}
