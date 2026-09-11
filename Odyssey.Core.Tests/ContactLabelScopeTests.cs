using Odyssey.Context.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// Guards on <see cref="ContactLabelScope"/> (issue #47 §16.10) — the map every write path, the
/// picker and the clamp read.
/// </summary>
/// <remarks>
/// <b>What these do not check:</b> that a member's classification is <i>stable</i>. Narrowing a
/// member's scope later — dropping <c>Postal</c> from the organization list, say — passes every
/// assertion here and silently strands existing rows in the residual state, because the invariant is
/// established once by a migration and never re-checked. That is the scope-narrowing rule in §6: a
/// narrowing change needs its own remap migration in the same commit.
/// </remarks>
public class ContactLabelScopeTests
{
    private static readonly ContactType[] BothTypes = [ContactType.Person, ContactType.Organization];

    public static TheoryData<ContactType> ContactTypes() => [ContactType.Person, ContactType.Organization];

    // ── Every member is classified somewhere, and no list names a non-member ──

    [Fact]
    public void Every_address_label_is_offered_to_at_least_one_contact_type()
    {
        foreach (var label in Enum.GetValues<AddressLabel>())
            Assert.True(
                BothTypes.Any(t => ContactLabelScope.IsValidFor(label, t)),
                $"AddressLabel.{label} is offered to neither contact type — it can never be written.");
    }

    [Fact]
    public void Every_email_label_is_offered_to_at_least_one_contact_type()
    {
        foreach (var label in Enum.GetValues<EmailLabel>())
            Assert.True(
                BothTypes.Any(t => ContactLabelScope.IsValidFor(label, t)),
                $"EmailLabel.{label} is offered to neither contact type — it can never be written.");
    }

    [Fact]
    public void Every_phone_label_is_offered_to_at_least_one_contact_type()
    {
        foreach (var label in Enum.GetValues<PhoneLabel>())
            Assert.True(
                BothTypes.Any(t => ContactLabelScope.IsValidFor(label, t)),
                $"PhoneLabel.{label} is offered to neither contact type — it can never be written.");
    }

    [Theory]
    [MemberData(nameof(ContactTypes))]
    public void No_scope_list_names_an_undefined_member(ContactType type)
    {
        Assert.All(ContactLabelScope.AddressLabelsFor(type), l => Assert.True(Enum.IsDefined(l)));
        Assert.All(ContactLabelScope.EmailLabelsFor(type), l => Assert.True(Enum.IsDefined(l)));
        Assert.All(ContactLabelScope.PhoneLabelsFor(type), l => Assert.True(Enum.IsDefined(l)));
    }

    [Theory]
    [MemberData(nameof(ContactTypes))]
    public void No_scope_list_repeats_a_member(ContactType type)
    {
        Assert.Distinct(ContactLabelScope.AddressLabelsFor(type));
        Assert.Distinct(ContactLabelScope.EmailLabelsFor(type));
        Assert.Distinct(ContactLabelScope.PhoneLabelsFor(type));
    }

    // ── Other is the universal clamp target ──────────────────────────────────

    /// <summary>
    /// <c>Other</c> is what makes the clamp and the remap legal at all: if it were scoped to one type,
    /// the other type's clamp would have nowhere to land.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContactTypes))]
    public void Other_is_valid_for_both_contact_types_in_all_three_vocabularies(ContactType type)
    {
        Assert.True(ContactLabelScope.IsValidFor(AddressLabel.Other, type));
        Assert.True(ContactLabelScope.IsValidFor(EmailLabel.Other, type));
        Assert.True(ContactLabelScope.IsValidFor(PhoneLabel.Other, type));
    }

    [Theory]
    [MemberData(nameof(ContactTypes))]
    public void Clamp_resolves_every_invalid_member_to_Other_and_leaves_every_valid_one_alone(ContactType type)
    {
        foreach (var label in Enum.GetValues<AddressLabel>())
            Assert.Equal(
                ContactLabelScope.IsValidFor(label, type) ? label : AddressLabel.Other,
                ContactLabelScope.Clamp(label, type));

        foreach (var label in Enum.GetValues<EmailLabel>())
            Assert.Equal(
                ContactLabelScope.IsValidFor(label, type) ? label : EmailLabel.Other,
                ContactLabelScope.Clamp(label, type));

        foreach (var label in Enum.GetValues<PhoneLabel>())
            Assert.Equal(
                ContactLabelScope.IsValidFor(label, type) ? label : PhoneLabel.Other,
                ContactLabelScope.Clamp(label, type));
    }

    /// <summary>An undefined ordinal reaching the clamp resolves to <c>Other</c> like any other
    /// invalid value — the import path calls this before the service ever sees the row.</summary>
    [Theory]
    [MemberData(nameof(ContactTypes))]
    public void Clamp_resolves_an_undefined_ordinal_to_Other(ContactType type)
    {
        Assert.Equal(AddressLabel.Other, ContactLabelScope.Clamp((AddressLabel)99, type));
        Assert.Equal(EmailLabel.Other, ContactLabelScope.Clamp((EmailLabel)99, type));
        Assert.Equal(PhoneLabel.Other, ContactLabelScope.Clamp((PhoneLabel)99, type));
    }

    // ── The default is the first offered member ──────────────────────────────

    /// <summary>
    /// Pins display order and the default together. Stated separately from the lists so a reordering
    /// that moves the intended default off the front fails here rather than silently changing what a
    /// new contact method opens on.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContactTypes))]
    public void The_default_label_is_always_the_first_offered_member(ContactType type)
    {
        Assert.Equal(ContactLabelScope.AddressLabelsFor(type)[0], ContactLabelScope.DefaultAddressLabel(type));
        Assert.Equal(ContactLabelScope.EmailLabelsFor(type)[0], ContactLabelScope.DefaultEmailLabel(type));
        Assert.Equal(ContactLabelScope.PhoneLabelsFor(type)[0], ContactLabelScope.DefaultPhoneLabel(type));
    }

    /// <summary>The vocabularies themselves, spelled out — issue #47 §6's tables are the contract, and
    /// the ordinals are the wire format, so both are pinned by value.</summary>
    [Fact]
    public void The_organization_vocabularies_are_the_ones_the_specification_declares()
    {
        Assert.Equal(
            [AddressLabel.Visiting, AddressLabel.Registered, AddressLabel.Branch, AddressLabel.Billing, AddressLabel.Postal, AddressLabel.Other],
            ContactLabelScope.AddressLabelsFor(ContactType.Organization));
        Assert.Equal(
            [EmailLabel.General, EmailLabel.Support, EmailLabel.Sales, EmailLabel.Billing, EmailLabel.Claims, EmailLabel.Other],
            ContactLabelScope.EmailLabelsFor(ContactType.Organization));
        Assert.Equal(
            [
                PhoneLabel.Switchboard, PhoneLabel.Support, PhoneLabel.Sales, PhoneLabel.Billing,
                PhoneLabel.Claims, PhoneLabel.Emergency, PhoneLabel.Direct, PhoneLabel.Mobile, PhoneLabel.Other,
            ],
            ContactLabelScope.PhoneLabelsFor(ContactType.Organization));
    }

    [Fact]
    public void The_person_vocabularies_are_the_ones_the_specification_declares()
    {
        Assert.Equal(
            [AddressLabel.Home, AddressLabel.Work, AddressLabel.Billing, AddressLabel.Postal, AddressLabel.Other],
            ContactLabelScope.AddressLabelsFor(ContactType.Person));
        Assert.Equal(
            [EmailLabel.Home, EmailLabel.Work, EmailLabel.Other],
            ContactLabelScope.EmailLabelsFor(ContactType.Person));
        Assert.Equal(
            [PhoneLabel.Home, PhoneLabel.Work, PhoneLabel.Mobile, PhoneLabel.Other],
            ContactLabelScope.PhoneLabelsFor(ContactType.Person));
    }

    /// <summary>
    /// The ordinals are the wire contract — these enums serialize as integers — so a member that
    /// silently changed number would remap every persisted row. Existing ordinals are pinned by issue
    /// #325; the new ones by issue #47 §6.
    /// </summary>
    [Fact]
    public void The_label_ordinals_are_the_wire_contract()
    {
        Assert.Equal((1, 2, 3, 4, 5, 20, 21, 22), (
            (int)AddressLabel.Home, (int)AddressLabel.Work, (int)AddressLabel.Billing, (int)AddressLabel.Other,
            (int)AddressLabel.Postal, (int)AddressLabel.Visiting, (int)AddressLabel.Registered, (int)AddressLabel.Branch));

        Assert.Equal((1, 2, 3, 20, 21, 22, 23, 24), (
            (int)EmailLabel.Home, (int)EmailLabel.Work, (int)EmailLabel.Other, (int)EmailLabel.General,
            (int)EmailLabel.Support, (int)EmailLabel.Sales, (int)EmailLabel.Billing, (int)EmailLabel.Claims));

        Assert.Equal((1, 2, 3, 4, 20, 21, 22, 23, 24, 25, 26), (
            (int)PhoneLabel.Home, (int)PhoneLabel.Work, (int)PhoneLabel.Mobile, (int)PhoneLabel.Other,
            (int)PhoneLabel.Switchboard, (int)PhoneLabel.Support, (int)PhoneLabel.Sales, (int)PhoneLabel.Billing,
            (int)PhoneLabel.Claims, (int)PhoneLabel.Emergency, (int)PhoneLabel.Direct));

        // Ordinal 5 stays free on PhoneLabel — a future person/shared label takes it rather than
        // extending the organization band (issue #47 §6).
        Assert.False(Enum.IsDefined((PhoneLabel)5));
    }

    // ── The assumptions the 422's message rests on (issue #47 §16.8) ──────────

    /// <summary>
    /// The rejection names the parent contact's <c>Type</c> and the valid set for it, which is a latent
    /// existence-and-type oracle for a principal holding <c>contacts.update</c> without
    /// <c>contacts.read</c>. No shipped role is in that position — and this pins it, so a future role
    /// edit that creates one fails here rather than quietly widening the disclosure (§10.5).
    ///
    /// <para>The same test pins §10.10: <c>PUT /api/contacts/{id}</c> upserts by invoking <c>Post</c> as
    /// a direct method call, so its <c>contacts.create</c> policy never re-runs — a role holding update
    /// without create would hold an ungated creation primitive.</para>
    /// </summary>
    [Theory]
    [InlineData(nameof(RolePermissions.AdminClaims))]
    [InlineData(nameof(RolePermissions.OwnerClaims))]
    [InlineData(nameof(RolePermissions.UserClaims))]
    [InlineData(nameof(RolePermissions.GuestClaims))]
    public void No_shipped_role_holds_contacts_update_without_read_and_create(string roleName)
    {
        var claims = roleName switch
        {
            nameof(RolePermissions.AdminClaims) => RolePermissions.AdminClaims,
            nameof(RolePermissions.OwnerClaims) => RolePermissions.OwnerClaims,
            nameof(RolePermissions.UserClaims) => RolePermissions.UserClaims,
            _ => RolePermissions.GuestClaims,
        };

        if (!claims.Contains(PermissionClaims.ContactsUpdate))
            return;

        Assert.Contains(PermissionClaims.ContactsRead, claims);
        Assert.Contains(PermissionClaims.ContactsCreate, claims);
    }
}
