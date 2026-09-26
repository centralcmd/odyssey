using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractEventType = Odyssey.Context.ContractEventType;
using ContextPropertyEventType = Odyssey.Context.PropertyEventType;
using DtoContractEventType = Odyssey.Dtos.Finance.ContractEventType;
using DtoPropertyEventType = Odyssey.Dtos.Finance.PropertyEventType;

namespace Odyssey.Core.Tests;

/// <summary>
/// Issue #209 AC 6 and AC 21 — the property-event type matrix, and the ordinal ranges that keep the
/// shared <c>Events</c> table's <c>Type</c> column unambiguous.
/// </summary>
/// <remarks>
/// The range tests are the <b>build-time twin</b> of <c>CK_Events_TypeMatchesOwner</c>: a contract
/// member appended at 100, or a property member outside 100–199, fails here instead of surfacing as a
/// <c>CHECK</c> violation (a <c>500</c>) on the first insert in production.
/// </remarks>
public class PropertyEventTypeGuardTests
{
    // ── AC 21 — mirrors and ranges ─────────────────────────────────────────

    [Fact]
    public void ThePropertyEventTypeMirrors_HaveIdenticalNamesAndOrdinals()
    {
        Assert.Equal(
            Enum.GetValues<DtoPropertyEventType>().Select(v => (v.ToString(), (int)v)),
            Enum.GetValues<ContextPropertyEventType>().Select(v => (v.ToString(), (int)v)));
    }

    [Fact]
    public void EveryContractEventType_StaysBelowOneHundred_InBothMirrors()
    {
        Assert.All(Enum.GetValues<ContextContractEventType>(), v => Assert.InRange((int)v, 0, 99));
        Assert.All(Enum.GetValues<DtoContractEventType>(), v => Assert.InRange((int)v, 0, 99));
    }

    [Fact]
    public void EveryPropertyEventType_SitsInOneHundredToOneNinetyNine_InBothMirrors()
    {
        Assert.All(Enum.GetValues<ContextPropertyEventType>(), v => Assert.InRange((int)v, 100, 199));
        Assert.All(Enum.GetValues<DtoPropertyEventType>(), v => Assert.InRange((int)v, 100, 199));
    }

    /// <summary>
    /// The column default the contract branch owns is 8; that it is no property member is what makes a
    /// property row that omits <c>Type</c> fail the range CHECK instead of passing (AC 3a).
    /// </summary>
    [Fact]
    public void TheSharedColumnDefault_IsNoPropertyEventType()
    {
        Assert.False(Enum.IsDefined((ContextPropertyEventType)(int)ContextContractEventType.Other));
        Assert.False(Enum.IsDefined((ContextPropertyEventType)0));
    }

    // ── AC 6 — the matrix ──────────────────────────────────────────────────

    [Fact]
    public void TheMatrix_Declares_ExactlyTheTwoPropertyTypes()
    {
        Assert.Equal(
            Enum.GetValues<PropertyType>().Order(),
            PropertyEventTypeMatrix.DeclaredTypes.Order());
    }

    [Theory]
    [InlineData(PropertyType.RealEstate)]
    [InlineData(PropertyType.Vehicle)]
    public void EachPropertyType_HasSeventeenDistinctLegalCells(PropertyType type)
    {
        var legal = PropertyEventTypeMatrix.LegalFor(type);

        Assert.Equal(17, legal.Count);
        Assert.Equal(legal.Count, legal.Distinct().Count());
    }

    [Fact]
    public void ThirtyFourOfFortyTwoCells_AreLegal()
    {
        var cells =
            from type in Enum.GetValues<PropertyType>()
            from member in Enum.GetValues<DtoPropertyEventType>()
            select PropertyEventTypeMatrix.IsLegal(type, member);

        var all = cells.ToList();
        Assert.Equal(42, all.Count);
        Assert.Equal(34, all.Count(legal => legal));
    }

    [Fact]
    public void TheThirteenUniversalMembers_AreLegalOnBothTypes()
    {
        Assert.Equal(13, PropertyEventTypeMatrix.Universal.Count);
        Assert.All(PropertyEventTypeMatrix.Universal, member =>
        {
            Assert.True(PropertyEventTypeMatrix.IsLegal(PropertyType.RealEstate, member));
            Assert.True(PropertyEventTypeMatrix.IsLegal(PropertyType.Vehicle, member));
        });
    }

    [Theory]
    [InlineData(PropertyType.RealEstate, DtoPropertyEventType.Renovation, DtoPropertyEventType.TaxAssessed,
        DtoPropertyEventType.TenancyStarted, DtoPropertyEventType.TenancyEnded)]
    [InlineData(PropertyType.Vehicle, DtoPropertyEventType.Serviced, DtoPropertyEventType.TyreChange,
        DtoPropertyEventType.PeriodicInspection, DtoPropertyEventType.Registered)]
    public void EachType_HasFourSpecificMembers_IllegalOnTheOther(PropertyType type, params DtoPropertyEventType[] specific)
    {
        var other = type == PropertyType.RealEstate ? PropertyType.Vehicle : PropertyType.RealEstate;

        Assert.Equal(specific, PropertyEventTypeMatrix.SpecificTo(type));
        Assert.All(specific, member => Assert.False(PropertyEventTypeMatrix.IsLegal(other, member)));

        // Type-specific first: the picker's reading order is declared with the legality.
        Assert.Equal(specific, PropertyEventTypeMatrix.LegalFor(type).Take(4));
    }

    [Fact]
    public void TheFourSystemOnlyMembers_AreFlagged_AndNothingElseIs()
    {
        DtoPropertyEventType[] expected =
        [
            DtoPropertyEventType.Archived,
            DtoPropertyEventType.Unarchived,
            DtoPropertyEventType.AcquisitionDateCleared,
            DtoPropertyEventType.DisposalReversed,
        ];

        Assert.Equal(expected, PropertyEventTypeMatrix.SystemOnly);
        Assert.Equal(
            expected,
            Enum.GetValues<DtoPropertyEventType>().Where(PropertyEventTypeMatrix.IsSystemOnly));
    }

    [Fact]
    public void AnUndeclaredPropertyType_IsLegalForNothing()
    {
        Assert.Empty(PropertyEventTypeMatrix.LegalFor((PropertyType)99));
        Assert.False(PropertyEventTypeMatrix.IsLegal((PropertyType)99, DtoPropertyEventType.Other));
    }
}
