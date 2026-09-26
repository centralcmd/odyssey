// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid, an int
// or a literal this test generated itself — there is no external input — and the raw inserts and deletes
// are deliberately issued outside EF: the CHECKs and foreign keys exist so the engine, not application
// code, is what enforces them.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;
using ContextPropertyEventType = Odyssey.Context.PropertyEventType;
using ContextContractEventType = Odyssey.Context.ContractEventType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #209: the <c>WidenEventsForProperties</c> migration over a database
/// already holding contract events, the two <c>CHECK</c> constraints that contain the shared
/// <c>Events</c> table, and the property cascade and author <c>SET NULL</c> observed firing on MariaDB.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers: the EF InMemory provider enforces no constraint and no
/// foreign key, so these are the only tests in which a raw insert that bypasses the services is refused.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class PropertyEventRelationalTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_property_events";
    private const string Baseline = "_AddProperties";
    private const string Subject = "_WidenEventsForProperties";

    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string Stamp = Anchor.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    /// <summary>
    /// AC 1 — the migration applies over existing contract events: every row becomes a contract row
    /// (<c>OwnerKind = 0</c>) and still reads back through <c>context.ContractEvents</c>, under its old id.
    /// </summary>
    [SkippableFact]
    public async Task The_migration_keeps_existing_contract_events_as_contract_rows()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Baseline);
            await context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
                VALUES ('{contractId}', 'Pre-existing agreement', {(int)ContextContractType.Rental}, '{Stamp}');
                INSERT INTO `ContractEvents` (`ContractEventId`, `ContractId`, `Type`, `Source`, `Title`, `OccurredAt`, `CreatedAtUtc`)
                VALUES ('{eventId}', '{contractId}', {(int)ContextContractEventType.EmailSent}, 1, 'Emailed the landlord', '{Stamp}', '{Stamp}');
                """);
        }

        await using (var context = NewContext())
        {
            await MigrationSeam.MigrateToAsync(context, Subject);

            Assert.Equal(0, await MigrationSeam.CountAsync(context, """
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ContractEvents'
                """));
            Assert.Equal(1, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `Events` WHERE `OwnerKind` = 0"));
            Assert.Equal(0, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `Events` WHERE `OwnerKind` <> 0"));

            var row = await context.ContractEvents.AsNoTracking().SingleAsync();
            Assert.Equal(eventId, row.EventId);
            Assert.Equal(contractId, row.ContractId);
            Assert.Equal(ContextContractEventType.EmailSent, row.Type);
            Assert.Equal(Odyssey.Context.ContractEventSource.System, row.Source);
            Assert.Empty(await context.PropertyEvents.AsNoTracking().ToListAsync());
        }

        await DropAsync();
    }

    /// <summary>AC 2 — a row naming both owners, or neither, fails <c>CK_Events_ExactlyOneOwner</c>.</summary>
    [SkippableFact]
    public async Task A_row_with_both_owners_or_neither_is_refused()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var contractId = await SeedContractAsync(context);
        var propertyId = await SeedPropertyAsync(context);

        await AssertRefusedAsync(context, "CK_Events_ExactlyOneOwner",
            Insert(ownerKind: 1, contractId: contractId, propertyId: propertyId, type: 108));
        await AssertRefusedAsync(context, "CK_Events_ExactlyOneOwner",
            Insert(ownerKind: 0, contractId: contractId, propertyId: propertyId, type: 8));
        await AssertRefusedAsync(context, "CK_Events_ExactlyOneOwner",
            Insert(ownerKind: 1, contractId: null, propertyId: null, type: 108));
        // The discriminator must agree with the owner the row names.
        await AssertRefusedAsync(context, "CK_Events_ExactlyOneOwner",
            Insert(ownerKind: 0, contractId: null, propertyId: propertyId, type: 8));
    }

    /// <summary>AC 3 — each owner's Type is held to its own range.</summary>
    [SkippableFact]
    public async Task A_type_from_the_other_owners_range_is_refused()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var contractId = await SeedContractAsync(context);
        var propertyId = await SeedPropertyAsync(context);

        await AssertRefusedAsync(context, "CK_Events_TypeMatchesOwner",
            Insert(ownerKind: 1, contractId: null, propertyId: propertyId, type: 8));
        await AssertRefusedAsync(context, "CK_Events_TypeMatchesOwner",
            Insert(ownerKind: 0, contractId: contractId, propertyId: null, type: 105));
        await AssertRefusedAsync(context, "CK_Events_TypeMatchesOwner",
            Insert(ownerKind: 1, contractId: null, propertyId: propertyId, type: 200));

        // The legal ones go through, which is what makes the refusals above mean something.
        await context.Database.ExecuteSqlRawAsync(Insert(ownerKind: 1, contractId: null, propertyId: propertyId, type: 105));
        await context.Database.ExecuteSqlRawAsync(Insert(ownerKind: 0, contractId: contractId, propertyId: null, type: 8));
    }

    /// <summary>
    /// AC 3a — a property row that OMITS Type gets the column's DEFAULT 8 and is refused, rather than
    /// passing as a contract Other. And a NULL Type is refused too: TPH maps the column NULLable, so the
    /// CHECK's explicit IS NOT NULL is what closes it.
    /// </summary>
    [SkippableFact]
    public async Task A_property_row_that_omits_or_nulls_its_type_is_refused()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        var propertyId = await SeedPropertyAsync(context);

        await AssertRefusedAsync(context, "CK_Events_TypeMatchesOwner", $"""
            INSERT INTO `Events` (`EventId`, `OwnerKind`, `PropertyId`, `Title`, `OccurredAt`, `CreatedAtUtc`)
            VALUES ('{Guid.NewGuid()}', 1, '{propertyId}', 'No type', '{Stamp}', '{Stamp}');
            """);
        await AssertRefusedAsync(context, "CK_Events_TypeMatchesOwner",
            Insert(ownerKind: 1, contractId: null, propertyId: propertyId, type: null));
    }

    /// <summary>AC 3a — an Other written through EF persists 108 explicitly, and reads back as Other.</summary>
    [SkippableFact]
    public async Task An_other_written_through_ef_persists_one_hundred_and_eight()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using (var context = NewContext())
        {
            var propertyId = await SeedPropertyAsync(context);
            context.PropertyEvents.Add(new PropertyEvent
            {
                PropertyId = propertyId,
                Type = ContextPropertyEventType.Other,
                Title = "Other",
                OccurredAt = Anchor,
                CreatedAtUtc = Anchor,
            });
            await context.SaveChangesAsync();
        }

        await using var verify = NewContext();
        Assert.Equal(1, await MigrationSeam.CountAsync(verify, "SELECT COUNT(*) FROM `Events` WHERE `OwnerKind` = 1 AND `Type` = 108"));
        Assert.Equal(ContextPropertyEventType.Other, (await verify.PropertyEvents.AsNoTracking().SingleAsync()).Type);
    }

    /// <summary>
    /// AC 15 — deleting a property through the engine takes its events and nothing else's: not another
    /// property's, and not a contract's in the same table.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_property_cascades_its_events_only()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using (var context = NewContext())
        {
            var doomed = await SeedPropertyAsync(context);
            var survivor = await SeedPropertyAsync(context);
            var contractId = await SeedContractAsync(context);
            await SeedPropertyEventAsync(context, doomed, "Doomed", null);
            await SeedPropertyEventAsync(context, survivor, "Survivor", null);
            context.ContractEvents.Add(new ContractEvent { ContractId = contractId, Title = "Contract row", OccurredAt = Anchor, CreatedAtUtc = Anchor });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `Properties` WHERE `PropertyId` = '{doomed}'");
        }

        await using var verify = NewContext();
        Assert.Equal("Survivor", (await verify.PropertyEvents.AsNoTracking().SingleAsync()).Title);
        Assert.Equal("Contract row", (await verify.ContractEvents.AsNoTracking().SingleAsync()).Title);
    }

    /// <summary>AC 16 — deleting the author keeps the property event and nulls only the attribution.</summary>
    [SkippableFact]
    public async Task Deleting_the_author_nulls_the_attribution_and_keeps_the_event()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        const string AuthorId = "departing-property-author";
        await using (var context = NewContext())
        {
            await AttributionUsers.EnsureAsync(context, AuthorId);
            var propertyId = await SeedPropertyAsync(context);
            await SeedPropertyEventAsync(context, propertyId, "Roof repaired", AuthorId);

            await context.Database.ExecuteSqlRawAsync($"DELETE FROM `AspNetUsers` WHERE `Id` = '{AuthorId}'");
        }

        await using var verify = NewContext();
        var stored = await verify.PropertyEvents.AsNoTracking().SingleAsync();
        Assert.Equal("Roof repaired", stored.Title);
        Assert.Null(stored.CreatedByUserId);
    }

    /// <summary>The read path's index and the owner key's delete rule, read back from the engine.</summary>
    [SkippableFact]
    public async Task The_property_read_path_index_and_cascade_rule_exist()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await MigrateAsync();

        await using var context = NewContext();
        Assert.Equal(2, await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Events'
              AND INDEX_NAME = 'IX_Events_PropertyId_OccurredAt'
            """));
        Assert.Equal(1, await MigrationSeam.CountAsync(context, """
            SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'Events'
              AND REFERENCED_TABLE_NAME = 'Properties' AND DELETE_RULE = 'CASCADE'
            """));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Insert(int ownerKind, Guid? contractId, Guid? propertyId, int? type) => $"""
        INSERT INTO `Events` (`EventId`, `OwnerKind`, `ContractId`, `PropertyId`, `Type`, `Title`, `OccurredAt`, `CreatedAtUtc`)
        VALUES ('{Guid.NewGuid()}', {ownerKind}, {Sql(contractId)}, {Sql(propertyId)},
                {(type is null ? "NULL" : type.Value.ToString(CultureInfo.InvariantCulture))}, 'Raw', '{Stamp}', '{Stamp}');
        """;

    private static string Sql(Guid? id) => id is null ? "NULL" : $"'{id}'";

    private static async Task AssertRefusedAsync(OdysseyContext context, string constraint, string sql)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains(constraint, failure.ToString(), StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedContractAsync(OdysseyContext context)
    {
        var contract = new Contract { ContractId = Guid.NewGuid(), Name = "Lease", Type = ContextContractType.Rental, CreatedAtUtc = Anchor };
        context.Contracts.Add(contract);
        await context.SaveChangesAsync();
        return contract.ContractId;
    }

    private static async Task<Guid> SeedPropertyAsync(OdysseyContext context)
    {
        var property = new Property
        {
            PropertyId = Guid.NewGuid(),
            Name = "Cabin",
            Description = "Cabin",
            Type = PropertyType.RealEstate,
            CurrencyCode = "USD",
            CreatedAt = Anchor,
            UpdatedAt = Anchor,
            RealEstateDetails = new RealEstateDetails { Kind = RealEstateKind.Cabin },
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();
        return property.PropertyId;
    }

    private static async Task SeedPropertyEventAsync(OdysseyContext context, Guid propertyId, string title, string? authorId)
    {
        context.PropertyEvents.Add(new PropertyEvent
        {
            PropertyId = propertyId,
            Type = ContextPropertyEventType.Repair,
            Title = title,
            OccurredAt = Anchor,
            CreatedByUserId = authorId,
            CreatedAtUtc = Anchor,
        });
        await context.SaveChangesAsync();
    }

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
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
