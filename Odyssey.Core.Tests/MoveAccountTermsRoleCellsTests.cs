using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #190 AC 20 — every party role the <c>MoveAccountTermsToContracts</c> migration writes onto a
/// contract it creates is a legal cell of <see cref="ContractPartyRoleMatrix"/>. The migration writes by
/// SQL and never consults the matrix, so this is the only thing standing between it and a party row
/// the API itself would refuse to edit.
/// </summary>
/// <remarks>
/// The ordinals are the ones the migration's SQL names literally (the account as <c>Object</c> = 17;
/// the custodian as <c>Custodian</c> = 21, <c>Lender</c> = 13 or <c>Other</c> = 6; the contract as
/// <c>Deposit</c> = 9, <c>Loan</c> = 8 or <c>Other</c> = 3), so a renumbered enum fails here too.
/// </remarks>
public class MoveAccountTermsRoleCellsTests
{
    public static TheoryData<int, int> WrittenCells() => new()
    {
        { 9, 17 }, { 8, 17 }, { 3, 17 }, // the account, as Object, on Deposit / Loan / Other
        { 9, 21 },                       // the custodian contact as Custodian on a Deposit
        { 8, 13 },                       // … as Lender on a Loan
        { 3, 6 },                        // … as Other on an Other
    };

    [Theory]
    [MemberData(nameof(WrittenCells))]
    public void Every_cell_the_migration_writes_is_legal(int type, int role)
    {
        var contractType = (ContractType)type;
        var partyRole = (ContractPartyRole)role;

        Assert.True(Enum.IsDefined(contractType));
        Assert.True(Enum.IsDefined(partyRole));
        Assert.Contains(partyRole, ContractPartyRoleMatrix.LegalFor(contractType));
    }
}
