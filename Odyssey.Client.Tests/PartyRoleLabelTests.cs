using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="PartyRoleLabel"/> is the ONE place a contract party's role becomes words (issue #122
/// §6.2): the tile overline, the live-region lines and the tile menu's <c>aria-label</c> all call it,
/// so its three fallbacks are a correctness property of the accessible name rather than a
/// presentation detail.
/// </summary>
public class PartyRoleLabelTests
{
    /// <summary>A named role reads as its registry label — the same words the picker offered.</summary>
    [Theory]
    [InlineData(ContractPartyRole.Employee, "Employee")]
    [InlineData(ContractPartyRole.Employer, "Employer")]
    [InlineData(ContractPartyRole.ServiceProvider, "Service provider")]
    [InlineData(ContractPartyRole.Other, "Other")]
    public void A_named_role_reads_as_its_registry_label(ContractPartyRole role, string expected) =>
        Assert.Equal(expected, PartyRoleLabel.For(role));

    /// <summary>
    /// <c>Unspecified</c> is rendered as a stated ABSENCE. The literal sentinel is never shown, and it
    /// is never rendered as the deliberate <c>Other</c> — "nobody has said" and "somebody looked and
    /// none of these fit" are different claims (issue #121 §4).
    /// </summary>
    [Fact]
    public void Unspecified_reads_as_a_stated_absence_and_never_as_Other()
    {
        var text = PartyRoleLabel.For(ContractPartyRole.Unspecified);

        Assert.Equal("No role set", text);
        Assert.DoesNotContain("Unspecified", text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(PartyRoleLabel.For(ContractPartyRole.Other), text);
    }

    /// <summary>
    /// An ordinal outside this build's registry is a real version-skew state: issue #121 lets members
    /// be appended and fixes only the initial seven. It is NAMED as unrecognised rather than collapsed
    /// into <c>Unspecified</c> ("no role stated", a different and wrong claim) or falling through to
    /// the registry's last entry, which would render it as the deliberate <c>Other</c>.
    /// </summary>
    [Fact]
    public void An_unknown_ordinal_is_named_as_unrecognised_not_collapsed()
    {
        var unknown = (ContractPartyRole)int.MaxValue;

        Assert.Null(OdsTypeRegistries.ContractPartyRoleOf(unknown));
        Assert.Equal("Unrecognised role", PartyRoleLabel.For(unknown));
        Assert.NotEqual(PartyRoleLabel.For(ContractPartyRole.Other), PartyRoleLabel.For(unknown));
        Assert.NotEqual(PartyRoleLabel.For(ContractPartyRole.Unspecified), PartyRoleLabel.For(unknown));
    }

    /// <summary>
    /// <c>IsUnknown</c> is what withholds the tile's <b>Edit party</b>, so it must fire for the skew
    /// case ALONE. <c>Unspecified</c> is a role the picker holds and can round-trip perfectly; treating
    /// it as unknown would take the edit affordance away from every row the migration backfilled.
    /// </summary>
    [Fact]
    public void IsUnknown_covers_the_skew_case_alone()
    {
        Assert.True(PartyRoleLabel.IsUnknown((ContractPartyRole)int.MaxValue));
        Assert.False(PartyRoleLabel.IsUnknown(ContractPartyRole.Unspecified));
        Assert.All(Enum.GetValues<ContractPartyRole>(), role => Assert.False(PartyRoleLabel.IsUnknown(role)));
    }

    /// <summary>
    /// <c>IsNamed</c> drives the overline's muted treatment: only a stated role this build can name
    /// reads as a category. Both fallback conditions read as an absence.
    /// </summary>
    [Fact]
    public void IsNamed_is_true_only_for_a_stated_role_this_build_knows()
    {
        Assert.True(PartyRoleLabel.IsNamed(ContractPartyRole.Buyer));
        Assert.False(PartyRoleLabel.IsNamed(ContractPartyRole.Unspecified));
        Assert.False(PartyRoleLabel.IsNamed((ContractPartyRole)int.MaxValue));
    }
}
