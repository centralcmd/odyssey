namespace Odyssey.Context;

/// <summary>
/// What a contract party <em>does</em> in the agreement (issue #121 §4, widened by issue #157 §4.2).
/// Orthogonal to <c>ContractPartyKind</c>, which says which of the two nullable target columns
/// is set: nothing constrains one by the other, so an account party may carry any role.
/// </summary>
/// <remarks>
/// Ordinals are a persistence contract — the column is an <c>int</c> — and are aligned member-for-member
/// with <see cref="Odyssey.Dtos.Finance.ContractPartyRole"/>, which is the wire half. Later members
/// append; no member is renumbered or removed.
///
/// <para>
/// <b>Which roles are legal depends on the contract's type</b> — see
/// <c>Odyssey.Dtos.Finance.ContractPartyRoleMatrix</c>, which is the single shared declaration both the server
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
    /// <c>ContractPartyKind</c> discriminator (issue #157 §4.2).
    /// </summary>
    Insured = 11,

    /// <summary>
    /// The party that receives on the agreement. <b>Blocks deletion of the linked contact</b>, with a
    /// 409 and a transactional detach valve as the supported way out (issue #157 §7.4).
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

    /// <summary>
    /// The thing the agreement concerns, in the general case (issue #169 §4.1) — what a contract is
    /// <em>about</em> rather than who stands on a side of it. Not legal on <c>ContractType.Employment</c>
    /// (the object of an employment contract is the employee's labour, and <see cref="Employee"/>
    /// already names them) nor on <c>ContractType.Insurance</c>, where <see cref="Insured"/> already
    /// documents "the person, account or thing covered".
    /// </summary>
    /// <remarks>
    /// Like every other role this is <b>orthogonal to <c>ContractPartyKind</c></b>: an object party is
    /// <em>expected</em> to be an account, but a contact target is equally legal and is not checked.
    /// Do not add that constraint as a "missing" one.
    /// </remarks>
    Object = 17,

    /// <summary>
    /// Real property or goods — the let premises, the purchased asset (issue #169 §4.1). Suggested on
    /// <c>ContractType.Rental</c> and <c>ContractType.Purchase</c>. Orthogonal to
    /// <c>ContractPartyKind</c>, exactly as <see cref="Object"/> is.
    /// </summary>
    Property = 18,

    /// <summary>
    /// Security pledged against a loan — Norwegian <em>pant</em> (issue #169 §4.1). Suggested on
    /// <c>ContractType.Loan</c>. Orthogonal to <c>ContractPartyKind</c>, exactly as
    /// <see cref="Object"/> is.
    /// </summary>
    Collateral = 19,

    /// <summary>
    /// The party placing the money and entitled to have it returned (issue #187). Suggested on
    /// <c>ContractType.Deposit</c>, where it mirrors <see cref="Lender"/> on a loan. A separate member rather than an
    /// alias of <see cref="Lender"/>, so a report or filter never has to guess whether a lender row is
    /// a creditor or a depositor.
    /// </summary>
    Depositor = 20,

    /// <summary>
    /// The party holding the deposited money and owing it back — typically the bank, or a landlord
    /// holding a rental deposit (issue #187). Suggested on <c>ContractType.Deposit</c>, where it mirrors
    /// <see cref="Borrower"/> on a loan.
    /// </summary>
    /// <remarks>
    /// <b>The name overlaps the account-level custodian deliberately</b> —
    /// <c>Odyssey.Dtos.Finance.Custodian</c> (the contact holding an account) and <c>Account.CustodianId</c>. The
    /// real-world concept is the same, but the two surfaces are <b>independent</b>: an account's
    /// custodian is an account attribute, a custodian party is a party on one agreement, and neither is
    /// synchronised with or validated against the other. Do not "unify" them. Unlike
    /// <see cref="Beneficiary"/>, this role does <b>not</b> block deletion of its contact.
    /// </remarks>
    Custodian = 21,
}
