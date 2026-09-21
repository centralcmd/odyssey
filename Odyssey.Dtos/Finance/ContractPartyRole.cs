namespace Odyssey.Dtos.Finance;

/// <summary>
/// What a contract party <em>does</em> in the agreement (issue #121 §4, widened by issue #157 §4.2).
/// Orthogonal to <see cref="ContractPartyKind"/>, which says which of the two nullable target columns
/// is set: nothing constrains one by the other, so an account party may carry any role.
/// </summary>
/// <remarks>
/// Ordinals are a wire AND persistence contract — they are serialized in request and response bodies and
/// stored as <c>int</c> — and are aligned member-for-member
/// with <c>Odyssey.Context.ContractPartyRole</c>, which is the persisted half. Later members
/// append; no member is renumbered or removed.
///
/// <para>
/// <b>Which roles are legal depends on the contract's type</b> — see
/// <see cref="ContractPartyRoleMatrix"/>, which is the single shared declaration both the server
/// validator and the client picker read. A rejected pair is refused with a <c>422</c>.
/// </para>
///
/// <para>
/// <b>Ordinals 0 (<c>Unspecified</c>) and 5 (<c>ServiceProvider</c>) are RETIRED</b> and are permanent
/// holes. <c>Unspecified</c> was migrated to <see cref="Other"/> and <c>ServiceProvider</c> to
/// <see cref="Seller"/>, whose documented meaning — the party disposing under this agreement — already
/// covers supplying a service. <b>Neither ordinal may ever be reused</b>: a reused value would make an
/// unmigrated row mean something new rather than nothing.
/// </para>
/// </remarks>
public enum ContractPartyRole
{
    /// <summary>The person employed under this agreement.</summary>
    Employee = 1,

    /// <summary>The party that employs.</summary>
    Employer = 2,

    /// <summary>The party acquiring under this agreement.</summary>
    Buyer = 3,

    /// <summary>The party disposing under this agreement — including supplying a service.</summary>
    Seller = 4,

    /// <summary>A deliberate role that is none of the others.</summary>
    Other = 6,

    /// <summary>The party letting the property under this tenancy.</summary>
    Landlord = 7,

    /// <summary>The party occupying under this tenancy.</summary>
    Tenant = 8,

    /// <summary>The party carrying the risk.</summary>
    Insurer = 9,

    /// <summary>The party that holds the policy and owes the premium.</summary>
    Policyholder = 10,

    /// <summary>
    /// The person, account or thing covered. <b>One member for both party kinds</b>: an insurance
    /// policy needs separate insured-account and insured-contact members only because those are
    /// separate tables, whereas <c>ContractParty</c> is one table carrying a
    /// <see cref="ContractPartyKind"/> discriminator (issue #157 §4.2).
    /// </summary>
    Insured = 11,

    /// <summary>
    /// The party that receives on the agreement. <b>Blocks deletion of the linked contact</b> with the
    /// same 409 and the same transactional detach valve as an insurance-policy beneficiary
    /// (issue #157 §7.4).
    /// </summary>
    Beneficiary = 12,

    /// <summary>The party advancing the money.</summary>
    Lender = 13,

    /// <summary>The party that owes the money back.</summary>
    Borrower = 14,

    /// <summary>A party standing behind another's obligation.</summary>
    Guarantor = 15,

    /// <summary>An intermediary that arranged the agreement. Legal on every contract type.</summary>
    Broker = 16,
}
