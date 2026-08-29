using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateTaxStatementStatus
{
    [EnumDataType(typeof(TaxStatementStatus))]
    public required TaxStatementStatus Status { get; set; }

    [StringLength(256)]
    public string? StatusComment { get; set; }
}
