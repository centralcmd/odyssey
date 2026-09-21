using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PartyRoleLabel"/> is the ONE place a contract party's role becomes words (issue #122
/// §6.2): the tile overline, the live-region lines and the tile menu's <c>aria-label</c> all call it,
/// so its fallbacks are a correctness property of the accessible name rather than a
/// presentation detail.
/// </summary>
public class PartyRoleLabelTests
{
    /// <summary>A named role reads as its registry label — the same words the picker offered.</summary>
    [Theory]
    [InlineData(ContractPartyRole.Employee, "Employee")]
    [InlineData(ContractPartyRole.Employer, "Employer")]
    [InlineData(ContractPartyRole.Landlord, "Landlord")]
    [InlineData(ContractPartyRole.Policyholder, "Policyholder")]
    [InlineData(ContractPartyRole.Other, "Other")]
    public void A_named_role_reads_as_its_registry_label(ContractPartyRole role, string expected) =>
        Assert.Equal(expected, PartyRoleLabel.For(role));

    /// <summary>Every live member has a registry entry, so no role falls to the unknown branch.</summary>
    [Fact]
    public void Every_live_member_is_nameable()
    {
        Assert.All(Enum.GetValues<ContractPartyRole>(), role =>
        {
            Assert.NotNull(OdsTypeRegistries.ContractPartyRoleOf(role));
            Assert.NotEqual(PartyRoleLabel.UnknownText, PartyRoleLabel.For(role));
        });
    }

    /// <summary>
    /// An ordinal outside this build's registry is a real version-skew state, and a WIDER one since
    /// issue #157 appended nine members a deployed-but-stale client cannot name. It is NAMED as
    /// unrecognised rather than falling through to the registry's last entry, which would render it
    /// as some arbitrary role the party never held.
    /// </summary>
    [Fact]
    public void An_unknown_ordinal_is_named_as_unrecognised_not_collapsed()
    {
        var unknown = (ContractPartyRole)int.MaxValue;

        Assert.Null(OdsTypeRegistries.ContractPartyRoleOf(unknown));
        Assert.Equal("Unrecognised role", PartyRoleLabel.For(unknown));
        Assert.NotEqual(PartyRoleLabel.For(ContractPartyRole.Other), PartyRoleLabel.For(unknown));
    }

    /// <summary>
    /// The two RETIRED ordinals are unknown to this build, so a row the migration somehow missed
    /// reads as unrecognised rather than as whichever live member happens to sit last in the
    /// registry (issue #157 §4.3).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void A_retired_ordinal_reads_as_unrecognised(int retired)
    {
        Assert.Equal("Unrecognised role", PartyRoleLabel.For((ContractPartyRole)retired));
        Assert.True(PartyRoleLabel.IsUnknown((ContractPartyRole)retired));
    }

    /// <summary>
    /// <c>IsUnknown</c> is what withholds the tile's <b>Edit party</b>, so it must fire for the skew
    /// case alone. Since issue #157 it is the exact complement of <see cref="PartyRoleLabel.IsNamed"/>:
    /// with <c>Unspecified</c> retired there is no third state.
    /// </summary>
    [Fact]
    public void IsUnknown_covers_the_skew_case_alone()
    {
        Assert.True(PartyRoleLabel.IsUnknown((ContractPartyRole)int.MaxValue));
        Assert.All(Enum.GetValues<ContractPartyRole>(), role =>
        {
            Assert.False(PartyRoleLabel.IsUnknown(role));
            Assert.True(PartyRoleLabel.IsNamed(role));
        });
    }

    /// <summary>
    /// <c>IsNamed</c> drives the overline's muted treatment: only a role this build can name reads as
    /// a category. The skew case reads as an absence.
    /// </summary>
    [Fact]
    public void IsNamed_is_true_only_for_a_role_this_build_knows()
    {
        Assert.True(PartyRoleLabel.IsNamed(ContractPartyRole.Buyer));
        Assert.False(PartyRoleLabel.IsNamed((ContractPartyRole)int.MaxValue));
    }
}
