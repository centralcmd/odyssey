using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The pure derivations behind the insurance link collections' display (issue #27) — what the
/// collapsed row's insurer segment says, which glyph it wears, and how an unnamed member reads.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="RecordCardDerivationTests"/>, whose doc comment states the
/// convention: these decide what the surface actually says, and their only other check would be
/// reading the rendered page. Two rules here are load-bearing rather than cosmetic — an unnamed
/// member must never render a name or a GUID, and a mixed-type collection must not wear one type's
/// glyph as if it spoke for all of them.
/// </remarks>
public class InsuranceLinkDerivationTests
{
    private static PolicyContactReference Available(string name, ContactType type = ContactType.Person) =>
        new() { ContactId = Guid.NewGuid(), Name = name, Type = type, Availability = LinkAvailability.Available };

    private static PolicyContactReference Archived(ContactType type = ContactType.Person) =>
        new() { ContactId = Guid.NewGuid(), Name = null, Type = type, Availability = LinkAvailability.Archived };

    private static PolicyContactReference Unresolvable() =>
        new() { ContactId = Guid.NewGuid(), Name = null, Type = null, Availability = LinkAvailability.Unresolvable };

    // ── The meta line's insurer segment ─────────────────────────────────────────

    [Fact]
    public void InsurerNames_NamesUpToTwo_ThenCounts()
    {
        Assert.Equal("Acme", InsuranceCard.InsurerNames([Available("Acme", ContactType.Organization)]));
        Assert.Equal("Acme, Beta", InsuranceCard.InsurerNames(
            [Available("Acme", ContactType.Organization), Available("Beta", ContactType.Organization)]));
        Assert.Equal("Acme, Beta +2", InsuranceCard.InsurerNames(
        [
            Available("Acme", ContactType.Organization),
            Available("Beta", ContactType.Organization),
            Available("Gamma", ContactType.Organization),
            Available("Delta", ContactType.Organization),
        ]));
    }

    /// <summary>
    /// The rule the whole read path exists to protect: an unnamed member reads as its STATE. Never
    /// the archived contact's name — the read model does not carry one — and never a raw GUID.
    /// </summary>
    [Fact]
    public void InsurerNames_RendersAnUnnamedMemberAsItsState()
    {
        var archived = Archived(ContactType.Organization);
        var unresolvable = Unresolvable();

        var segment = InsuranceCard.InsurerNames([archived, unresolvable]);

        Assert.Equal("Archived, Unavailable", segment);
        Assert.DoesNotContain(archived.ContactId.ToString(), segment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unresolvable.ContactId.ToString(), segment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsurerGlyph_UsesTheSharedTypesGlyph_ButFallsBackToGenericWhenMixed()
    {
        var person = InsuranceCard.InsurerGlyph([Available("A"), Available("B")]);
        var organization = InsuranceCard.InsurerGlyph(
            [Available("A", ContactType.Organization), Available("B", ContactType.Organization)]);

        Assert.NotEqual("link", person);
        Assert.NotEqual("link", organization);
        Assert.NotEqual(person, organization);

        // Mixed types: no single type glyph can speak for the collection, so the generic one does.
        Assert.Equal("link", InsuranceCard.InsurerGlyph(
            [Available("A"), Available("B", ContactType.Organization)]));
    }

    /// <summary>An unresolvable link has no type at all, so it cannot contribute one to the glyph
    /// decision — a lone unresolvable member must not crash or invent a type.</summary>
    [Fact]
    public void InsurerGlyph_TreatsATypelessMemberAsContributingNoType()
    {
        Assert.Equal("link", InsuranceCard.InsurerGlyph([Unresolvable()]));

        // One real type plus a typeless member still resolves to that one type.
        Assert.NotEqual("link", InsuranceCard.InsurerGlyph([Available("A"), Unresolvable()]));
    }

    // ── The detail tiles ────────────────────────────────────────────────────────

    [Fact]
    public void ContactMembers_CarriesTheTypeInTextForANamedMember()
    {
        var member = Assert.Single(InsuranceCard.ContactMembers([Available("Sam Rivera")]));

        Assert.Equal("Sam Rivera", member.Display);
        Assert.False(member.Unnamed);
        Assert.False(string.IsNullOrEmpty(member.TypeLabel));
    }

    [Fact]
    public void ContactMembers_RendersAnArchivedMemberAsItsStateWithItsTypeStillInText()
    {
        var member = Assert.Single(InsuranceCard.ContactMembers([Archived()]));

        Assert.Equal("Archived", member.Display);
        Assert.True(member.Unnamed);
        // The type survives an archive — it is redundant for anyone who can resolve the id anyway.
        Assert.False(string.IsNullOrEmpty(member.TypeLabel));
    }

    /// <summary>
    /// An unresolvable member states no type, because there is no contact row to read one from —
    /// which is exactly why <c>PolicyContactReference.Type</c> is nullable.
    /// </summary>
    [Fact]
    public void ContactMembers_StatesNoTypeForAnUnresolvableMember()
    {
        var member = Assert.Single(InsuranceCard.ContactMembers([Unresolvable()]));

        Assert.Equal("Unavailable", member.Display);
        Assert.True(member.Unnamed);
        Assert.Null(member.TypeLabel);
    }

    [Fact]
    public void AccountMembers_CarryTheirNameAndTypeInText()
    {
        var member = Assert.Single(InsuranceCard.AccountMembers(
        [
            new InsuredAccountReference { AccountId = Guid.NewGuid(), Name = "Maple St", Type = AccountType.Property },
        ]));

        Assert.Equal("Maple St", member.Display);
        Assert.False(member.Unnamed);
        Assert.False(string.IsNullOrEmpty(member.TypeLabel));
    }

    // ── The blocked-delete dialog's per-kind copy ───────────────────────────────

    /// <summary>
    /// Every kind must state itself in text: the dialog's whole job is telling a user WHICH links
    /// block a delete, and a glyph alone cannot carry that.
    /// </summary>
    [Theory]
    [InlineData(InsuranceLinkKind.Insurer, "Insurer")]
    [InlineData(InsuranceLinkKind.InsuredContact, "Insured contact")]
    [InlineData(InsuranceLinkKind.Beneficiary, "Beneficiary")]
    public void KindLabel_NamesEveryKind(InsuranceLinkKind kind, string expected) =>
        Assert.Equal(expected, ContactInsuranceLinksDialog.KindLabel(kind));

    [Fact]
    public void EveryKind_HasADistinctLabelIconAndNote()
    {
        var kinds = Enum.GetValues<InsuranceLinkKind>();

        Assert.Equal(kinds.Length, kinds.Select(ContactInsuranceLinksDialog.KindLabel).Distinct().Count());
        Assert.Equal(kinds.Length, kinds.Select(ContactInsuranceLinksDialog.KindIcon).Distinct().Count());
        Assert.Equal(kinds.Length, kinds.Select(ContactInsuranceLinksDialog.KindNote).Distinct().Count());
        Assert.All(kinds, kind => Assert.False(string.IsNullOrWhiteSpace(ContactInsuranceLinksDialog.KindNote(kind))));
    }
}
