using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Core.Tests;

public class ContactServiceTests
{
    private static NewContact Person(string first, string last, string? displayName = null, bool archived = false) => new()
    {
        Type = ContactType.Person,
        DisplayName = displayName,
        Archived = archived,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContact Org(string legalName, string? displayName = null, bool archived = false, string? orgNumber = null, string? website = null) => new()
    {
        Type = ContactType.Organization,
        DisplayName = displayName,
        Archived = archived,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName, OrganizationNumber = orgNumber, Website = website },
    };

    [Fact]
    public async Task Create_Person_ResolvesDisplayNameFromNameParts()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Person("Michael", "Chen"));

        Assert.Equal("Michael Chen", created.ResolvedDisplayName);
        Assert.Null(created.DisplayName);
        Assert.Equal("MICHAEL CHEN", created.NormalizedName);
        Assert.Equal(ContactType.Person, created.Type);
        Assert.NotNull(created.PersonDetails);
        Assert.Null(created.OrganizationDetails);
    }

    [Fact]
    public async Task Create_Organization_ResolvesDisplayNameFromLegalName()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Org("Lakeside Property Management LLC"));

        Assert.Equal("Lakeside Property Management LLC", created.ResolvedDisplayName);
        Assert.Equal(ContactType.Organization, created.Type);
        Assert.NotNull(created.OrganizationDetails);
        Assert.Null(created.PersonDetails);
    }

    [Fact]
    public async Task Create_DisplayNameOverride_WinsOverFallback()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Person("Sarah", "Whitfield", displayName: "Sarah (agent)"));

        Assert.Equal("Sarah (agent)", created.ResolvedDisplayName);
        Assert.Equal("Sarah (agent)", created.DisplayName);
    }

    [Fact]
    public async Task Create_PersonWithOrganizationDetails_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Nope" },
        }));
    }

    [Fact]
    public async Task Create_OrganizationWithoutDetails_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(new NewContact
        {
            Type = ContactType.Organization,
            Archived = false,
        }));
    }

    [Fact]
    public async Task Create_FutureDateOfBirth_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var body = Person("Future", "Person");
        body.PersonDetails!.DateOfBirth = DateTime.UtcNow.AddDays(1);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(body));
    }

    [Fact]
    public async Task Create_NonHttpWebsite_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.Create(Org("Evil Co", website: "javascript:alert(1)")));
    }

    [Fact]
    public async Task Update_ChangingType_SwapsDetailSubRecord()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Person("Jane", "Doe"));
        var updated = await service.Update(created.ContactId, Org("Doe Consulting"));

        Assert.NotNull(updated);
        Assert.Equal(ContactType.Organization, updated!.Type);
        Assert.NotNull(updated.OrganizationDetails);
        Assert.Null(updated.PersonDetails);
        Assert.Equal("Doe Consulting", updated.ResolvedDisplayName);
    }

    [Fact]
    public async Task Delete_RemovesContact()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Org("To Delete"));
        await service.Delete(created.ContactId);

        Assert.Null(await service.Get(created.ContactId));
    }

    // ── Server-side list contract (issue #277) ────────────────────────────────

    [Fact]
    public async Task SearchFor_WithQuery_FiltersByNormalizedName()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        await service.Create(Org("Amazon"));
        await service.Create(Org("Apple Store"));
        await service.Create(Org("Netflix"));

        var results = (await service.ListAsync(new ContactsQueryParams { Search = "apple" })).Items;

        Assert.Equal("Apple Store", Assert.Single(results).ResolvedDisplayName);
    }

    [Fact]
    public async Task ListAsync_SortByName_RespectsDirection()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        foreach (var name in new[] { "Bravo", "Alpha", "Charlie" })
            await service.Create(Org(name));

        var asc = await service.ListAsync(new ContactsQueryParams { SortBy = ContactSortBy.Name, SortDir = SortDirection.Asc });
        Assert.Equal(["Alpha", "Bravo", "Charlie"], asc.Items.Select(c => c.ResolvedDisplayName));

        var desc = await service.ListAsync(new ContactsQueryParams { SortBy = ContactSortBy.Name, SortDir = SortDirection.Desc });
        Assert.Equal(["Charlie", "Bravo", "Alpha"], desc.Items.Select(c => c.ResolvedDisplayName));
    }

    [Fact]
    public async Task ListAsync_TypeFilter_MatchesSelectedTypes()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        await service.Create(Person("Ada", "Lovelace"));
        await service.Create(Org("Corp"));

        var page = await service.ListAsync(new ContactsQueryParams { Types = [ContactType.Organization] });

        Assert.Equal("Corp", Assert.Single(page.Items).ResolvedDisplayName);
    }

    [Fact]
    public async Task ArchiveAndUnarchiveTransitionsArchivedTimestamp()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Org("Electric Co"));

        await service.Update(created.ContactId, Org("Electric Co", archived: true));
        Assert.NotNull((await service.Get(created.ContactId))!.Archived);

        await service.Update(created.ContactId, Org("Electric Co", archived: false));
        Assert.Null((await service.Get(created.ContactId))!.Archived);
    }

    // ── Contact sub-resources (issue #325) ────────────────────────────────────

    [Fact]
    public async Task CreateAddress_FirstRecord_BecomesPrimary_AndBumpsUpdatedAt()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Org("Landlord LLC"));
        var before = (await service.Get(cp.ContactId))!.UpdatedAt;

        var address = await service.CreateAddress(cp.ContactId, new NewAddress
        {
            Label = AddressLabel.Home, Line1 = "Storgata 55", City = "Oslo", CountryCode = "no",
        });

        Assert.NotNull(address);
        Assert.True(address!.IsPrimary);
        Assert.Equal("NO", address.CountryCode);
        Assert.True((await service.Get(cp.ContactId))!.UpdatedAt >= before);
    }

    [Fact]
    public async Task SettingSecondAddressPrimary_ClearsThePreviousPrimary()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Org("Landlord LLC"));

        var first = await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO" });
        var second = await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Work, Line1 = "B", City = "Bergen", CountryCode = "NO", IsPrimary = true });

        var addresses = await service.GetAddresses(cp.ContactId);
        Assert.NotNull(addresses);
        Assert.Single(addresses!, a => a.IsPrimary);
        Assert.True(addresses!.Single(a => a.Id == second!.Id).IsPrimary);
        Assert.False(addresses.Single(a => a.Id == first!.Id).IsPrimary);
    }

    [Fact]
    public async Task DeletingPrimaryAddress_PromotesAnotherRow()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Org("Landlord LLC"));
        var first = await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO" });
        await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Work, Line1 = "B", City = "Bergen", CountryCode = "NO" });

        Assert.True(await service.DeleteAddress(cp.ContactId, first!.Id));

        var addresses = await service.GetAddresses(cp.ContactId);
        Assert.Single(addresses!);
        Assert.True(addresses!.Single().IsPrimary);
    }

    [Fact]
    public async Task UpdateAddress_ForWrongParent_ReturnsFalse()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var owner = await service.Create(Org("Owner"));
        var other = await service.Create(Org("Other"));
        var address = await service.CreateAddress(owner.ContactId, new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO" });

        // The address exists, but not under 'other' — must be treated as not found.
        var updated = await service.UpdateAddress(other.ContactId, address!.Id, new NewAddress { Label = AddressLabel.Work, Line1 = "Z", City = "Oslo", CountryCode = "NO" });

        Assert.False(updated);
    }

    [Fact]
    public async Task GetAddresses_ForUnknownContact_ReturnsNull()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        Assert.Null(await service.GetAddresses(Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateEmailAndPhone_RoundTripThroughGet()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Person("Priya", "Nair"));

        await service.CreateEmail(cp.ContactId, new NewEmailAddress { Label = EmailLabel.Work, Value = "p.nair@example.org" });
        await service.CreatePhone(cp.ContactId, new NewPhoneNumber { Label = PhoneLabel.Mobile, Value = "+1 650 555 0088" });

        var fetched = await service.Get(cp.ContactId);
        Assert.Equal("p.nair@example.org", Assert.Single(fetched!.EmailAddresses).Value);
        Assert.Equal("+1 650 555 0088", Assert.Single(fetched.PhoneNumbers).Value);
    }

    // ── Update-path primary arbitration (finding F4, update branch) ───────────

    [Fact]
    public async Task UpdatingAnAddressToPrimary_StealsPrimaryFromTheSibling()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Org("Landlord LLC"));
        var first = await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO" });
        var second = await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Work, Line1 = "B", City = "Bergen", CountryCode = "NO" });

        // second starts non-primary; promoting it via UPDATE must clear the previous primary (first).
        Assert.True(await service.UpdateAddress(cp.ContactId, second!.Id,
            new NewAddress { Label = AddressLabel.Work, Line1 = "B", City = "Bergen", CountryCode = "NO", IsPrimary = true }));

        var addresses = await service.GetAddresses(cp.ContactId);
        Assert.NotNull(addresses);
        Assert.True(addresses.Single(a => a.Id == second!.Id).IsPrimary);
        Assert.False(addresses.Single(a => a.Id == first!.Id).IsPrimary);
    }

    [Fact]
    public async Task ClearingThePrimaryFlagViaUpdate_KeepsExactlyOnePrimary()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Org("Landlord LLC"));
        var first = await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO" });
        await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Work, Line1 = "B", City = "Bergen", CountryCode = "NO" });

        // Clearing the only primary must not leave the collection with zero primaries.
        Assert.True(await service.UpdateAddress(cp.ContactId, first!.Id,
            new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO", IsPrimary = false }));

        var addresses = await service.GetAddresses(cp.ContactId);
        Assert.Single(addresses!, a => a.IsPrimary);
    }

    [Fact]
    public async Task ChildMutation_BumpsParentUpdatedAt_StrictlyForward()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard(), clock);
        var cp = await service.Create(Org("Landlord LLC"));
        var before = (await service.Get(cp.ContactId))!.UpdatedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.CreateAddress(cp.ContactId, new NewAddress { Label = AddressLabel.Home, Line1 = "A", City = "Oslo", CountryCode = "NO" });

        var after = (await service.Get(cp.ContactId))!.UpdatedAt;
        Assert.True(after > before, $"expected UpdatedAt to advance past {before:o}, got {after:o}");
    }

    // ── PUT-as-upsert cross-field validation (architect finding #6) ───────────

    [Fact]
    public async Task Update_WithMismatchedTypeAndDetails_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var cp = await service.Create(Org("Acme"));

        // Type=Person but only OrganizationDetails supplied — must be rejected on the update path too.
        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(cp.ContactId, new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            OrganizationDetails = new OrganizationDetailsDto { LegalName = "Acme" },
        }));
    }

    // ── ExternalUid generation & collision (issue #338 §6) ────────────────────

    [Fact]
    public async Task Create_WithoutExternalUid_GeneratesUrnUuid()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var created = await service.Create(Org("Acme"));

        var externalUid = (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid;
        Assert.StartsWith("urn:uuid:", externalUid);
        Assert.True(Guid.TryParse(externalUid["urn:uuid:".Length..], out _));
    }

    [Fact]
    public async Task Create_WithExternalUid_StoresSuppliedValueVerbatim()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var body = Org("Acme");
        body.ExternalUid = "urn:uuid:11111111-1111-1111-1111-111111111111";

        var created = await service.Create(body);

        var externalUid = (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid;
        Assert.Equal("urn:uuid:11111111-1111-1111-1111-111111111111", externalUid);
    }

    [Fact]
    public async Task Create_TwoContacts_GetDifferentAutoGeneratedExternalUids()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());

        var first = await service.Create(Org("Acme"));
        var second = await service.Create(Org("Globex"));

        var firstUid = (await context.Contacts.FindAsync(first.ContactId))!.ExternalUid;
        var secondUid = (await context.Contacts.FindAsync(second.ContactId))!.ExternalUid;
        Assert.NotEqual(firstUid, secondUid);
    }

    [Fact]
    public async Task Create_WithControlCharacterInExternalUid_Throws()
    {
        // Every other free-text field (Notes, LegalName, ...) already rejects control characters via
        // CleanRequired/CleanOptional; ExternalUid must too, since an unescaped control character (e.g.
        // an embedded CRLF) would corrupt the exported vCard's line structure (issue #338 review).
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var body = Org("Acme");
        body.ExternalUid = "legit-uid\r\nEND:VCARD\r\nBEGIN:VCARD\r\nUID:forged";

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(body));
    }

    [Fact]
    public async Task Create_WithOversizedExternalUid_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var body = Org("Acme");
        body.ExternalUid = new string('a', 256);

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(body));
    }

    [Fact]
    public async Task Create_WithExternalUidAlreadyUsedByAnotherContact_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var existing = Org("Acme");
        existing.ExternalUid = "urn:uuid:22222222-2222-2222-2222-222222222222";
        await service.Create(existing);

        var duplicate = Org("Globex");
        duplicate.ExternalUid = "urn:uuid:22222222-2222-2222-2222-222222222222";

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Create(duplicate));
    }

    [Fact]
    public async Task Update_WithExternalUidAlreadyUsedByADifferentContact_Throws()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var first = Org("Acme");
        first.ExternalUid = "urn:uuid:33333333-3333-3333-3333-333333333333";
        await service.Create(first);
        var second = await service.Create(Org("Globex"));

        var update = Org("Globex Renamed");
        update.ExternalUid = "urn:uuid:33333333-3333-3333-3333-333333333333";

        await Assert.ThrowsAsync<DomainValidationException>(() => service.Update(second.ContactId, update));
    }

    [Fact]
    public async Task Update_WithItsOwnCurrentExternalUid_Succeeds()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Org("Acme"));
        var ownUid = (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid;

        var update = Org("Acme Renamed");
        update.ExternalUid = ownUid;
        var result = await service.Update(created.ContactId, update);

        Assert.NotNull(result);
        Assert.Equal(ownUid, (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid);
    }

    [Fact]
    public async Task Update_WithoutSupplyingExternalUid_LeavesItUnchanged()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Org("Acme"));
        var ownUid = (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid;

        await service.Update(created.ContactId, Org("Acme Renamed"));

        Assert.Equal(ownUid, (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid);
    }

    [Fact]
    public async Task FindIdByExternalUid_MatchesExactly()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = new ContactService(context, new NoopContactReferenceGuard());
        var created = await service.Create(Org("Acme"));
        var ownUid = (await context.Contacts.FindAsync(created.ContactId))!.ExternalUid;

        Assert.Equal(created.ContactId, await service.FindIdByExternalUid(ownUid));
        Assert.Null(await service.FindIdByExternalUid("urn:uuid:00000000-0000-0000-0000-000000000000"));
    }

    // A monotonic, advanceable clock so timestamp bumps can be asserted strictly (not via wall time).
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now = now.Add(by);
    }
}
