namespace Odyssey.Dtos.Finance;

/// <summary>
/// Why a <c>PUT /api/contracts/{id}</c> that changes the contract's <c>Type</c> is refused with a
/// <c>422</c>: the parties already on the contract whose role the <b>incoming</b> type rejects
/// (issue #157 §5.3, §9.2). Nothing is written.
/// </summary>
/// <remarks>
/// Shaped in <c>ContractController</c> rather than thrown from the service, for the same reason the
/// contact-delete <c>409</c> payload is: <c>DomainException</c> carries a message, a code and a
/// <c>string → string[]</c> errors dictionary, which cannot express a list of objects.
/// <c>ContractService</c> keeps an unconditional <c>DomainUnprocessableException</c> for non-HTTP
/// callers.
///
/// <para>
/// Discloses nothing new: these are the same parties the caller just read off the contract they are
/// editing, under the same <c>contracts.update</c> claim.
/// </para>
/// </remarks>
public sealed record ContractTypeChangeBlockers
{
    /// <summary>The type the caller asked for — the one whose column rejects the parties below.</summary>
    public required ContractType RequestedType { get; set; }

    /// <summary>The parties that would be orphaned. Never empty when this payload is present.</summary>
    public List<BlockingContractParty> Parties { get; set; } = new();

    /// <summary>The roles that <em>are</em> legal on <see cref="RequestedType"/>, suggested first —
    /// the vocabulary the caller has to re-role into.</summary>
    public List<ContractPartyRole> LegalRoles { get; set; } = new();
}

/// <summary>One party whose role the incoming contract type rejects.</summary>
public sealed record BlockingContractParty
{
    public required Guid ContractPartyId { get; set; }

    public required ContractPartyRole Role { get; set; }

    /// <summary>
    /// The party's target as the caller already sees it on the contract — a contact, account or
    /// property name.
    /// <see langword="null"/> when the target no longer resolves: the id is what keeps the round trip
    /// honest, the name is the personal data and stays out.
    /// </summary>
    public string? DisplayName { get; set; }
}
