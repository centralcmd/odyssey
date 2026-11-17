using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Avatar;
using Odyssey.Core.Locations;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// What an exported contact carries back in: address line 2, a geocoded address's coordinates and time zone,
/// a country written as a name, and the archived state.
/// </summary>
public class ContactVCardRoundTripTests
{
    private static (ContactService Service, ContactVCardService VCard) CreateServices(OdysseyContext context)
    {
        var stores = Odyssey.Testing.FileStorage.TestFileStores.For(context);
        var avatars = new ContactAvatarService(
            context, new FileService(context, new FileValidationService()), new FixedUploadLimits(64L * 1024 * 1024),
            writer: stores.Writer, store: stores.Store, source: stores.Source);
        var service = new ContactService(context, new NoopContactReferenceGuard());
        return (service, new ContactVCardService(
            context, service, avatars, new FakeImportExportLimitsLookup(), NullLogger<ContactVCardService>.Instance));
    }

    private static NewContact Person(string first, string last, bool archived = false) => new()
    {
        Type = ContactType.Person,
        Archived = archived,
        PersonDetails = new PersonDetailsDto { FirstName = first, LastName = last },
    };

    private static NewAddress Address(string line1, string? line2 = null, bool geocoded = false) => new()
    {
        Label = AddressLabel.Home,
        IsPrimary = true,
        Line1 = line1,
        Line2 = line2,
        City = "Oslo",
        PostalCode = "0150",
        CountryCode = "NO",
        Latitude = geocoded ? 59.9139m : null,
        Longitude = geocoded ? 10.7522m : null,
        TimeZoneId = geocoded ? "Europe/Oslo" : null,
        Source = geocoded ? LocationSource.Manual : null,
    };

    private static async Task<VCardImportResult> ImportAsync(ContactVCardService vCard, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await vCard.ImportAsync(stream, stream.Length, "text/vcard", claims: LocationClaims.Trusted);
    }

    private static string Vcard(params string[] lines) =>
        "BEGIN:VCARD\r\nVERSION:4.0\r\n" + string.Join("\r\n", lines) + "\r\nEND:VCARD\r\n";

    private static async Task<string> ExportAllAsync(ContactVCardService vCard)
    {
        using var buffer = new MemoryStream();
        await vCard.ExportManyStreamingAsync(new ContactsQueryParams(), buffer, (_, _) => { });
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ── Line 2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_Line2_GoesInTheExtendedAddressComponent()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Kari", "Nordmann"));
        await service.CreateAddress(created.ContactId, Address("Storgata 1", "Leil. 4"), claims: LocationClaims.Trusted);

        var export = (await vCard.ExportOneAsync(created.ContactId))!.Content;

        Assert.Contains("ADR;TYPE=home;PREF=1:;Leil. 4;Storgata 1;Oslo;;0150;NO", export);
    }

    [Fact]
    public async Task Export_NoLine2_KeepsThePreviousLine()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Kari", "Nordmann"));
        await service.CreateAddress(created.ContactId, Address("Storgata 1"), claims: LocationClaims.Trusted);

        var export = (await vCard.ExportOneAsync(created.ContactId))!.Content;

        Assert.Contains("ADR;TYPE=home;PREF=1:;;Storgata 1;Oslo;;0150;NO", export);
    }

    [Fact]
    public async Task RoundTrip_Line2_ReimportsIntoTheSamePlace()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Kari", "Nordmann"));
        var original = await service.CreateAddress(created.ContactId, Address("Storgata 1", "Leil. 4"), claims: LocationClaims.Trusted);
        var exported = (await vCard.ExportOneAsync(created.ContactId))!.Content;

        var result = await ImportAsync(vCard, exported);

        Assert.Equal(1, result.UpdatedCount);
        var address = Assert.Single((await service.GetAddresses(created.ContactId, claims: LocationClaims.Trusted))!);
        Assert.Equal("Storgata 1", address.Line1);
        Assert.Equal("Leil. 4", address.Line2);
        Assert.Equal(original!.LocationId, address.LocationId);
        Assert.Equal(1, await context.Locations.CountAsync());
    }

    [Fact]
    public async Task Import_MultiLineStreet_SplitsIntoLine1AndLine2()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        await ImportAsync(vCard, Vcard("UID:multi-line", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            @"ADR;TYPE=home:;;Storgata 1\nLeil. 4;Oslo;;0150;NO"));

        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams(), claims: LocationClaims.Trusted)).Items);
        var address = Assert.Single(contact.Addresses);
        Assert.Equal("Storgata 1", address.Line1);
        Assert.Equal("Leil. 4", address.Line2);
    }

    // ── Country names ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Norway", "NO")]
    [InlineData("Norge", "NO")]
    [InlineData("NOR", "NO")]
    [InlineData("no", "NO")]
    [InlineData("Sverige", "SE")]
    [InlineData("Deutschland", "DE")]
    [InlineData("United States of America", "US")]
    public async Task Import_CountryNameOrCode_ResolvesToIsoCode(string country, string expected)
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard("UID:country", "FN:Acme", "ORG:Acme",
            $"ADR;TYPE=work:;;Storgata 1;Oslo;;0150;{country}"));

        Assert.Empty(result.Skipped);
        var contact = Assert.Single((await service.ListAsync(new ContactsQueryParams(), claims: LocationClaims.Trusted)).Items);
        Assert.Equal(expected, Assert.Single(contact.Addresses).CountryCode);
    }

    // ── Coordinates and time zone ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_GeocodedAddress_GroupsGeoAndTz()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Kari", "Nordmann"));
        await service.CreateAddress(created.ContactId, Address("Storgata 1", geocoded: true), claims: LocationClaims.Trusted);

        var single = (await vCard.ExportOneAsync(created.ContactId))!.Content;
        var bulk = await ExportAllAsync(vCard);

        foreach (var export in new[] { single, bulk })
        {
            Assert.Contains("adr1.ADR;TYPE=home;PREF=1:;;Storgata 1;Oslo;;0150;NO", export);
            Assert.Contains("adr1.GEO:geo:59.9139", export);
            Assert.Contains(",10.7522", export);
            Assert.Contains("adr1.TZ:Europe/Oslo", export);
        }
    }

    [Fact]
    public async Task RoundTrip_GeocodedAddress_RecreatesThePlaceWithItsCoordinates()
    {
        await using var source = TestContextFactory.CreateJournal();
        var (sourceService, sourceVCard) = CreateServices(source);
        var created = await sourceService.Create(Person("Kari", "Nordmann"));
        await sourceService.CreateAddress(created.ContactId, Address("Storgata 1", "Leil. 4", geocoded: true), claims: LocationClaims.Trusted);
        var exported = (await sourceVCard.ExportOneAsync(created.ContactId))!.Content;

        await using var target = TestContextFactory.CreateJournal();
        var (_, targetVCard) = CreateServices(target);
        var result = await ImportAsync(targetVCard, exported);

        Assert.Empty(result.Skipped);
        var place = Assert.Single(await target.Locations.ToListAsync());
        Assert.Equal("Storgata 1", place.Line1);
        Assert.Equal("Leil. 4", place.Line2);
        Assert.Equal(59.9139m, place.Latitude);
        Assert.Equal(10.7522m, place.Longitude);
        Assert.Equal("Europe/Oslo", place.TimeZoneId);
    }

    [Fact]
    public async Task Import_GroupedGeoAndTz_GeocodesTheCreatedPlace()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard("UID:geo", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "adr1.ADR;TYPE=home:;;Storgata 1;Oslo;;0150;NO",
            "adr1.GEO:geo:59.9139,10.7522",
            "adr1.TZ:Europe/Oslo"));

        Assert.Empty(result.Skipped);
        var place = Assert.Single(await context.Locations.ToListAsync());
        Assert.Equal(59.9139m, place.Latitude);
        Assert.Equal(10.7522m, place.Longitude);
        Assert.Equal("Europe/Oslo", place.TimeZoneId);
    }

    [Fact]
    public async Task Import_GeoWithoutTz_KeepsTheAddress_DropsTheCoordinates_AndReportsIt()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard("UID:geo-no-tz", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "adr1.ADR;TYPE=home:;;Storgata 1;Oslo;;0150;NO",
            "adr1.GEO:geo:59.9139,10.7522"));

        Assert.Equal(1, result.AddressesLinked);
        Assert.StartsWith("Address coordinates dropped", Assert.Single(result.Skipped).Reason);
        var place = Assert.Single(await context.Locations.ToListAsync());
        Assert.Null(place.Latitude);
    }

    [Fact]
    public async Task Import_UngroupedGeo_IsNotAppliedToAnAddress()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (_, vCard) = CreateServices(context);

        var result = await ImportAsync(vCard, Vcard("UID:geo-contact", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "ADR;TYPE=home:;;Storgata 1;Oslo;;0150;NO",
            "GEO:geo:59.9139,10.7522",
            "TZ:Europe/Oslo"));

        Assert.Empty(result.Skipped);
        Assert.Null(Assert.Single(await context.Locations.ToListAsync()).Latitude);
    }

    [Fact]
    public async Task Import_GeoOnAnExistingPlace_NeverOverwritesItsCoordinates()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var other = await service.Create(Person("Ola", "Nordmann"));
        await service.CreateAddress(other.ContactId, Address("Storgata 1", geocoded: true), claims: LocationClaims.Trusted);

        await ImportAsync(vCard, Vcard("UID:geo-existing", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "adr1.ADR;TYPE=home:;;Storgata 1;Oslo;;0150;NO",
            "adr1.GEO:geo:10,10",
            "adr1.TZ:Europe/Oslo"));

        var place = Assert.Single(await context.Locations.ToListAsync());
        Assert.Equal(59.9139m, place.Latitude);
    }

    // ── Archived ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ArchivedContact_CarriesTheArchiveMarker()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var archived = await service.Create(Person("Kari", "Nordmann", archived: true));
        var active = await service.Create(Person("Ola", "Nordmann"));

        Assert.Contains("X-ODYSSEY-ARCHIVED:", (await vCard.ExportOneAsync(archived.ContactId))!.Content);
        Assert.DoesNotContain("X-ODYSSEY-ARCHIVED", (await vCard.ExportOneAsync(active.ContactId))!.Content);
    }

    [Fact]
    public async Task Import_ArchiveMarker_CreatesAnArchivedContact()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        await ImportAsync(vCard, Vcard("UID:archived", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "X-ODYSSEY-ARCHIVED:20260101T120000Z"));

        var archived = Assert.Single((await service.ListAsync(new ContactsQueryParams(), claims: LocationClaims.Trusted)).Items).Archived;
        Assert.Equal(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), archived);
    }

    [Fact]
    public async Task RoundTrip_ArchivedContact_KeepsItsArchiveDate()
    {
        await using var source = TestContextFactory.CreateJournal();
        var (sourceService, sourceVCard) = CreateServices(source);
        var created = await sourceService.Create(Person("Kari", "Nordmann", archived: true));
        var original = (await sourceService.Get(created.ContactId))!.Archived!.Value;
        var exported = (await sourceVCard.ExportOneAsync(created.ContactId))!.Content;

        await using var target = TestContextFactory.CreateJournal();
        var (targetService, targetVCard) = CreateServices(target);
        await ImportAsync(targetVCard, exported);

        var imported = Assert.Single((await targetService.ListAsync(new ContactsQueryParams(), claims: LocationClaims.Trusted)).Items).Archived;
        Assert.Equal(original.AddTicks(-(original.Ticks % TimeSpan.TicksPerSecond)), imported);
    }

    [Fact]
    public async Task Import_UnparseableArchiveDate_StillArchives()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);

        await ImportAsync(vCard, Vcard("UID:archived-bad", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "X-ODYSSEY-ARCHIVED:soon"));

        Assert.NotNull(Assert.Single((await service.ListAsync(new ContactsQueryParams(), claims: LocationClaims.Trusted)).Items).Archived);
    }

    [Fact]
    public async Task Import_ArchiveMarker_OnAnUpdate_KeepsTheTargetsStatus()
    {
        await using var context = TestContextFactory.CreateJournal();
        var (service, vCard) = CreateServices(context);
        var created = await service.Create(Person("Kari", "Nordmann"));
        var uid = (await service.Get(created.ContactId))!.ExternalUid;

        await ImportAsync(vCard, Vcard($"UID:{uid}", "FN:Kari Nordmann", "N:Nordmann;Kari;;;",
            "X-ODYSSEY-ARCHIVED:20260101T120000Z"));

        Assert.Null((await service.Get(created.ContactId))!.Archived);
    }
}
