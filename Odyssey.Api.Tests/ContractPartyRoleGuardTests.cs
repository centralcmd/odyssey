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
    /// AC 16 — the fifteen live ordinals, pinned by LITERAL. They are a wire <em>and</em> persistence
    /// contract — stored as <c>int</c> and serialized as <c>int</c> — so a later member appends and
    /// none is renumbered. Pinning both declarations against literals rather than against each other
    /// is what makes a symmetric "tidy-up" renumbering fail the build.
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

        Assert.Equal(1, (int)ContextRole.Employee);
        Assert.Equal(6, (int)ContextRole.Other);
        Assert.Equal(16, (int)ContextRole.Broker);
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
    /// AC 18 — <c>Other</c> and <c>Broker</c> are legal on EVERY type. Both are deliberately
    /// universal: a deliberate none-of-these and an intermediary belong to no particular kind of
    /// agreement, and a type that rejected them would leave a real party unrecordable.
    /// </summary>
    [Theory]
    [InlineData(DtoRole.Other)]
    [InlineData(DtoRole.Broker)]
    public void UniversalRoles_AreLegalOnEveryType(DtoRole role)
    {
        Assert.All(Enum.GetValues<DtoType>(), type =>
            Assert.True(ContractPartyRoleMatrix.IsLegal(type, role), $"{role} must be legal on {type}."));
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
    /// §4.7's headline count, pinned: <b>52 of the 135 cells are legal</b>. A single-cell change to
    /// the matrix is a deliberate act, and this is what makes it visible in a diff.
    /// </summary>
    [Fact]
    public void LegalCellCount_Is52Of135()
    {
        var types = Enum.GetValues<DtoType>();
        var roles = Enum.GetValues<DtoRole>();

        Assert.Equal(9, types.Length);
        Assert.Equal(15, roles.Length);
        Assert.Equal(52, types.Sum(type => roles.Count(role => ContractPartyRoleMatrix.IsLegal(type, role))));
    }

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
    /// Insurance carries FOUR suggested roles, mirroring an insurance policy's four link collections;
    /// <c>Other</c>-the-type carries exactly one, and every other type exactly two (§4.7).
    /// </summary>
    [Fact]
    public void SuggestedCounts_MatchTheSpecTable()
    {
        Assert.Equal(4, ContractPartyRoleMatrix.SuggestedFor(DtoType.Insurance).Count);
        Assert.Single(ContractPartyRoleMatrix.SuggestedFor(DtoType.Other));

        Assert.All(
            Enum.GetValues<DtoType>().Where(type => type is not (DtoType.Insurance or DtoType.Other)),
            type => Assert.Equal(2, ContractPartyRoleMatrix.SuggestedFor(type).Count));
    }

    /// <summary>
    /// AC 19's matrix half — a <c>Loan</c> accepts <c>Lender</c>/<c>Borrower</c> and rejects
    /// <c>Buyer</c>/<c>Seller</c>, which is the whole point of the new type: before it, a mortgage was
    /// filed as a <c>Purchase</c> with a buyer and a seller.
    /// </summary>
    [Fact]
    public void LoanColumn_TakesLenderAndBorrower_NotBuyerAndSeller()
    {
        Assert.Equal(
            [DtoRole.Lender, DtoRole.Borrower],
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
