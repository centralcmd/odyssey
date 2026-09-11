using Microsoft.EntityFrameworkCore;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The contact type-switch remap (issue #47 §5, §16.6) — the fourth mechanism holding the invariant
/// "a contact method's label is valid for its contact's type".
/// </summary>
public class ContactLabelRemapTests
{
    private static NewContact Person(string first = "Ada", string last = "Lovelace", bool archived = false) => new()
    {
        Type = ContactType.Person,
        Archived = archived,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContact Org(string legalName = "Acme", bool archived = false) => new()
    {
        Type = ContactType.Organization,
        Archived = archived,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName },
    };

    /// <summary>
    /// Person → Organization clamps all three collections in the same save. A round trip back is
    /// deliberately lossy: the remap writes <c>Other</c> and never guesses a "closest" label.
    /// </summary>
    [Fact]
    public async Task Switching_a_person_to_an_organization_clamps_every_contact_method_label()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Person());

        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Home, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO",
        });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Home, Value = "ada@example.com" });
        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Home, Value = "+47 22 00 00 00" });

        var updated = await service.Update(created.ContactId, Org());

        Assert.NotNull(updated);
        Assert.Equal(ContactType.Organization, updated!.Type);
        Assert.Equal(AddressLabel.Other, Assert.Single(updated.Addresses).Label);
        Assert.Equal(EmailLabel.Other, Assert.Single(updated.EmailAddresses).Label);
        Assert.Equal(PhoneLabel.Other, Assert.Single(updated.PhoneNumbers).Label);
    }

    /// <summary>A label valid for BOTH types survives the switch — the remap clamps, it does not reset.</summary>
    [Fact]
    public async Task A_shared_label_survives_a_type_switch_untouched()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Person());

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Mobile, Value = "+47 900 00 000" });
        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Postal, Line1 = "Postboks 1", City = "Oslo", CountryCode = "NO",
        });

        var updated = await service.Update(created.ContactId, Org());

        Assert.Equal(PhoneLabel.Mobile, Assert.Single(updated!.PhoneNumbers).Label);
        Assert.Equal(AddressLabel.Postal, Assert.Single(updated.Addresses).Label);
    }

    /// <summary>
    /// A <c>PUT</c> that does not change the type must not touch the child collections at all.
    ///
    /// <para>Asserted on the change tracker rather than on a query count: <c>ReadQuery()</c> is
    /// <c>AsNoTracking()</c>, so the remap is the only thing that can put a child entity in it — and a
    /// query count needs a relational <c>DbCommandInterceptor</c>, which cannot run on this tier
    /// (the same trap CLAUDE.md records for <c>ExecuteDeleteAsync</c>).</para>
    /// </summary>
    [Fact]
    public async Task An_unchanged_type_puts_no_child_entity_in_the_change_tracker()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Org());

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Switchboard, Value = "+47 22 00 00 00" });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.General, Value = "post@acme.example" });
        await service.CreateAddress(created.ContactId, new NewAddress
        {
            Label = AddressLabel.Visiting, Line1 = "Storgata 55", City = "Oslo", CountryCode = "NO",
        });

        context.ChangeTracker.Clear();
        await service.Update(created.ContactId, Org("Acme Renamed"));

        Assert.Empty(context.ChangeTracker.Entries<Context.Address>());
        Assert.Empty(context.ChangeTracker.Entries<Context.EmailAddress>());
        Assert.Empty(context.ChangeTracker.Entries<Context.PhoneNumber>());
    }

    /// <summary>A changed type does the opposite — which is what makes the assertion above meaningful
    /// rather than a tautology about a tracker nothing ever populates.</summary>
    [Fact]
    public async Task A_changed_type_does_put_the_child_rows_in_the_change_tracker()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Org());

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Switchboard, Value = "+47 22 00 00 00" });

        context.ChangeTracker.Clear();
        await service.Update(created.ContactId, Person());

        Assert.NotEmpty(context.ChangeTracker.Entries<Context.PhoneNumber>());
    }

    /// <summary>Organization → Person, the mirror direction.</summary>
    [Fact]
    public async Task Switching_an_organization_to_a_person_clamps_the_organization_band()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Org());

        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Claims, Value = "+47 22 00 00 00" });
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Support, Value = "support@acme.example" });

        var updated = await service.Update(created.ContactId, Person());

        Assert.Equal(PhoneLabel.Other, Assert.Single(updated!.PhoneNumbers).Label);
        Assert.Equal(EmailLabel.Other, Assert.Single(updated.EmailAddresses).Label);
    }
}
