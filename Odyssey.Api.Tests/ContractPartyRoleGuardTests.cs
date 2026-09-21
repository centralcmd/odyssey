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
    /// AC 16, and issue #169 AC 18 — the eighteen live ordinals, pinned by LITERAL. They are a wire
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

        Assert.Equal(1, (int)ContextRole.Employee);
        Assert.Equal(6, (int)ContextRole.Other);
        Assert.Equal(16, (int)ContextRole.Broker);
        Assert.Equal(17, (int)ContextRole.Object);
        Assert.Equal(18, (int)ContextRole.Property);
        Assert.Equal(19, (int)ContextRole.Collateral);
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

    /// <summary><c>Loan</c> appends at 8; no existing type ordinal moved (issue #157 §4.1).</summary>
    [Fact]
    public void LoanContractType_AppendsWithoutRenumbering()
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
    /// Issue #169 AC 8 — the headline count, pinned: <b>69 of the 162 cells are legal</b>, up from 52
    /// of 135. A single-cell change to the matrix is a deliberate act, and this is what makes it
    /// visible in a diff.
    /// </summary>
    /// <remarks>
    /// The same fact is pinned a SECOND time, independently, in
    /// <c>ContractPartyRoleMatrixApiTests.EveryMatrixCell_IsAcceptedOrRefusedExactlyAsDeclared</c>,
    /// which counts the same cells over real HTTP. Nothing links the two, so a widening that updates
    /// one and not the other is green here and red there.
    /// </remarks>
    [Fact]
    public void LegalCellCount_Is69Of162()
    {
        var types = Enum.GetValues<DtoType>();
        var roles = Enum.GetValues<DtoRole>();

        Assert.Equal(9, types.Length);
        Assert.Equal(18, roles.Length);
        Assert.Equal(69, types.Sum(type => roles.Count(role => ContractPartyRoleMatrix.IsLegal(type, role))));
    }

    /// <summary>
    /// Issue #169 AC 8's second half — the PER-TYPE legal totals, so a widening that kept the headline
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
    [InlineData(DtoType.Membership, 6)]
    [InlineData(DtoType.Other, 18)]
    public void PerTypeLegalCounts_MatchTheSpecTable(DtoType type, int legal)
    {
        Assert.Equal(legal, ContractPartyRoleMatrix.LegalFor(type).Count);
        Assert.Equal(legal, Enum.GetValues<DtoRole>().Count(role => ContractPartyRoleMatrix.IsLegal(type, role)));
    }

    /// <summary>
    /// Issue #169 §4.2 — where each new role reaches, pinned cell by cell. <c>Object</c> is the
    /// general case and reaches seven types; <c>Property</c> and <c>Collateral</c> are type-specific
    /// and must not leak outside their columns (AC 4).
    /// </summary>
    [Fact]
    public void TheThreeObjectRoles_ReachExactlyTheDeclaredTypes()
    {
        // Ordinal order, which is what TypesTaking sorts by: Other is 3, so it lands mid-list.
        Assert.Equal(
            [DtoType.Service, DtoType.Rental, DtoType.Other, DtoType.Subscription, DtoType.Purchase,
             DtoType.Membership, DtoType.Loan],
            TypesTaking(DtoRole.Object));

        Assert.Equal(
            [DtoType.Rental, DtoType.Other, DtoType.Purchase],
            TypesTaking(DtoRole.Property));

        Assert.Equal(
            [DtoType.Other, DtoType.Loan],
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
