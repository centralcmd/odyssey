// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid this test
// generated itself, and the engine-level delete is deliberately issued outside the services so the
// ENGINE resolves the foreign key rather than application code.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #217 (AC 12–13): the migration applies over a populated database, the
/// homeowner-association key is <c>SET NULL</c> at the engine, and both the contact-delete path and
/// <see cref="ContactReferenceGuard"/> leave the property and its details intact with the column null.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers: the EF InMemory provider runs no migrations, enforces no
/// foreign keys, and cannot execute <c>ContactReferenceGuard</c>'s set-based statements.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class PropertyHomeownerAssociationRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_property_hoa";
    private const string PreviousMigration = "_WidenEventsForProperties";
    private const string ThisMigration = "_AddRealEstateHomeownerAssociation";

    [SkippableFact]
    public async Task The_migration_adds_a_nullable_set_null_key_and_leaves_existing_rows_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var propertyId = Guid.NewGuid();
        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, PreviousMigration);
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `Properties` (`PropertyId`, `Name`, `Description`, `Type`, `CurrencyCode`, `CreatedAt`, `UpdatedAt`)
                VALUES ('{propertyId}', 'Pre-existing', '', 0, 'USD', '2026-01-01', '2026-01-01');
                INSERT INTO `RealEstateDetails` (`PropertyId`, `Kind`) VALUES ('{propertyId}', 1);
                """);
        }

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, ThisMigration);
            Assert.True(await MigrationSeam.HasRunAsync(context, ThisMigration));
        }

        await using var verify = NewContext();
        var rule = await verify.Database.SqlQuery<string>($"""
            SELECT CONCAT(k.REFERENCED_TABLE_NAME, ':', r.DELETE_RULE) AS Value
            FROM information_schema.KEY_COLUMN_USAGE k
            JOIN information_schema.REFERENTIAL_CONSTRAINTS r
              ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
             AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
            WHERE k.CONSTRAINT_SCHEMA = DATABASE()
              AND k.TABLE_NAME = 'RealEstateDetails'
              AND k.COLUMN_NAME = 'HomeownerAssociationId'
            """).SingleAsync();
        Assert.Equal("Contacts:SET NULL", rule);

        var nonUnique = await verify.Database.SqlQuery<long>($"""
            SELECT NON_UNIQUE AS Value
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'RealEstateDetails'
              AND INDEX_NAME = 'IX_RealEstateDetails_HomeownerAssociationId'
            """).SingleAsync();
        Assert.Equal(1L, nonUnique);

        var stored = await verify.RealEstateDetails.AsNoTracking().SingleAsync(d => d.PropertyId == propertyId);
        Assert.Null(stored.HomeownerAssociationId);
    }

    [SkippableFact]
    public async Task Deleting_the_contact_at_the_engine_nulls_the_link_and_keeps_the_property()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, contactId;
        await using (var context = NewContext())
        {
            contactId = await SeedOrganizationAsync(context);
            propertyId = await SeedFlatAsync(context, contactId);
            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Contacts` WHERE `ContactId` = '{contactId}'");
        }

        await AssertClearedAsync(propertyId, contactId, contactShouldExist: false);
    }

    [SkippableFact]
    public async Task Deleting_the_contact_through_the_service_nulls_the_link_and_keeps_the_property()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, contactId;
        await using (var context = NewContext())
        {
            contactId = await SeedOrganizationAsync(context);
            propertyId = await SeedFlatAsync(context, contactId);
        }

        await using (var context = NewContext())
        {
            await new ContactService(context, new ContactReferenceGuard(context)).Delete(contactId);
        }

        await AssertClearedAsync(propertyId, contactId, contactShouldExist: false);
    }

    [SkippableFact]
    public async Task The_reference_guard_nulls_the_link_before_the_contact_row_goes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        Guid propertyId, contactId;
        await using (var context = NewContext())
        {
            contactId = await SeedOrganizationAsync(context);
            propertyId = await SeedFlatAsync(context, contactId);
            await new ContactReferenceGuard(context).ClearAndCascadeReferencesAsync(contactId);
        }

        await AssertClearedAsync(propertyId, contactId, contactShouldExist: true);
    }

    private async Task AssertClearedAsync(Guid propertyId, Guid contactId, bool contactShouldExist)
    {
        await using var verify = NewContext();
        var details = await verify.RealEstateDetails.AsNoTracking().SingleAsync(d => d.PropertyId == propertyId);
        Assert.Null(details.HomeownerAssociationId);
        Assert.Equal("Storgata 12", details.AddressLine);
        Assert.True(await verify.Properties.AnyAsync(p => p.PropertyId == propertyId));
        Assert.Equal(contactShouldExist, await verify.Contacts.AnyAsync(c => c.ContactId == contactId));
    }

    private static async Task<Guid> SeedOrganizationAsync(OdysseyContext context)
    {
        var contact = new Contact
        {
            ContactId = Guid.NewGuid(),
            ExternalUid = $"urn:uuid:{Guid.NewGuid()}",
            NormalizedName = "STORGATA BORETTSLAG",
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = "Storgata Borettslag" },
        };
        context.Contacts.Add(contact);
        await context.SaveChangesAsync();
        return contact.ContactId;
    }

    // Through the service, so the write path's own validation runs against the real engine too.
    private static async Task<Guid> SeedFlatAsync(OdysseyContext context, Guid associationId) =>
        (await new PropertyService(context, new ContactLookup(context)).Create(new NewProperty
        {
            Name = "Storgata 12, H0302",
            Description = "Flat",
            Type = PropertyType.RealEstate,
            CurrencyCode = "USD",
            RealEstateDetails = new RealEstateDetailsDto
            {
                Kind = RealEstateKind.Apartment,
                AddressLine = "Storgata 12",
                HomeownerAssociationId = associationId,
            },
        }, userId: null)).PropertyId;

    private async Task MigrateAsync()
    {
        await RecreateAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await using var server = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }
}
