using Odyssey.Dtos.Finance;
using Xunit;
using ContextRole = Odyssey.Context.ContractPartyRole;
using ContextType = Odyssey.Context.ContractType;
using DtoRole = Odyssey.Dtos.Finance.ContractPartyRole;
using DtoType = Odyssey.Dtos.Finance.ContractType;

namespace Odyssey.Api.Tests;

/// <summary>
/// The build-time half of issue #121 §4 and issue #157 §4: <c>ContractPartyRole</c> and
/// <c>ContractType</c> are each declared twice — once as the persisted entity enum and once as the
/// wire vocabulary — and the two halves must stay aligned member-for-member. Mapster maps between them
/// by ordinal, so a member added to one declaration and not the other is silently wrong at runtime
/// rather than a compile error (issue #157 AC 16).
/// </summary>
public class ContractPartyRoleGuardTests
{
    /// <summary>AC 16 — identical member NAMES and identical ORDINALS, in both directions.</summary>
    [Fact]
    public void BothRoleDeclarations_HaveIdenticalMembersAndOrdinals()
    {
        var context = Enum.GetValues<ContextRole>()
            .ToDictionary(role => role.ToString(), role => (int)role, StringComparer.Ordinal);
        var dto = Enum.GetValues<DtoRole>()
            .ToDictionary(role => role.ToString(), role => (int)role, StringComparer.Ordinal);

        Assert.Equal(context.Keys.Order(StringComparer.Ordinal), dto.Keys.Order(StringComparer.Ordinal));
        Assert.All(context, pair => Assert.Equal(pair.Value, dto[pair.Key]));
    }

    /// <summary>The same for <c>ContractType</c>, which issue #157 appends <c>Loan</c> to.</summary>
    [Fact]
    public void BothTypeDeclarations_HaveIdenticalMembersAndOrdinals()
    {
        var context = Enum.GetValues<ContextType>()
            .ToDictionary(type => type.ToString(), type => (int)type, StringComparer.Ordinal);
        var dto = Enum.GetValues<DtoType>()
            .ToDictionary(type => type.ToString(), type => (int)type, StringComparer.Ordinal);

        Assert.Equal(context.Keys.Order(StringComparer.Ordinal), dto.Keys.Order(StringComparer.Ordinal));
        Assert.All(context, pair => Assert.Equal(pair.Value, dto[pair.Key]));
    }

    /// <summary>
    /// AC 16, issue #169 AC 18 and issue #187 AC 1 — the twenty live ordinals, pinned by LITERAL. They are a wire
    /// <em>and</em> persistence contract — stored as <c>int</c> and serialized as <c>int</c> — so a
    /// later member appends and none is renumbered. Pinning both declarations against literals rather
    /// than against each other is what makes a symmetric "tidy-up" renumbering fail the build.
    /// </summary>
    [Fact]
    public void LiveOrdinals_ArePinned()
    {
        Assert.Equal(1, (int)DtoRole.Employee);
        Assert.Equal(2, (int)DtoRole.Employer);
        Assert.Equal(3, (int)DtoRole.Buyer);
        Assert.Equal(4, (int)DtoRole.Seller);
        Assert.Equal(6, (int)DtoRole.Other);
        Assert.Equal(7, (int)DtoRole.Landlord);
        Assert.Equal(8, (int)DtoRole.Tenant);
        Assert.Equal(9, (int)DtoRole.Insurer);
        Assert.Equal(10, (int)DtoRole.Policyholder);
        Assert.Equal(11, (int)DtoRole.Insured);
        Assert.Equal(12, (int)DtoRole.Beneficiary);
        Assert.Equal(13, (int)DtoRole.Lender);
        Assert.Equal(14, (int)DtoRole.Borrower);
        Assert.Equal(15, (int)DtoRole.Guarantor);
        Assert.Equal(16, (int)DtoRole.Broker);
        Assert.Equal(17, (int)DtoRole.Object);
        Assert.Equal(18, (int)DtoRole.Property);
        Assert.Equal(19, (int)DtoRole.Collateral);
        Assert.Equal(20, (int)DtoRole.Depositor);
        Assert.Equal(21, (int)DtoRole.Custodian);

        Assert.Equal(1, (int)ContextRole.Employee);
        Assert.Equal(6, (int)ContextRole.Other);
        Assert.Equal(16, (int)ContextRole.Broker);
        Assert.Equal(17, (int)ContextRole.Object);
        Assert.Equal(18, (int)ContextRole.Property);
        Assert.Equal(19, (int)ContextRole.Collateral);
        Assert.Equal(20, (int)ContextRole.Depositor);
        Assert.Equal(21, (int)ContextRole.Custodian);

        Assert.Equal(20, Enum.GetValues<DtoRole>().Length);
        Assert.Equal(20, Enum.GetValues<ContextRole>().Length);
    }

    /// <summary>
    /// AC 16 — ordinals <c>0</c> and <c>5</c> are PERMANENT HOLES. Reusing one would make an
    /// unmigrated row mean something new rather than nothing, which is strictly worse than a gap: a
    /// gap costs no storage and no correctness, while a reused value silently reinterprets history
    /// and every in-flight request carrying the old ordinal (issue #157 §4.3).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void RetiredOrdinals_AreNeverReused(int retired)
    {
        Assert.DoesNotContain(retired, Enum.GetValues<DtoRole>().Select(role => (int)role));
        Assert.DoesNotContain(retired, Enum.GetValues<ContextRole>().Select(role => (int)role));
    }

    /// <summary>
    /// The two retired MEMBERS are gone by name as well as by ordinal, so a re-addition that keeps the old
    /// name at a new number also fails.
    /// </summary>
    [Theory]
    [InlineData("Unspecified")]
    [InlineData("ServiceProvider")]
    public void RetiredMembers_AreGoneByName(string retired)
    {
        Assert.DoesNotContain(retired, Enum.GetNames<DtoRole>());
        Assert.DoesNotContain(retired, Enum.GetNames<ContextRole>());
    }

    /// <summary>
    /// <c>Loan</c> appends at 8 and <c>Deposit</c> at 9; no existing type ordinal moved (issue #157
    /// §4.1, issue #187 AC 1).
    /// </summary>
    [Fact]
    public void AppendedContractTypes_AppendWithoutRenumbering()
    {
        Assert.Equal(0, (int)DtoType.Employment);
        Assert.Equal(1, (int)DtoType.Service);
        Assert.Equal(2, (int)DtoType.Rental);
        Assert.Equal(3, (int)DtoType.Other);
        Assert.Equal(4, (int)DtoType.Insurance);
        Assert.Equal(5, (int)DtoType.Subscription);
        Assert.Equal(6, (int)DtoType.Purchase);
        Assert.Equal(7, (int)DtoType.Membership);
        Assert.Equal(8, (int)DtoType.Loan);
        Assert.Equal(9, (int)DtoType.Deposit);

        Assert.Equal(9, (int)ContextType.Deposit);
    }

    // ── The matrix itself (issue #157 §4.7) ──────────────────────────────────

    /// <summary>
    /// AC 17 — every contract type carries AT LEAST ONE suggested role, so a type added later cannot
    /// ship with an empty picker group. This is what forces <c>Other</c>-the-type to suggest
    /// <c>Other</c>-the-role rather than leaving that column's suggested group blank.
    /// </summary>
    [Fact]
    public void EveryContractType_HasAtLeastOneSuggestedRole()
    {
        Assert.All(Enum.GetValues<DtoType>(), type =>
            Assert.NotEmpty(ContractPartyRoleMatrix.SuggestedFor(type)));
    }

    /// <summary>
    /// AC 18, widened by issue #169 AC 6 — <c>Other</c>, <c>Broker</c> and now <c>Guarantor</c> are
    /// legal on EVERY type. All three are deliberately universal: a deliberate none-of-these, an
    /// intermediary and a party standing behind another's obligation belong to no particular kind of
    /// agreement, and a type that rejected them would leave a real party unrecordable.
    /// </summary>
    [Theory]
    [InlineData(DtoRole.Other)]
    [InlineData(DtoRole.Broker)]
    [InlineData(DtoRole.Guarantor)]
    public void UniversalRoles_AreLegalOnEveryType(DtoRole role)
    {
        Assert.All(Enum.GetValues<DtoType>(), type =>
            Assert.True(ContractPartyRoleMatrix.IsLegal(type, role), $"{role} must be legal on {type}."));
    }

    /// <summary>
    /// Issue #169 AC 7 — the universal trio is <c>Allowed</c> on every type and <c>Suggested</c> on
    /// none. Suggesting a universal role anywhere would push a type's own vocabulary down the picker
    /// in favour of one that belongs to no type in particular.
    /// </summary>
    [Theory]
    [InlineData(DtoRole.Other)]
    [InlineData(DtoRole.Broker)]
    [InlineData(DtoRole.Guarantor)]
    public void UniversalRoles_AreAllowedButNeverSuggested(DtoRole role)
    {
        Assert.All(
            // Other-the-type SUGGESTS Other-the-role, which is the one deliberate exception: it is
            // that column's only domain vocabulary.
            Enum.GetValues<DtoType>().Where(type => !(type is DtoType.Other && role is DtoRole.Other)),
            type => Assert.Equal(
                ContractPartyRoleLegality.Allowed, ContractPartyRoleMatrix.LegalityOf(type, role)));
    }

    /// <summary>
    /// The matrix declares a column for every <c>ContractType</c> member — so a type added later
    /// cannot silently reject every role by being absent from the declaration.
    /// </summary>
    [Fact]
    public void EveryContractType_HasAMatrixColumn()
    {
        Assert.Equal(
            Enum.GetValues<DtoType>().Order().ToList(),
            ContractPartyRoleMatrix.DeclaredTypes.Order().ToList());
    }

    /// <summary>
    /// Issue #187 AC 9 — the headline count, pinned: <b>78 of the 200 cells are legal</b>, up from 69
    /// of 162 (issue #169) and 52 of 135 before that. A single-cell change to the matrix is a deliberate act, and this is what makes it
    /// visible in a diff.
    /// </summary>
    /// <remarks>
    /// The same fact is pinned a SECOND time, independently, in
    /// <c>ContractPartyRoleMatrixApiTests.EveryMatrixCell_IsAcceptedOrRefusedExactlyAsDeclared</c>,
    /// which counts the same cells over real HTTP. Nothing links the two, so a widening that updates
    /// one and not the other is green here and red there.
    /// </remarks>
    [Fact]
    public void LegalCellCount_Is78Of200()
    {
        var types = Enum.GetValues<DtoType>();
        var roles = Enum.GetValues<DtoRole>();

        Assert.Equal(10, types.Length);
        Assert.Equal(20, roles.Length);
        Assert.Equal(78, types.Sum(type => roles.Count(role => ContractPartyRoleMatrix.IsLegal(type, role))));
    }

    /// <summary>
    /// Issue #169 AC 8's second half, re-pinned by issue #187 AC 10 — the PER-TYPE legal totals, so a widening that kept the headline
    /// count right by moving a cell between two columns still fails.
    /// </summary>
    [Theory]
    [InlineData(DtoType.Employment, 5)]
    [InlineData(DtoType.Service, 6)]
    [InlineData(DtoType.Rental, 7)]
    [InlineData(DtoType.Insurance, 7)]
    [InlineData(DtoType.Subscription, 6)]
    [InlineData(DtoType.Purchase, 7)]
    [InlineData(DtoType.Loan, 7)]
    [InlineData(DtoType.Deposit, 7)]
    [InlineData(DtoType.Membership, 6)]
    [InlineData(DtoType.Other, 20)]
    public void PerTypeLegalCounts_MatchTheSpecTable(DtoType type, int legal)
    {
        Assert.Equal(legal, ContractPartyRoleMatrix.LegalFor(type).Count);
        Assert.Equal(legal, Enum.GetValues<DtoRole>().Count(role => ContractPartyRoleMatrix.IsLegal(type, role)));
    }

    /// <summary>
    /// Issue #169 §4.2 — where each new role reaches, pinned cell by cell. <c>Object</c> is the
    /// general case and reaches eight types; <c>Property</c> and <c>Collateral</c> are type-specific
    /// and must not leak outside their columns (AC 4). Issue #187 extends <c>Object</c> and
    /// <c>Collateral</c> to <c>Deposit</c>.
    /// </summary>
    [Fact]
    public void TheThreeObjectRoles_ReachExactlyTheDeclaredTypes()
    {
        // Ordinal order, which is what TypesTaking sorts by: Other is 3, so it lands mid-list.
        Assert.Equal(
            [DtoType.Service, DtoType.Rental, DtoType.Other, DtoType.Subscription, DtoType.Purchase,
             DtoType.Membership, DtoType.Loan, DtoType.Deposit],
            TypesTaking(DtoRole.Object));

        Assert.Equal(
            [DtoType.Rental, DtoType.Other, DtoType.Purchase],
            TypesTaking(DtoRole.Property));

        Assert.Equal(
            [DtoType.Other, DtoType.Loan, DtoType.Deposit],
            TypesTaking(DtoRole.Collateral));
    }

    /// <summary>
    /// Issue #169 §4.3 — the two exclusions, stated so they are not "fixed" later. An employment
    /// contract's object is the employee's labour, and an insurance contract's is already named by
    /// <c>Insured</c>; a second name for one concept would split where the covered thing is recorded.
    /// </summary>
    [Theory]
    [InlineData(DtoType.Employment)]
    [InlineData(DtoType.Insurance)]
    public void Object_IsNotLegalOnEmploymentOrInsurance(DtoType type) =>
        Assert.False(ContractPartyRoleMatrix.IsLegal(type, DtoRole.Object));

    private static List<DtoType> TypesTaking(DtoRole role) =>
        [.. Enum.GetValues<DtoType>().Where(type => ContractPartyRoleMatrix.IsLegal(type, role)).Order()];

    /// <summary>
    /// The three readers agree by construction: <c>LegalFor</c> is exactly suggested-then-allowed,
    /// with no duplicates and no member in both groups. A picker built from the groups and a
    /// validator built from <c>IsLegal</c> would otherwise be able to disagree.
    /// </summary>
    [Fact]
    public void LegalFor_IsSuggestedThenAllowed_WithNoOverlap()
    {
        Assert.All(Enum.GetValues<DtoType>(), type =>
        {
            var suggested = ContractPartyRoleMatrix.SuggestedFor(type);
            var allowed = ContractPartyRoleMatrix.AllowedFor(type);

            Assert.Empty(suggested.Intersect(allowed));
            Assert.Equal([.. suggested, .. allowed], ContractPartyRoleMatrix.LegalFor(type));
            Assert.Equal(
                ContractPartyRoleMatrix.LegalFor(type).Count,
                ContractPartyRoleMatrix.LegalFor(type).Distinct().Count());
            Assert.All(ContractPartyRoleMatrix.LegalFor(type), role =>
                Assert.True(ContractPartyRoleMatrix.IsLegal(type, role)));
        });
    }

    /// <summary>
    /// Issue #169 AC 10 — the suggested counts, re-pinned to the 4 / 3 / 2 / 1 shape. Insurance carries
    /// FOUR, mirroring an insurance policy's four link collections; Rental, Purchase and Loan carry
    /// THREE, their object role being as ordinary as their two counterparties; <c>Other</c>-the-type
    /// carries exactly one; the rest carry two.
    /// </summary>
    /// <remarks>
    /// This REPLACES the retired "exactly two except Insurance and Other" invariant (issue #169 §4.4).
    /// The count is re-pinned at a new shape rather than loosened: a rule that merely asserted "at
    /// least one" would let a column quietly grow a fourth suggestion and bury its own vocabulary.
    /// </remarks>
    [Theory]
    [InlineData(DtoType.Insurance, 4)]
    [InlineData(DtoType.Rental, 3)]
    [InlineData(DtoType.Purchase, 3)]
    [InlineData(DtoType.Loan, 3)]
    [InlineData(DtoType.Employment, 2)]
    [InlineData(DtoType.Deposit, 2)]
    [InlineData(DtoType.Service, 2)]
    [InlineData(DtoType.Subscription, 2)]
    [InlineData(DtoType.Membership, 2)]
    [InlineData(DtoType.Other, 1)]
    public void SuggestedCounts_MatchTheSpecTable(DtoType type, int suggested) =>
        Assert.Equal(suggested, ContractPartyRoleMatrix.SuggestedFor(type).Count);

    /// <summary>
    /// AC 19's matrix half — a <c>Loan</c> accepts <c>Lender</c>/<c>Borrower</c> and rejects
    /// <c>Buyer</c>/<c>Seller</c>, which is the whole point of the new type: before it, a mortgage was
    /// filed as a <c>Purchase</c> with a buyer and a seller.
    /// </summary>
    [Fact]
    public void LoanColumn_TakesLenderAndBorrower_NotBuyerAndSeller()
    {
        Assert.Equal(
            [DtoRole.Lender, DtoRole.Borrower, DtoRole.Collateral],
            ContractPartyRoleMatrix.SuggestedFor(DtoType.Loan));

        Assert.False(ContractPartyRoleMatrix.IsLegal(DtoType.Loan, DtoRole.Buyer));
        Assert.False(ContractPartyRoleMatrix.IsLegal(DtoType.Loan, DtoRole.Seller));
        Assert.True(ContractPartyRoleMatrix.IsLegal(DtoType.Loan, DtoRole.Guarantor));
    }

    // ── The Deposit column (issue #187) ──────────────────────────────────────

    /// <summary>
    /// Issue #187 AC 3 and AC 4 — a <c>Deposit</c> suggests its two counterparties, in that order, and
    /// its legal roles read suggested-then-allowed with the universal trio last.
    /// </summary>
    [Fact]
    public void DepositColumn_SuggestsDepositorAndCustodian_InTheDeclaredOrder()
    {
        Assert.Equal(
            [DtoRole.Depositor, DtoRole.Custodian],
            ContractPartyRoleMatrix.SuggestedFor(DtoType.Deposit));

        Assert.Equal(
            [DtoRole.Depositor, DtoRole.Custodian, DtoRole.Object, DtoRole.Collateral, DtoRole.Guarantor,
             DtoRole.Broker, DtoRole.Other],
            ContractPartyRoleMatrix.LegalFor(DtoType.Deposit));
    }

    /// <summary>
    /// Issue #187 AC 5 — the deliberate asymmetry: collateral is central to a loan and incidental to
    /// a deposit, so it is suggested on one and merely allowed on the other.
    /// </summary>
    [Fact]
    public void Collateral_IsSuggestedOnLoan_ButOnlyAllowedOnDeposit()
    {
        Assert.Equal(ContractPartyRoleLegality.Allowed,
            ContractPartyRoleMatrix.LegalityOf(DtoType.Deposit, DtoRole.Collateral));
        Assert.Equal(ContractPartyRoleLegality.Suggested,
            ContractPartyRoleMatrix.LegalityOf(DtoType.Loan, DtoRole.Collateral));
    }

    /// <summary>
    /// Issue #187 AC 6 — the mirror pair does not cross over. Depositor/Custodian are separate members
    /// rather than aliases of Lender/Borrower precisely so a report never has to guess which one a row
    /// means; letting either type take the other's roles would reintroduce that guess.
    /// </summary>
    [Theory]
    [InlineData(DtoType.Deposit, DtoRole.Lender)]
    [InlineData(DtoType.Deposit, DtoRole.Borrower)]
    [InlineData(DtoType.Loan, DtoRole.Depositor)]
    [InlineData(DtoType.Loan, DtoRole.Custodian)]
    public void DepositAndLoan_DoNotTakeEachOthersCounterparties(DtoType type, DtoRole role) =>
        Assert.Equal(ContractPartyRoleLegality.Rejected, ContractPartyRoleMatrix.LegalityOf(type, role));

    /// <summary>
    /// Issue #187 AC 7 — the two new roles reach exactly two columns: suggested on <c>Deposit</c>,
    /// allowed on <c>Other</c>, rejected everywhere else.
    /// </summary>
    [Theory]
    [InlineData(DtoRole.Depositor)]
    [InlineData(DtoRole.Custodian)]
    public void DepositRoles_ReachOnlyDepositAndOther(DtoRole role)
    {
        Assert.All(Enum.GetValues<DtoType>(), type =>
        {
            var expected = type switch
            {
                DtoType.Deposit => ContractPartyRoleLegality.Suggested,
                DtoType.Other => ContractPartyRoleLegality.Allowed,
                _ => ContractPartyRoleLegality.Rejected,
            };

            Assert.Equal(expected, ContractPartyRoleMatrix.LegalityOf(type, role));
        });
    }

    /// <summary>
    /// Issue #187 AC 8 — <c>Other</c>-the-type stays legal for EVERY live role. A contract filed as
    /// "none of the above" may genuinely be any of them, so a role appended later without widening
    /// that column fails here rather than leaving it unrecordable there.
    /// </summary>
    [Fact]
    public void OtherType_PermitsEveryLiveRole()
    {
        Assert.Equal(Enum.GetValues<DtoRole>().Length, ContractPartyRoleMatrix.LegalFor(DtoType.Other).Count);
        Assert.All(Enum.GetValues<DtoRole>(), role =>
            Assert.True(ContractPartyRoleMatrix.IsLegal(DtoType.Other, role), $"Other must permit {role}."));
    }

    /// <summary>
    /// A type the matrix does not declare rejects everything rather than silently permitting it. Not
    /// reachable through the API (<c>[EnumDataType]</c> refuses an unknown ordinal first), but it is
    /// what keeps a direct caller from writing an arbitrary role against a cast integer.
    /// </summary>
    [Fact]
    public void UndeclaredContractType_RejectsEveryRole()
    {
        var undeclared = (DtoType)999;

        Assert.Empty(ContractPartyRoleMatrix.LegalFor(undeclared));
        Assert.All(Enum.GetValues<DtoRole>(), role =>
            Assert.False(ContractPartyRoleMatrix.IsLegal(undeclared, role)));
    }

    /// <summary>
    /// Issue #169 AC 9's declaration half — a Rental's legal roles read
    /// <c>Landlord, Tenant, Property, Object, Guarantor, Broker, Other</c>, in that order. This is the
    /// order the picker offers and the order the <c>422</c> message lists, and it is what makes the
    /// universal trio's new leading member visible rather than incidental.
    /// </summary>
    [Fact]
    public void RentalLegalRoles_ReadSuggestedThenAllowed_InTheDeclaredOrder() =>
        Assert.Equal(
            [DtoRole.Landlord, DtoRole.Tenant, DtoRole.Property, DtoRole.Object, DtoRole.Guarantor,
             DtoRole.Broker, DtoRole.Other],
            ContractPartyRoleMatrix.LegalFor(DtoType.Rental));

    /// <summary>
    /// The three-state legality reading, which the picker's grouping depends on: a suggested cell
    /// reports <c>Suggested</c> rather than merely <c>Allowed</c>, and a rejected one is distinct
    /// from both.
    /// </summary>
    [Fact]
    public void LegalityOf_DistinguishesSuggestedFromAllowedFromRejected()
    {
        Assert.Equal(ContractPartyRoleLegality.Suggested,
            ContractPartyRoleMatrix.LegalityOf(DtoType.Rental, DtoRole.Landlord));
        Assert.Equal(ContractPartyRoleLegality.Allowed,
            ContractPartyRoleMatrix.LegalityOf(DtoType.Rental, DtoRole.Guarantor));
        Assert.Equal(ContractPartyRoleLegality.Rejected,
            ContractPartyRoleMatrix.LegalityOf(DtoType.Rental, DtoRole.Employee));
    }
}
