using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;
using Fields = Odyssey.Dtos.Journal.ContactConversionFields;

namespace Odyssey.Core.Tests;

/// <summary>Converting a contact between Person and Organization in place (issue #335).</summary>
public class ContactConversionTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    // ── Mapper (§8) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Mapper_PersonToOrganization_FoldsNamePartsAndCarriesDates()
    {
        var contact = PersonEntity("Michael", "Chen", middle: "Wei", birth: new(1990, 4, 12), sex: Sex.Male, title: "Engineer", company: "Northwind");

        var d = ContactConversionMapper.Derive(contact, ContactType.Organization, carryLifecycleDates: true, Today);

        Assert.Equal("Michael Wei Chen", d.OrganizationDetails!.LegalName);
        Assert.Equal(new DateTime(1990, 4, 12), d.OrganizationDetails.EstablishedDate);
        Assert.Null(d.OrganizationDetails.OrganizationNumber);
        Assert.Null(d.PersonDetails);
        Assert.Equal([Fields.Sex, Fields.Title, Fields.Company], d.Lost);
        Assert.Contains(d.Converted, c => c.From == Fields.DateOfBirth && c.To == Fields.EstablishedDate);
        Assert.Contains(d.Converted, c => c.From == Fields.MiddleName && c.To == Fields.LegalName);
        Assert.Empty(d.Issues);
    }

    [Fact]
    public void Mapper_PersonToOrganization_DisplayNameWinsAndNamePartsAreLost()
    {
        var contact = PersonEntity("Sarah", "Whitfield", middle: "Ann");
        contact.DisplayName = "Whitfield Lettings";

        var d = ContactConversionMapper.Derive(contact, ContactType.Organization, true, Today);

        Assert.Equal("Whitfield Lettings", d.OrganizationDetails!.LegalName);
        Assert.Equal([Fields.FirstName, Fields.MiddleName, Fields.LastName], d.Lost);
    }

    [Fact]
    public void Mapper_PersonToOrganization_OverLongLegalNameIsAnIssue_NeverTruncated()
    {
        var contact = PersonEntity(new string('a', 128), new string('b', 128), middle: new string('c', 10));

        var d = ContactConversionMapper.Derive(contact, ContactType.Organization, true, Today);

        Assert.Equal(Fields.LegalName, Assert.Single(d.Issues).Field);
        Assert.True(d.OrganizationDetails!.LegalName.Length > 256);
    }

    [Fact]
    public void Mapper_OrganizationToPerson_SplitsAtLastWord()
    {
        var contact = OrgEntity("Northwind Labs", established: new(1998, 11, 4), dissolved: new(2023, 6, 30), orgNumber: "98-7654321", website: "https://x.example");

        var d = ContactConversionMapper.Derive(contact, ContactType.Person, true, Today);

        Assert.Equal("Northwind", d.PersonDetails!.FirstName);
        Assert.Equal("Labs", d.PersonDetails.LastName);
        Assert.Equal(new DateTime(1998, 11, 4), d.PersonDetails.DateOfBirth);
        Assert.Equal(new DateTime(2023, 6, 30), d.PersonDetails.DateOfDeath);
        Assert.Equal([Fields.OrganizationNumber, Fields.Website], d.Lost);
        Assert.Empty(d.Notes);
        Assert.Empty(d.Issues);
    }

    [Fact]
    public void Mapper_OrganizationToPerson_SingleWord_IsALastNameIssue_ResolvedByOverride()
    {
        var contact = OrgEntity("Equinor");

        var issue = Assert.Single(ContactConversionMapper.Derive(contact, ContactType.Person, true, Today).Issues);
        Assert.Equal(Fields.LastName, issue.Field);

        var overridden = ContactConversionMapper.Derive(contact, ContactType.Person, true, Today, lastNameOverride: "ASA");
        Assert.Empty(overridden.Issues);
        Assert.Equal("Equinor", overridden.PersonDetails!.FirstName);
        Assert.Equal("ASA", overridden.PersonDetails.LastName);
    }

    [Fact]
    public void Mapper_OrganizationToPerson_MoreThanTwoWords_CarriesTheLowConfidenceNote()
    {
        var d = ContactConversionMapper.Derive(OrgEntity("Acme Holding Ltd"), ContactType.Person, true, Today);

        Assert.Equal("Acme Holding", d.PersonDetails!.FirstName);
        Assert.Equal("Ltd", d.PersonDetails.LastName);
        Assert.Equal(ContactConversionCodes.NameSplitLowConfidence, Assert.Single(d.Notes).Code);
    }

    [Fact]
    public void Mapper_WithoutCarry_DropsTheDatesIntoLostFields()
    {
        var d = ContactConversionMapper.Derive(
            OrgEntity("Pacific Home", established: new(1974, 6, 1), dissolved: new(2023, 6, 30)),
            ContactType.Person, carryLifecycleDates: false, Today);

        Assert.Null(d.PersonDetails!.DateOfBirth);
        Assert.Null(d.PersonDetails.DateOfDeath);
        Assert.Contains(Fields.EstablishedDate, d.Lost);
        Assert.Contains(Fields.DissolvedDate, d.Lost);
        Assert.DoesNotContain(d.Converted, c => c.From == Fields.EstablishedDate);
    }

    [Fact]
    public void Mapper_LegacyDatesOutOfOrder_IsATargetFieldIssue()
    {
        var d = ContactConversionMapper.Derive(
            OrgEntity("Old Corp", established: new(2000, 1, 1), dissolved: new(1990, 1, 1)),
            ContactType.Person, true, Today);

        Assert.Equal(Fields.DateOfDeath, Assert.Single(d.Issues).Field);
    }

    // ── Service: conversion ─────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_PersonToOrganization_KeepsIdAndSwapsTheDetailsRow()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Michael", "Chen"));

        var result = await service.ConvertAsync(created.ContactId, Request(ContactType.Organization, created.UpdatedAt));

        Assert.NotNull(result);
        Assert.Equal(ContactType.Person, result!.PreviousType);
        Assert.Equal(created.ContactId, result.Contact.ContactId);
        Assert.Equal(ContactType.Organization, result.Contact.Type);
        Assert.Equal("Michael Chen", result.Contact.OrganizationDetails!.LegalName);
        Assert.Null(result.Contact.PersonDetails);
        Assert.Equal(0, await context.PersonDetails.CountAsync());
        Assert.Equal(created.ExternalUid, result.Contact.ExternalUid);
    }

    [Fact]
    public async Task Convert_SingleWordLegalName_Is422OnLastName_AndAnOverrideConverts()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Org("Equinor"));

        var ex = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.ConvertAsync(created.ContactId, Request(ContactType.Person, created.UpdatedAt)));
        Assert.True(ex.Errors!.ContainsKey(Fields.LastName));

        var result = await service.ConvertAsync(
            created.ContactId, Request(ContactType.Person, created.UpdatedAt) with { LastName = "ASA" });
        Assert.Equal("ASA", result!.Contact.PersonDetails!.LastName);
    }

    [Fact]
    public async Task Convert_SameType_Is422OnTargetType()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Org("Acme Corp"));

        var ex = await Assert.ThrowsAsync<DomainUnprocessableException>(
            () => service.ConvertAsync(created.ContactId, Request(ContactType.Organization, created.UpdatedAt)));
        Assert.True(ex.Errors!.ContainsKey("targetType"));
    }

    [Fact]
    public async Task Convert_StaleExpectedUpdatedAt_IsRefused_AndChangesNothing()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Org("Acme Corp"));

        await Assert.ThrowsAsync<ContactConversionStaleException>(
            () => service.ConvertAsync(created.ContactId, Request(ContactType.Person, created.UpdatedAt.AddSeconds(-1))));

        context.ChangeTracker.Clear();
        Assert.Equal(ContactType.Organization, (await service.Get(created.ContactId))!.Type);
    }

    [Theory]
    [InlineData("Line\nbreak")]
    [InlineData("Tab\u0007bell")]
    public async Task Convert_OverrideWithControlCharacter_Is400KeyedOnTheField(string legalName)
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Ada", "Lovelace"));

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.ConvertAsync(created.ContactId, Request(ContactType.Organization, created.UpdatedAt) with { LegalName = legalName }));
        Assert.True(ex.Errors!.ContainsKey(Fields.LegalName));
    }

    [Fact]
    public async Task Convert_ClampsOutOfScopeLabels_AndMatchesThePreviewCount()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Ada", "Lovelace"));
        await service.CreateEmail(created.ContactId, new NewEmailAddress { Label = EmailLabel.Home, Value = "ada@example.com" });
        await service.CreatePhone(created.ContactId, new NewPhoneNumber { Label = PhoneLabel.Mobile, Value = "+47 400 00 000" });
        var current = (await service.Get(created.ContactId))!;

        var preview = await service.PreviewConversionAsync(created.ContactId, ContactType.Organization, true);
        var result = await service.ConvertAsync(created.ContactId, Request(ContactType.Organization, current.UpdatedAt));

        Assert.Equal(1, preview!.RemappedLabels.Emails);
        Assert.Equal(0, preview.RemappedLabels.Phones);
        Assert.Equal(EmailLabel.Other, Assert.Single(result!.Contact.EmailAddresses).Label);
        Assert.Equal(PhoneLabel.Mobile, Assert.Single(result.Contact.PhoneNumbers).Label);
    }

    [Fact]
    public async Task Preview_WritesNothing_AndAgreesWithTheConversion()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(new NewContact
        {
            Type = ContactType.Person,
            Archived = false,
            PersonDetails = new PersonDetailsDto { FirstName = "Priya", LastName = "Nair", Title = "Physician", DateOfBirth = new DateTime(1962, 7, 19) },
        });

        var preview = await service.PreviewConversionAsync(created.ContactId, ContactType.Organization, true);

        Assert.Equal(created.UpdatedAt, (await service.Get(created.ContactId))!.UpdatedAt);
        Assert.True(preview!.CanConvert);
        Assert.Equal(created.UpdatedAt, preview.UpdatedAt);
        Assert.Equal([Fields.Title], preview.LostFields);

        var result = await service.ConvertAsync(created.ContactId, Request(ContactType.Organization, preview.UpdatedAt));
        Assert.Equal(preview.OrganizationDetails!.LegalName, result!.Contact.OrganizationDetails!.LegalName);
        Assert.Equal(preview.OrganizationDetails.EstablishedDate, result.Contact.OrganizationDetails.EstablishedDate);
    }

    [Fact]
    public async Task Convert_UnknownId_ReturnsNull()
    {
        await using var context = TestContextFactory.CreateJournal();

        Assert.Null(await Service(context).ConvertAsync(Guid.NewGuid(), Request(ContactType.Person, DateTime.UtcNow)));
        Assert.Null(await Service(context).PreviewConversionAsync(Guid.NewGuid(), ContactType.Person, true));
    }

    // ── Blockers (§8.3) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_PersonTaggedOnAPhoto_IsBlocked_AndChangesNothing()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Diego", "Ramos"));
        context.PhotoPeople.Add(new PhotoPerson { PhotoId = Guid.NewGuid(), ContactId = created.ContactId });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ContactTypeChangeBlockedException>(
            () => service.ConvertAsync(created.ContactId, Request(ContactType.Organization, created.UpdatedAt)));

        Assert.Equal(1, ex.Blockers.PhotoPersonTags.Count);
        Assert.Equal(ContactConversionCodes.Blocked, ex.Code);
        Assert.DoesNotContain("1", ex.Message);
        context.ChangeTracker.Clear();
        Assert.Equal(ContactType.Person, (await service.Get(created.ContactId))!.Type);
    }

    [Fact]
    public async Task Convert_HomeownerAssociation_IsBlocked_IncludingAnArchivedProperty()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Org("Maple Heights HOA"));
        SeedAssociatedProperty(context, created.ContactId, "Maple St house", archived: true);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ContactTypeChangeBlockedException>(
            () => service.ConvertAsync(created.ContactId, Request(ContactType.Person, created.UpdatedAt)));

        Assert.Equal(1, ex.Blockers.HomeownerAssociations.Count);
        Assert.Equal("Maple St house", Assert.Single(ex.Blockers.HomeownerAssociations.Properties).Name);
    }

    [Fact]
    public async Task Blockers_AreCappedAtFifty_WithAnExactCount()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Many", "Photos"));
        for (var i = 0; i < 51; i++)
        {
            context.PhotoPeople.Add(new PhotoPerson { PhotoId = Guid.NewGuid(), ContactId = created.ContactId });
        }

        await context.SaveChangesAsync();

        var preview = await service.PreviewConversionAsync(created.ContactId, ContactType.Organization, true);

        Assert.Equal(51, preview!.Blockers.PhotoPersonTags.Count);
        Assert.Equal(50, preview.Blockers.PhotoPersonTags.PhotoIds.Count);
        Assert.True(preview.Blockers.PhotoPersonTags.Truncated);
        Assert.False(preview.CanConvert);
    }

    [Fact]
    public async Task Blockers_ApplyOnlyToTheirDirection()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var org = await service.Create(Org("Acme Corp"));
        // A photo tag naming an organization (a stale row) does not block converting it to a person.
        context.PhotoPeople.Add(new PhotoPerson { PhotoId = Guid.NewGuid(), ContactId = org.ContactId });
        await context.SaveChangesAsync();

        var result = await service.ConvertAsync(org.ContactId, Request(ContactType.Person, org.UpdatedAt));

        Assert.Equal(ContactType.Person, result!.Contact.Type);
    }

    [Fact]
    public async Task Put_TypeChange_IsBlockedToo_AndLeavesNoDirtyState()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Diego", "Ramos"));
        context.PhotoPeople.Add(new PhotoPerson { PhotoId = Guid.NewGuid(), ContactId = created.ContactId });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ContactTypeChangeBlockedException>(
            () => service.Update(created.ContactId, Org("Diego Ramos AS") with { ExternalUid = "urn:uuid:replaced" }));

        // The refusal mutated nothing tracked, so an ordinary follow-up save carries only its own change.
        var kept = await service.Update(created.ContactId, Person("Diego", "Ramirez"));
        Assert.Equal(ContactType.Person, kept!.Type);
        Assert.Equal("Ramirez", kept.PersonDetails!.LastName);
        Assert.Equal(created.ExternalUid, kept.ExternalUid);
    }

    [Fact]
    public async Task Put_KeepingTheType_IsNeverBlocked()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Diego", "Ramos"));
        context.PhotoPeople.Add(new PhotoPerson { PhotoId = Guid.NewGuid(), ContactId = created.ContactId });
        await context.SaveChangesAsync();

        var updated = await service.Update(created.ContactId, Person("Diego", "Ramos", displayName: "Diego (walker)"));

        Assert.Equal("Diego (walker)", updated!.DisplayName);
    }

    // ── vCard import (§5.4) ─────────────────────────────────────────────────────

    [Fact]
    public async Task VCardImport_BlockedReType_IsSkippedWithTheFixedReason_AndReportsTheId()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var vCard = VCardService(context);
        var first = await service.Create(Person("Diego", "Ramos") with { ExternalUid = "urn:uuid:one" });
        var second = await service.Create(Person("Ada", "Lovelace") with { ExternalUid = "urn:uuid:two" });
        foreach (var id in new[] { first.ContactId, second.ContactId })
        {
            context.PhotoPeople.Add(new PhotoPerson { PhotoId = Guid.NewGuid(), ContactId = id });
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var content =
            Vcard("UID:urn:uuid:one", "KIND:org", "ORG:Diego Ramos AS", "FN:Diego Ramos AS")
            + Vcard("UID:urn:uuid:two", "KIND:org", "ORG:Lovelace Ltd", "FN:Lovelace Ltd")
            + Vcard("UID:urn:uuid:three", "N:Hopper;Grace;;;", "FN:Grace Hopper");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var outcome = await vCard.ImportWithOutcomeAsync(stream, stream.Length, "text/vcard");

        Assert.Equal(1, outcome.Result.CreatedCount);
        var group = Assert.Single(outcome.Result.Skipped);
        Assert.Equal("Contact type change blocked by existing links.", group.Reason);
        Assert.Equal(2, group.Count);
        Assert.Equal([first.ContactId, second.ContactId], outcome.TypeChangeBlockedContactIds);
        Assert.Empty(outcome.TypeConvertedContactIds);
    }

    [Fact]
    public async Task VCardImport_UnblockedReType_ReportsTheConvertedId()
    {
        await using var context = TestContextFactory.CreateJournal();
        var service = Service(context);
        var created = await service.Create(Person("Diego", "Ramos") with { ExternalUid = "urn:uuid:one" });
        context.ChangeTracker.Clear();

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            Vcard("UID:urn:uuid:one", "KIND:org", "ORG:Diego Ramos AS", "FN:Diego Ramos AS")));
        var outcome = await VCardService(context).ImportWithOutcomeAsync(stream, stream.Length, "text/vcard");

        Assert.Equal(1, outcome.Result.UpdatedCount);
        Assert.Equal([created.ContactId], outcome.TypeConvertedContactIds);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static ContactService Service(OdysseyContext context) =>
        new(context, new NoopContactReferenceGuard());

    private static ContactVCardService VCardService(OdysseyContext context) =>
        new(context, Service(context),
            new ContactAvatarService(context, new FileService(context, new FileValidationService()), new FixedUploadLimits(64L * 1024 * 1024)),
            new FakeImportExportLimitsLookup(),
            NullLogger<ContactVCardService>.Instance);

    private static ContactConversionRequest Request(ContactType target, DateTime expectedUpdatedAt) => new()
    {
        TargetType = target,
        ExpectedUpdatedAt = expectedUpdatedAt,
    };

    private static NewContact Person(string first, string last, string? displayName = null) => new()
    {
        Type = ContactType.Person,
        DisplayName = displayName,
        Archived = false,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewContact Org(string legalName) => new()
    {
        Type = ContactType.Organization,
        Archived = false,
        OrganizationDetails = new OrganizationDetailsDto { LegalName = legalName },
    };

    private static Contact PersonEntity(
        string first, string last, string? middle = null, DateOnly? birth = null, Sex? sex = null,
        string? title = null, string? company = null) => new()
    {
        ExternalUid = "urn:uuid:test",
        NormalizedName = "",
        Type = ContactType.Person,
        PersonDetails = new PersonDetails
        {
            FirstName = first, LastName = last, MiddleName = middle, DateOfBirth = birth, Sex = sex, Title = title, Company = company,
        },
    };

    private static Contact OrgEntity(
        string legalName, DateOnly? established = null, DateOnly? dissolved = null, string? orgNumber = null, string? website = null) => new()
    {
        ExternalUid = "urn:uuid:test",
        NormalizedName = "",
        Type = ContactType.Organization,
        OrganizationDetails = new OrganizationDetails
        {
            LegalName = legalName, EstablishedDate = established, DissolvedDate = dissolved, OrganizationNumber = orgNumber, Website = website,
        },
    };

    private static void SeedAssociatedProperty(OdysseyContext context, Guid associationId, string name, bool archived)
    {
        var propertyId = Guid.NewGuid();
        context.Properties.Add(new Property
        {
            PropertyId = propertyId,
            Name = name,
            Description = "",
            Type = PropertyType.RealEstate,
            CurrencyCode = "NOK",
            Archived = archived ? DateTime.UtcNow : null,
            RealEstateDetails = new RealEstateDetails
            {
                PropertyId = propertyId,
                Kind = RealEstateKind.House,
                HomeownerAssociationId = associationId,
            },
        });
    }

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";
}
