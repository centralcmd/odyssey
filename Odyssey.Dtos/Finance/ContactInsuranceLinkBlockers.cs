namespace Odyssey.Dtos.Finance;

/// <summary>
/// Why a <c>DELETE /api/contacts/{id}</c> is refused: which insurance link kinds name the contact, how
/// many rows of each, and — <b>only for a caller that also holds <c>insurance.read</c></b> — which
/// policies (issue #27 §7 #5).
///
/// <para>
/// The claim conditional is applied in <c>ContactController</c>, not in the domain service:
/// <c>DomainConflictException</c> carries a message and nothing else, and the service has no
/// <c>ClaimsPrincipal</c>. The service keeps its own unconditional guard as defence-in-depth for
/// non-HTTP callers.
/// </para>
/// </summary>
public sealed record ContactInsuranceLinkBlockers
{
    /// <summary>Per-kind link ROW counts. Every surface counts rows, never resolved names (§9).</summary>
    public List<InsuranceLinkKindCount> Kinds { get; set; } = new();

    /// <summary>Total link rows across all three kinds.</summary>
    public int TotalLinks { get; set; }

    /// <summary>How many distinct policies are involved. Safe without <c>insurance.read</c>: a count is
    /// not an identifier.</summary>
    public int PolicyCount { get; set; }

    /// <summary>
    /// The blocking policies — <b>empty unless the caller holds <c>insurance.read</c></b>, in which case
    /// it names every one of them. Never partially populated: an empty list with a non-zero
    /// <see cref="PolicyCount"/> is exactly the "you may not see which" case.
    /// </summary>
    public List<BlockingInsurancePolicy> Policies { get; set; } = new();
}

/// <summary>One kind and the number of link rows of that kind naming the contact.</summary>
public sealed record InsuranceLinkKindCount
{
    public required InsuranceLinkKind Kind { get; set; }

    public required int Count { get; set; }
}

/// <summary>One policy naming the contact, and in which of its collections.</summary>
public sealed record BlockingInsurancePolicy
{
    public required Guid InsurancePolicyId { get; set; }

    public required string Name { get; set; }

    public List<InsuranceLinkKind> Kinds { get; set; } = new();
}
