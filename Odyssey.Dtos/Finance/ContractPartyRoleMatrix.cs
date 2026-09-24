namespace Odyssey.Dtos.Finance;

/// <summary>
/// How a <see cref="ContractPartyRole"/> stands on a given <see cref="ContractType"/> (issue #157 §4.6).
/// </summary>
public enum ContractPartyRoleLegality
{
    /// <summary>Not legal on this type. A write naming it is refused with a <c>422</c>.</summary>
    Rejected = 0,

    /// <summary>Legal, and offered after the suggested ones.</summary>
    Allowed = 1,

    /// <summary>Legal, and offered first.</summary>
    Suggested = 2,
}

/// <summary>
/// The contract-type × party-role matrix (issue #157 §4.7, widened by issues #169 §4.2 and #187): which roles
/// are legal on which contract type, and which of those are <em>suggested</em>.
/// <b>78 of the 200 cells are legal.</b>
/// </summary>
/// <remarks>
/// <b>This is the single declaration, not a server rule the client re-implements.</b> It lives in
/// <c>Odyssey.Dtos</c>, which holds zero project references and is reachable from both the API and the
/// WASM client, so the write-path validator and the party picker name <em>one</em> symbol — the same
/// precedent <c>SettingItem.Rule</c> and <c>SystemSettingsBounds</c> set. A client-side <em>copy</em>
/// of a server rule is the defect CLAUDE.md forbids; a shared declaration is what avoids it.
///
/// <para>
/// <see cref="ContractPartyRoleLegality.Suggested"/> carries <b>no server-side meaning</b> — the
/// validator only ever asks whether a cell is legal. It exists so the picker's ordering is declared
/// once, beside the legality it has to stay consistent with, rather than being re-derived client-side
/// where the two could drift apart.
/// </para>
///
/// <para>
/// <see cref="ContractPartyRole.Guarantor"/>, <see cref="ContractPartyRole.Broker"/> and
/// <see cref="ContractPartyRole.Other"/> are legal on every type — the universal trio (issue #169 §2
/// goal 2). A party standing behind another's obligation belongs to no particular kind of agreement,
/// which is why <c>Guarantor</c> joined the two that were universal already.
/// </para>
///
/// <para>
/// The suggested counts are <b>4 / 3 / 2 / 1</b>: four for <see cref="ContractType.Insurance"/>,
/// mirroring an insurance policy's four link collections; three for
/// <see cref="ContractType.Rental"/>, <see cref="ContractType.Purchase"/> and
/// <see cref="ContractType.Loan"/>, whose object role is as ordinary as their two counterparties;
/// two for the remaining named types, <see cref="ContractType.Deposit"/> included; and one for <see cref="ContractType.Other"/>, since a contract
/// whose type is "none of the above" has no domain vocabulary to offer, so the only role that can be
/// <em>suggested</em> is the one that says as much. The earlier "exactly two except Insurance and
/// Other" invariant is <b>retired</b> by issue #169 §4.4, not loosened — the count is still pinned,
/// at a new shape.
/// </para>
/// </remarks>
public static class ContractPartyRoleMatrix
{
    private static readonly IReadOnlyList<ContractPartyRole> NoRoles = [];

    /// <summary>
    /// Legal on every contract type, and never suggested on any: a party standing behind another's
    /// obligation, an intermediary, and a deliberate "none of these", none of which belongs to a
    /// particular kind of agreement. Held once so a type added later cannot forget them.
    /// </summary>
    /// <remarks>
    /// <b>A member here must NOT also appear in a column's <c>allowedBeyondUniversal</c> list.</b>
    /// <see cref="Cell"/>'s constructor composes the two, so a role in both yields a duplicated entry
    /// in <see cref="LegalFor"/> — a self-inflicted failure of the distinctness assertion on the
    /// ordinary path, which is why <see cref="ContractPartyRole.Guarantor"/> was deleted from the
    /// Rental, Purchase, Loan and Other columns as it was promoted here (issue #169 §3).
    /// </remarks>
    private static readonly ContractPartyRole[] UniversallyAllowed =
        [ContractPartyRole.Guarantor, ContractPartyRole.Broker, ContractPartyRole.Other];

    private static readonly Dictionary<ContractType, Cell> Cells = new()
    {
        // Employment takes no object role at all: the object of an employment contract is the
        // employee's labour, and Employee already names them (issue #169 §4.3).
        [ContractType.Employment] = new(
            [ContractPartyRole.Employee, ContractPartyRole.Employer],
            []),
        [ContractType.Service] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller],
            [ContractPartyRole.Object]),
        [ContractType.Rental] = new(
            [ContractPartyRole.Landlord, ContractPartyRole.Tenant, ContractPartyRole.Property],
            [ContractPartyRole.Object]),
        // Nor does Insurance: Insured is already documented as "the person, account or thing
        // covered", and a second name for one concept would split where the covered thing is
        // recorded, so a report or filter would have to check both (issue #169 §4.3).
        [ContractType.Insurance] = new(
            [
                ContractPartyRole.Insurer,
                ContractPartyRole.Policyholder,
                ContractPartyRole.Insured,
                ContractPartyRole.Beneficiary,
            ],
            []),
        [ContractType.Subscription] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller],
            [ContractPartyRole.Object]),
        [ContractType.Purchase] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller, ContractPartyRole.Property],
            [ContractPartyRole.Object]),
        [ContractType.Loan] = new(
            [ContractPartyRole.Lender, ContractPartyRole.Borrower, ContractPartyRole.Collateral],
            [ContractPartyRole.Object]),
        // The mirror of Loan (issue #187). Collateral is allowed rather than suggested — a deposit
        // pledged as security is incidental to a deposit, where collateral is central to a loan.
        [ContractType.Deposit] = new(
            [ContractPartyRole.Depositor, ContractPartyRole.Custodian],
            [ContractPartyRole.Object, ContractPartyRole.Collateral]),
        [ContractType.Membership] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller],
            [ContractPartyRole.Object]),
        // The catch-all type takes the catch-all role as its one suggestion and permits every other
        // role outright — a contract filed as "none of the above" may genuinely be any of them.
        [ContractType.Other] = new(
            [ContractPartyRole.Other],
            [
                ContractPartyRole.Employee,
                ContractPartyRole.Employer,
                ContractPartyRole.Buyer,
                ContractPartyRole.Seller,
                ContractPartyRole.Landlord,
                ContractPartyRole.Tenant,
                ContractPartyRole.Insurer,
                ContractPartyRole.Policyholder,
                ContractPartyRole.Insured,
                ContractPartyRole.Beneficiary,
                ContractPartyRole.Lender,
                ContractPartyRole.Borrower,
                ContractPartyRole.Object,
                ContractPartyRole.Property,
                ContractPartyRole.Collateral,
                ContractPartyRole.Depositor,
                ContractPartyRole.Custodian,
            ]),
    };

    /// <summary>
    /// How <paramref name="role"/> stands on <paramref name="type"/>. An out-of-range
    /// <paramref name="type"/> resolves to <see cref="ContractPartyRoleLegality.Rejected"/> for every
    /// role — the matrix never invents a legality for a type it has not been told about.
    /// </summary>
    public static ContractPartyRoleLegality LegalityOf(ContractType type, ContractPartyRole role)
    {
        if (!Cells.TryGetValue(type, out var cell))
        {
            return ContractPartyRoleLegality.Rejected;
        }

        if (cell.Suggested.Contains(role))
        {
            return ContractPartyRoleLegality.Suggested;
        }

        return cell.Allowed.Contains(role)
            ? ContractPartyRoleLegality.Allowed
            : ContractPartyRoleLegality.Rejected;
    }

    /// <summary>Whether <paramref name="role"/> may be written on a contract of <paramref name="type"/>.</summary>
    public static bool IsLegal(ContractType type, ContractPartyRole role) =>
        LegalityOf(type, role) != ContractPartyRoleLegality.Rejected;

    /// <summary>
    /// The roles legal on <paramref name="type"/>, <b>suggested ones first</b> — the picker's order,
    /// and the order the <c>422</c> message lists them in. Empty for an out-of-range type.
    /// </summary>
    public static IReadOnlyList<ContractPartyRole> LegalFor(ContractType type) =>
        Cells.TryGetValue(type, out var cell) ? cell.Legal : NoRoles;

    /// <summary>The suggested roles for <paramref name="type"/>, in declaration order. Never empty for a known type.</summary>
    public static IReadOnlyList<ContractPartyRole> SuggestedFor(ContractType type) =>
        Cells.TryGetValue(type, out var cell) ? cell.Suggested : NoRoles;

    /// <summary>
    /// The roles that are legal on <paramref name="type"/> but <em>not</em> suggested, in declaration
    /// order — the picker's second group.
    /// </summary>
    public static IReadOnlyList<ContractPartyRole> AllowedFor(ContractType type) =>
        Cells.TryGetValue(type, out var cell) ? cell.Allowed : NoRoles;

    /// <summary>The contract types the matrix declares — every member of <see cref="ContractType"/>.</summary>
    public static IReadOnlyCollection<ContractType> DeclaredTypes => Cells.Keys;

    /// <summary>
    /// One column of the matrix. <paramref name="allowedBeyondUniversal"/> is what this type permits
    /// <em>in addition to</em> <see cref="UniversallyAllowed"/>, which is appended here rather than
    /// repeated on all ten columns.
    /// </summary>
    private sealed class Cell
    {
        public Cell(ContractPartyRole[] suggested, ContractPartyRole[] allowedBeyondUniversal)
        {
            Suggested = suggested;
            Allowed = [.. allowedBeyondUniversal, .. UniversallyAllowed.Where(role => !suggested.Contains(role))];
            Legal = [.. suggested, .. Allowed];
        }

        public ContractPartyRole[] Suggested { get; }

        public ContractPartyRole[] Allowed { get; }

        public ContractPartyRole[] Legal { get; }
    }
}
