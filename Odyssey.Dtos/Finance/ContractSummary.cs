namespace Odyssey.Dtos.Finance;

/// <summary>Contract counts bucketed by derived status (issue #174 §7).</summary>
public sealed record ContractStatusCounts
{
    public int Active { get; set; }

    public int Upcoming { get; set; }

    public int Expired { get; set; }

    public int Archived { get; set; }
}

/// <summary>A typed count used by the contract summary rollup.</summary>
public sealed record ContractTypeCount
{
    public ContractType Type { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// Summary rollup for the contracts page header (issue #174 §7): totals plus counts by status and by
/// type. Archived contracts are counted in <see cref="CountsByStatus"/> but excluded from the active
/// totals.
/// </summary>
public sealed record ContractSummary
{
    public int TotalContracts { get; set; }

    public required ContractStatusCounts CountsByStatus { get; set; }

    public List<ContractTypeCount> CountsByType { get; set; } = new();
}
