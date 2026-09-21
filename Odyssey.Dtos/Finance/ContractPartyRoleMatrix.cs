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
/// The contract-type × party-role matrix (issue #157 §4.7): which roles are legal on which contract
/// type, and which of those are <em>suggested</em>. <b>52 of the 135 cells are legal.</b>
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
/// <see cref="ContractPartyRole.Broker"/> and <see cref="ContractPartyRole.Other"/> are legal on every
/// type. Every type carries exactly two suggested roles except <see cref="ContractType.Insurance"/>,
/// which carries four — mirroring an insurance policy's four link collections — and
/// <see cref="ContractType.Other"/>, which carries one: a contract whose type is "none of the above"
/// has no domain vocabulary to offer, so the only role that can be <em>suggested</em> is the one that
/// says as much.
/// </para>
/// </remarks>
public static class ContractPartyRoleMatrix
{
    private static readonly IReadOnlyList<ContractPartyRole> NoRoles = [];

    /// <summary>
    /// Legal on every contract type, and never suggested on any: an intermediary and a deliberate
    /// "none of these", neither of which belongs to a particular kind of agreement. Held once so a
    /// type added later cannot forget them.
    /// </summary>
    private static readonly ContractPartyRole[] UniversallyAllowed =
        [ContractPartyRole.Broker, ContractPartyRole.Other];

    private static readonly Dictionary<ContractType, Cell> Cells = new()
    {
        [ContractType.Employment] = new(
            [ContractPartyRole.Employee, ContractPartyRole.Employer],
            []),
        [ContractType.Service] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller],
            []),
        [ContractType.Rental] = new(
            [ContractPartyRole.Landlord, ContractPartyRole.Tenant],
            [ContractPartyRole.Guarantor]),
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
            []),
        [ContractType.Purchase] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller],
            [ContractPartyRole.Guarantor]),
        [ContractType.Loan] = new(
            [ContractPartyRole.Lender, ContractPartyRole.Borrower],
            [ContractPartyRole.Guarantor]),
        [ContractType.Membership] = new(
            [ContractPartyRole.Buyer, ContractPartyRole.Seller],
            []),
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
                ContractPartyRole.Guarantor,
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
    /// repeated on all nine columns.
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
